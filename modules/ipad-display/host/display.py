"""Capture a single, identified, extended Windows monitor."""

import asyncio
import ctypes
import threading
import time
import logging
from concurrent.futures import ThreadPoolExecutor
from ctypes import wintypes
from fractions import Fraction

import av
from aiortc import VideoStreamTrack
from aiortc.mediastreams import MediaStreamError
from mss import MSS


# E-DDC product ID used by VirtualDrivers/Virtual-Display-Driver.
VIRTUAL_EDIDS = {"MTT1337"}
CLOCK = 90000


def displays():
    # MSS caches the initial topology on each public MSS instance.
    with MSS() as probe:
        return probe.monitors[1:]


def selected_display(selector, monitors):
    primary = [m for m in monitors if m.get("is_primary")]
    if len(primary) != 1:
        return None
    eligible = []
    for monitor in monitors:
        identity = monitor.get("unique_id", "")
        if monitor.get("is_primary", True) or not identity:
            continue
        components = identity.upper().split("#")
        automatic = len(components) > 1 and components[1] in VIRTUAL_EDIDS
        if (selector == "auto" and automatic) or identity.casefold() == selector.casefold():
            # A duplicate or overlapping desktop is not an independent display.
            main = primary[0]
            if (monitor["left"] < main["left"] + main["width"]
                    and main["left"] < monitor["left"] + monitor["width"]
                    and monitor["top"] < main["top"] + main["height"]
                    and main["top"] < monitor["top"] + monitor["height"]):
                continue
            eligible.append(monitor)
    return eligible[0] if len(eligible) == 1 else None


class CursorInfo(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD), ("flags", wintypes.DWORD),
        ("hCursor", wintypes.HANDLE), ("ptScreenPos", wintypes.POINT),
    ]


def draw_pointer(shot):
    # The GDI capture omits the hardware pointer. Draw a small outlined arrow
    # only when Windows reports a visible cursor on this monitor.
    cursor = CursorInfo()
    cursor.cbSize = ctypes.sizeof(cursor)
    if not ctypes.windll.user32.GetCursorInfo(ctypes.byref(cursor)) or cursor.flags != 1:
        return
    x = cursor.ptScreenPos.x - shot.left
    y = cursor.ptScreenPos.y - shot.top
    if not (0 <= x < shot.width and 0 <= y < shot.height):
        return
    for row, size in enumerate((1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 6, 4, 2, 1)):
        if y + row >= shot.height:
            break
        for col in range(min(size, shot.width - x)):
            start = 4 * ((y + row) * shot.width + x + col)
            shade = 25 if col == 0 or col == size - 1 else 255
            shot.raw[start:start + 4] = bytes((shade, shade, shade, 255))


class DesktopTrack(VideoStreamTrack):
    def __init__(self, identity, fps=30):
        super().__init__()
        self.identity = identity  # None is used exclusively by explicit demo mode.
        self.fps = fps
        self.max_size = (1280, 720)
        self.video_mode = None
        self.started = time.monotonic()
        self.next_frame = self.started
        self.capture = None
        self.fast = True
        self.still_worker = None
        self.worker = None
        self.halt = threading.Event()
        self.frame_lock = threading.Lock()
        self.latest = None
        self.ready = asyncio.Event()

    def stop(self):
        if self.readyState == "ended":
            return
        super().stop()
        self.halt.set()
        self.ready.set()
        if self.still_worker is not None:
            self.still_worker.submit(self.close_capture)
            self.still_worker.shutdown(wait=False)
            self.still_worker = None
        elif self.worker is None:
            self.close_capture()

    def close_capture(self):
        if self.capture is not None:
            self.capture.close()
            self.capture = None

    async def still(self):
        if self.still_worker is None:
            self.still_worker = ThreadPoolExecutor(1, thread_name_prefix="Virtual display JPEG capture")
        return await asyncio.get_running_loop().run_in_executor(self.still_worker, self.frame)

    def frame(self):
        if self.identity is None:
            frame = av.VideoFrame(640, 360, "yuv420p")
            tick = int((time.monotonic() - self.started) * 4) % 8
            for plane_index, plane in enumerate(frame.planes):
                row = bytes(32 + ((x * 8 // plane.width + tick) % 8) * 24
                            for x in range(plane.width))
                if plane_index:
                    row = bytes([112 if plane_index == 1 else 160]) * plane.width
                plane.update((row + bytes(plane.line_size - plane.width)) * plane.height)
            return frame
        monitor = selected_display(self.identity, displays())
        if monitor is None:
            raise MediaStreamError("Selected display disconnected or became primary")
        frame = self.read_bgra(monitor)
        # Discard captured pixels if Windows changed this monitor's geometry or
        # identity while BitBlt was executing.
        if selected_display(self.identity, displays()) != monitor:
            raise MediaStreamError("Display topology changed")
        scale = min(1.0, self.max_size[0] / frame.width, self.max_size[1] / frame.height)
        width = max(2, round(frame.width * scale / 2) * 2)
        height = max(2, round(frame.height * scale / 2) * 2)
        return frame.reformat(width, height, format="yuv420p")

    def read_bgra(self, monitor):
        if self.fast:
            try:
                if self.capture is None:
                    from fast_capture import FastCapture
                    self.capture = FastCapture(monitor)
                return self.capture.grab(monitor)
            except (ImportError, RuntimeError, OSError, ValueError) as exc:
                self.close_capture()
                if selected_display(self.identity, displays()) != monitor:
                    raise MediaStreamError("Display changed before capture fallback") from exc
                logging.getLogger("ipad-display").warning("Windows Graphics Capture unavailable (%s); using GDI",
                                                          type(exc).__name__)
                self.fast = False
        if self.capture is None:
            self.capture = MSS()
        shot = self.capture.grab(monitor)
        draw_pointer(shot)  # WGC already captures the system cursor.
        frame = av.VideoFrame(shot.width, shot.height, "bgra")
        plane = frame.planes[0]
        stride = shot.width * 4
        if stride == plane.line_size:
            plane.update(shot.raw)
        else:
            padded = bytearray(plane.buffer_size)
            pixels = memoryview(shot.raw)
            for row in range(shot.height):
                start = row * plane.line_size
                padded[start:start + stride] = pixels[row * stride:(row + 1) * stride]
            plane.update(padded)
        return frame

    def produce(self, loop):
        # Capture overlaps RTP encoding. A slow consumer takes the newest
        # completed frame, with at most one waiting frame and no stale backlog.
        try:
            while not self.halt.is_set():
                now = time.monotonic()
                frame = self.frame()
                frame.pts = round((now - self.started) * CLOCK)
                frame.time_base = Fraction(1, CLOCK)
                frame.opaque = self.video_mode
                with self.frame_lock:
                    self.latest = frame
                loop.call_soon_threadsafe(self.ready.set)
                self.next_frame = max(self.next_frame + 1 / self.fps, time.monotonic())
                remaining = self.next_frame - time.monotonic()
                while remaining > 0 and not self.halt.is_set():
                    # CPython's Windows sleep uses a high-resolution timer.
                    # Event.wait(3 ms) can instead consume a whole 15.6 ms tick.
                    time.sleep(min(remaining, 0.05))
                    remaining = self.next_frame - time.monotonic()
        except Exception as exc:
            with self.frame_lock:
                self.latest = exc
            loop.call_soon_threadsafe(self.ready.set)
        finally:
            self.close_capture()

    async def recv(self):
        if self.worker is None and self.readyState == "live":
            self.worker = threading.Thread(target=self.produce, args=(asyncio.get_running_loop(),),
                                           name="Virtual display capture", daemon=True)
            self.worker.start()
        while self.readyState == "live":
            await self.ready.wait()
            self.ready.clear()
            with self.frame_lock:
                current, self.latest = self.latest, None
            if isinstance(current, Exception):
                self.stop()
                raise MediaStreamError("The selected display is unavailable") from current
            if current is not None:
                return current
        raise MediaStreamError
