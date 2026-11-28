"""Temporarily attach or detach only this installation's Windows virtual panel."""

import argparse
import ctypes as C
from ctypes import wintypes as W
import json
import msvcrt
import os
from pathlib import Path
import sys
import time
import winreg

OWNER = Path(os.environ["ProgramFiles"]) / "iPad WiFi Display Driver" / "owned-device.json"
SESSION = Path(os.environ["LOCALAPPDATA"]) / "iPadWiFiDisplay" / "session"
LAYOUT = SESSION / "layout.json"
ACTIVE, PRIMARY = 1, 4
POSITION, WIDTH, HEIGHT, FREQUENCY, ORIENTATION = 0x20, 0x80000, 0x100000, 0x400000, 0x80


class Device(C.Structure):
    _fields_ = [("size", W.DWORD), ("name", W.WCHAR * 32), ("label", W.WCHAR * 128),
                ("flags", W.DWORD), ("identity", W.WCHAR * 128), ("key", W.WCHAR * 128)]


class Mode(C.Structure):
    _fields_ = [("device", W.WCHAR * 32), ("spec", W.WORD), ("version", W.WORD),
                ("size", W.WORD), ("extra", W.WORD), ("fields", W.DWORD),
                ("x", W.LONG), ("y", W.LONG), ("orientation", W.DWORD), ("fixed", W.DWORD),
                ("unused", W.WORD * 5), ("form", W.WCHAR * 32), ("dpi", W.WORD),
                ("bpp", W.DWORD), ("width", W.DWORD), ("height", W.DWORD),
                ("flags", W.DWORD), ("hz", W.DWORD), ("other", W.DWORD * 8)]

    def __init__(self):
        super().__init__()
        self.size = C.sizeof(self)


user = C.windll.user32
user.EnumDisplayDevicesW.argtypes = [W.LPCWSTR, W.DWORD, C.POINTER(Device), W.DWORD]
user.EnumDisplaySettingsExW.argtypes = [W.LPCWSTR, W.DWORD, C.POINTER(Mode), W.DWORD]
user.ChangeDisplaySettingsExW.argtypes = [W.LPCWSTR, C.POINTER(Mode), W.HWND, W.DWORD, W.LPVOID]
user.SetDisplayConfig.argtypes = [W.UINT, W.LPVOID, W.UINT, W.LPVOID, W.UINT]
GEOMETRY = ("x", "y", "width", "height", "hz", "orientation")


def devices(parent=None):
    index = 0
    while True:
        item = Device()
        item.size = C.sizeof(item)
        if not user.EnumDisplayDevicesW(parent, index, C.byref(item), 1):
            return
        yield item
        index += 1


def current(device):
    mode = Mode()
    if not user.EnumDisplaySettingsExW(device.name, 0xFFFFFFFF, C.byref(mode), 0):
        raise RuntimeError("Cannot read an active display's settings")
    return {field: getattr(mode, field) for field in GEOMETRY}


def supported(target):
    modes = {}
    index = 0
    while True:
        mode = Mode()
        if not user.EnumDisplaySettingsExW(target.name, index, C.byref(mode), 0):
            break
        if mode.bpp == 32:
            modes.setdefault((mode.width, mode.height, mode.hz), mode)
        index += 1
    return modes


def mode_request(target, mode, x, y, test=False):
    mode.fields = POSITION | WIDTH | HEIGHT | FREQUENCY
    mode.x, mode.y = x, y
    # CDS_UPDATEREGISTRY changes only this user's named display mode and
    # allows Windows to restore it on the next Extend; never use CDS_GLOBAL.
    return user.ChangeDisplaySettingsExW(target.name, C.byref(mode), None, 2 if test else 1, None)


def video_mode(action, desired):
    target, other = virtual_device()
    if not target.flags & ACTIVE:
        raise RuntimeError("Attach the owned virtual screen before configuring its mode")
    available = supported(target)
    original = current(target)
    result = {"available": True, "device": target.name, "current": original,
              "modes": [dict(width=w, height=h, hz=f) for w, h, f in sorted(available)]}
    if action == "modes":
        return result
    old = tuple(original[k] for k in ("width", "height", "hz"))
    if desired not in available:
        raise RuntimeError("Requested mode is not available on the owned virtual display")
    if desired == old:
        return result
    if old not in available or original["orientation"] != 0:
        raise RuntimeError("Cannot safely resize this virtual display arrangement")
    x, y = original["x"], original["y"]
    width, height, _ = desired
    for panel in other.values():
        if original["x"] + original["width"] == panel["x"]:
            x = panel["x"] - width
        if original["y"] + original["height"] == panel["y"]:
            y = panel["y"] - height
    for panel in other.values():
        if (x < panel["x"] + panel["width"] and panel["x"] < x + width
                and y < panel["y"] + panel["height"] and panel["y"] < y + height):
            raise RuntimeError("Requested size would overlap another display")
    for probe in (True, False):
        status = mode_request(target, available[desired], x, y, test=probe)
        if status != 0:
            raise RuntimeError(f"Windows rejected the virtual display mode ({status})")
    for _ in range(15):
        active, others_now = virtual_device()
        now = current(active)
        if (others_now == other and (now["width"], now["height"], now["hz"]) == desired
                and (now["x"], now["y"]) == (x, y)):
            result["current"] = now
            return result
        time.sleep(0.1)
    # Only issue a rollback to the verified, still-owned adapter. Other
    # adapters are never named in a display change.
    active, _ = virtual_device()
    rollback = mode_request(active, available[old], original["x"], original["y"])
    raise RuntimeError(f"Windows did not confirm the requested layout; virtual-mode rollback returned {rollback}")


def virtual_device():
    owner = json.loads(OWNER.read_text(encoding="utf-8-sig"))["InstanceId"]
    if not owner.upper().startswith("ROOT\\DISPLAY\\"):
        raise RuntimeError("No owned virtual adapter")
    path = "SYSTEM\\CurrentControlSet\\Enum\\" + owner
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path) as key:
        hardware, _ = winreg.QueryValueEx(key, "HardwareID")
        if "ROOT\\MTTVDD" not in [item.upper() for item in hardware]:
            raise RuntimeError("The saved adapter is no longer the virtual driver")
    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path + "\\Device Parameters") as key:
        video_id, _ = winreg.QueryValueEx(key, "VideoID")
    all_devices = list(devices())
    expected = "\\control\\video\\" + video_id.lower() + "\\"
    targets = [d for d in all_devices if expected in d.key.lower()]
    if len(targets) != 1 or targets[0].flags & PRIMARY:
        raise RuntimeError("Virtual display is primary, unavailable or ambiguous")
    target = targets[0]
    children = list(devices(target.name))
    if (len(children) > 1 or any("#MTT1337#" not in c.identity.upper() for c in children)
            or (target.flags & ACTIVE and not children)):
        raise RuntimeError("Unexpected virtual monitor identity")
    other = [d for d in all_devices if d.flags & ACTIVE and d.name != target.name]
    if sum(bool(d.flags & PRIMARY) for d in other) != 1:
        raise RuntimeError("No independent primary screen")
    layout = {d.key: dict(current(d), primary=bool(d.flags & PRIMARY)) for d in other}
    return target, layout


def manage(action):
    target, other = virtual_device()
    attached = bool(target.flags & ACTIVE)
    saved = json.loads(LAYOUT.read_text()) if LAYOUT.exists() else {}
    can_restore = saved.get("target") == target.key and saved.get("other") == other
    # The IDD on this Windows build accepts target-specific detachment, but
    # requires CCD to attach again. Only manage a known pair where the Extend
    # topology has exactly one possible physical + virtual combination.
    panels = [(d.name, m.identity) for d in devices() for m in devices(d.name)]
    single_pair = (len(other) == 1 and len(panels) == 2
                   and sum(d == target.name and "#MTT1337#" in m.upper() for d, m in panels) == 1
                   and len(set(d for d, m in panels)) == 2)
    result = dict(available=attached or (single_pair and can_restore), attached=attached,
                  managed=single_pair, device=target.name)
    if (action == "status" or (action == "attach") == attached):
        return result
    if not single_pair:
        raise RuntimeError("Automatic display changes require one physical screen and one owned virtual panel")
    if action == "detach":
        snapshot = {"target": target.key, "other": other, "mode": current(target)}
        tmp = LAYOUT.with_suffix(".tmp")
        tmp.write_text(json.dumps(snapshot), encoding="utf-8")
        tmp.replace(LAYOUT)
        mode = Mode()
        mode.fields = POSITION | WIDTH | HEIGHT
        for flags in (2, 0):  # CDS_TEST, then dynamically detach just this adapter.
            status = user.ChangeDisplaySettingsExW(target.name, C.byref(mode), None, flags, None)
            if status:
                raise RuntimeError(f"{action} rejected by Windows ({status})")
    else:
        if not can_restore:
            raise RuntimeError("The matching previous desktop arrangement is unavailable")
        # Restore Windows' last extended layout for this exact monitor pair.
        status = user.SetDisplayConfig(0, None, 0, None, 0x84)  # SDC_TOPOLOGY_EXTEND | SDC_APPLY
        if status:
            raise RuntimeError(f"{action} rejected by Windows ({status})")
    for _ in range(12):
        actual, now_other = virtual_device()
        if now_other != other:
            raise RuntimeError("Other display layout changed during operation")
        if bool(actual.flags & ACTIVE) == (action == "attach"):
            result.update(available=True, attached=(action == "attach"))
            if action == "attach" and current(actual) != saved["mode"]:
                result["warning"] = "Windows selected a different virtual display mode"
            return result
        time.sleep(0.1)
    raise RuntimeError("Windows did not apply the requested layout")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["status", "attach", "detach", "mode", "modes"])
    parser.add_argument("--width", type=int)
    parser.add_argument("--height", type=int)
    parser.add_argument("--hz", type=int)
    args = parser.parse_args()
    try:
        if args.action == "modes":
            result = video_mode("modes", None)
        elif args.action == "status":
            result = manage(args.action)
        else:
            SESSION.mkdir(parents=True, exist_ok=True)
            with (SESSION / "layout.lock").open("a+b") as lock:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                if args.action == "mode":
                    result = video_mode(args.action, (args.width, args.height, args.hz))
                else:
                    result = manage(args.action)
        print(json.dumps(result))
        return 0
    except (OSError, RuntimeError, KeyError, ValueError) as exc:
        print(json.dumps(dict(available=False, error=str(exc))))
        return 1


if __name__ == "__main__":
    sys.exit(main())
