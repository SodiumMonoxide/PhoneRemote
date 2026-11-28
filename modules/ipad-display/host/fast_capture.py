"""Windows Graphics Capture for a single, verified, non-primary display."""

import ctypes as C
from ctypes import wintypes as W
from contextlib import ExitStack
import time

import av
from winrt.runtime import ApartmentType, init_apartment, uninit_apartment
from winrt.windows.foundation import AsyncStatus
from winrt.windows.graphics.capture import Direct3D11CaptureFramePool
from winrt.windows.graphics.capture.interop import create_for_monitor
from winrt.windows.graphics.directx import DirectXPixelFormat
from winrt.windows.graphics.directx.direct3d11.interop import create_direct3d11_device
from winrt.windows.graphics.imaging import SoftwareBitmap


# The projection caches WinRT activation factories until Python unloads its
# native modules. Keep the MTA alive for that lifetime, even when the last
# capture worker exits between iPad sessions; Windows releases it at exit.
_process_mta = C.c_void_p()
_mta_result = C.windll.ole32.CoIncrementMTAUsage(C.byref(_process_mta))
if _mta_result < 0:
    raise OSError(f"Could not initialize the capture MTA ({_mta_result})")


class MonitorInfo(C.Structure):
    _fields_ = [("size", W.DWORD), ("rect", W.RECT), ("work", W.RECT),
                ("flags", W.DWORD), ("device", W.WCHAR * 32)]


class DisplayDevice(C.Structure):
    _fields_ = [("size", W.DWORD), ("name", W.WCHAR * 32), ("label", W.WCHAR * 128),
                ("flags", W.DWORD), ("identity", W.WCHAR * 128), ("key", W.WCHAR * 128)]


# A separate DLL instance avoids mutating ctypes signatures used by MSS.
user = C.WinDLL("user32")
user.MonitorFromPoint.argtypes = (W.POINT, W.DWORD)
user.MonitorFromPoint.restype = W.HANDLE
user.GetMonitorInfoW.argtypes = (W.HANDLE, C.POINTER(MonitorInfo))
user.EnumDisplayDevicesW.argtypes = (W.LPCWSTR, W.DWORD, C.POINTER(DisplayDevice), W.DWORD)


def monitor_handle(monitor):
    if monitor.get("is_primary", True) or not monitor.get("unique_id"):
        raise ValueError("Only an identified secondary display can be captured")
    x, y, width, height = (monitor[k] for k in ("left", "top", "width", "height"))
    # MONITOR_DEFAULTTONULL never substitutes a different monitor.
    handle = user.MonitorFromPoint(W.POINT(x + width // 2, y + height // 2), 0)
    info = MonitorInfo()
    info.size = C.sizeof(info)
    if not handle or not user.GetMonitorInfoW(handle, C.byref(info)) or info.flags & 1:
        raise RuntimeError("Selected display is absent or became primary")
    rect = info.rect
    if (rect.left, rect.top, rect.right, rect.bottom) != (x, y, x + width, y + height):
        raise RuntimeError("Selected display geometry changed")
    child = DisplayDevice()
    child.size = C.sizeof(child)
    if (not user.EnumDisplayDevicesW(info.device, 0, C.byref(child), 1)
            or child.identity.casefold() != monitor["unique_id"].casefold()
            or user.EnumDisplayDevicesW(info.device, 1, C.byref(child), 1)):
        raise RuntimeError("Selected display identity changed or is ambiguous")
    return handle


class FastCapture:
    """Construct, grab and close on the same capture worker thread.

    grab() returns a BGRA source frame. Treat it as read-only, since a static
    desktop can yield the previous frame until WGC reports new pixels.
    """

    def __init__(self, monitor):
        self.handle = monitor_handle(monitor)
        self.identity = monitor["unique_id"]
        self.width, self.height = monitor["width"], monitor["height"]
        self.latest = None
        self.resources = ExitStack()
        try:
            init_apartment(ApartmentType.MULTI_THREADED)
            self.resources.callback(uninit_apartment)
            self.item = create_for_monitor(self.handle)
            if (self.item.size.width, self.item.size.height) != (self.width, self.height):
                raise RuntimeError("Capture item resolution differs from the selected display")
            self.device = create_direct3d11_device()
            self.resources.callback(self.device.close)
            self.pool = Direct3D11CaptureFramePool.create_free_threaded(
                self.device, DirectXPixelFormat.B8_G8_R8_A8_UINT_NORMALIZED, 1, self.item.size)
            self.resources.callback(self.pool.close)
            self.session = self.pool.create_capture_session(self.item)
            self.resources.callback(self.session.close)
            self.session.is_cursor_capture_enabled = True
            self.session.start_capture()
        except Exception:
            self.close()
            raise

    def grab(self, monitor):
        if monitor["unique_id"] != self.identity or monitor_handle(monitor) != self.handle:
            raise RuntimeError("The capture source changed")
        frame = self.pool.try_get_next_frame()
        if frame is None and self.latest is None:
            deadline = time.monotonic() + 1.5
            while frame is None and time.monotonic() < deadline:
                time.sleep(.003)
                frame = self.pool.try_get_next_frame()
            if frame is None:
                raise RuntimeError("Windows Graphics Capture did not produce its first frame")
        if frame is not None:
            with frame:
                if (frame.content_size.width, frame.content_size.height) != (self.width, self.height):
                    raise RuntimeError("Captured monitor mode changed")
                pending = SoftwareBitmap.create_copy_from_surface_async(frame.surface)
                if pending.wait(1.0) != AsyncStatus.COMPLETED:
                    raise RuntimeError("Windows Graphics Capture surface readback failed")
                with pending.get_results() as bitmap:
                    output = av.VideoFrame(self.width, self.height, "bgra")
                    plane = output.planes[0]
                    if plane.line_size != self.width * 4:
                        raise ValueError("Unaligned pixel width; use the GDI path")
                    bitmap.copy_to_buffer(memoryview(plane))
                self.latest = output
        if monitor_handle(monitor) != self.handle:
            raise RuntimeError("Selected display changed during capture")
        return self.latest

    def close(self):
        self.latest = self.item = self.session = self.pool = self.device = None
        self.resources.close()
