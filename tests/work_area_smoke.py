"""Check panel and mini-toolbar bounds on every connected monitor using a disposable profile."""
import argparse
import ctypes
from pathlib import Path
import tempfile
import time

import win32api
import win32con
import win32gui

from mini_toolbar_smoke import choose_item, controller


def rect(hwnd):
    return win32gui.GetWindowRect(hwnd)


def assert_settled(hwnd, expected, label):
    deadline = time.monotonic() + 4
    while time.monotonic() < deadline:
        if rect(hwnd) == expected:
            time.sleep(.09)  # Allow queued layout / DPI events to settle.
            if rect(hwnd) == expected:
                return
        time.sleep(.04)
    raise AssertionError(f"{label}: expected {expected}, got {rect(hwnd)}")


def move(hwnd, x, y):
    flags = win32con.SWP_NOSIZE | win32con.SWP_NOZORDER | win32con.SWP_NOACTIVATE
    win32gui.SetWindowPos(hwnd, 0, x, y, 0, 0, flags)


def dimensions(hwnd):
    x, y, right, bottom = rect(hwnd)
    return right - x, bottom - y


def check_display_notifications(hwnd, area):
    left, top, right, bottom = area
    move(hwnd, left + (right - left) // 4, top + (bottom - top) // 4)
    time.sleep(.32)
    expected = rect(hwnd)
    width, height = dimensions(hwnd)
    flags = win32con.SWP_NOMOVE | win32con.SWP_NOZORDER | win32con.SWP_NOACTIVATE
    for message in (win32con.WM_SETTINGCHANGE, win32con.WM_DISPLAYCHANGE):
        # Perturb only the disposable panel. Sending the notification to this HWND
        # exercises the same recovery path without touching any system setting.
        win32gui.SetWindowPos(hwnd, 0, 0, 0, width + 8, height + 8, flags)
        assert dimensions(hwnd) != (width, height)
        win32gui.SendMessageTimeout(hwnd, message, 0, 0, win32con.SMTO_ABORTIFHUNG, 2000)
        assert_settled(hwnd, expected, hex(message))
    print("PASS work-area and display-change notifications", flush=True)


def check_edges(hwnd, area, label):
    left, top, right, bottom = area
    # Move into this monitor first. Let any DPI-driven resize finish before checking
    # the outer HWND against its work area, in physical desktop coordinates.
    move(hwnd, left + (right - left) // 4, top + (bottom - top) // 4)
    time.sleep(.32)
    w, h = dimensions(hwnd)
    assert w <= right - left and h <= bottom - top, (label, area, w, h)
    mid_x, mid_y = (left + right - w) // 2, (top + bottom - h) // 2
    for edge, x, y, ex, ey in [
        ("left", left - w // 4, mid_y, left, mid_y),
        ("right", right - w + w // 4, mid_y, right - w, mid_y),
        ("top", mid_x, top - h // 4, mid_x, top),
        ("bottom", mid_x, bottom - h + h // 4, mid_x, bottom - h),
        ("corner", right - w + w // 4, bottom - h + h // 4, right - w, bottom - h),
    ]:
        move(hwnd, x, y)
        assert_settled(hwnd, (ex, ey, ex + w, ey + h), f"{label}, {edge}")
    print("PASS", label, "all four edges and bottom-right corner", flush=True)


def test(exe, profile):
    for filename in ("shortcut.json", "passthrough-shortcut.json"):
        (profile / filename).write_text("null", encoding="utf8")
    # Pywinauto and Win32 must both see physical coordinates on mixed-DPI desktops.
    ctypes.windll.user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))
    monitors = [win32api.GetMonitorInfo(monitor) for monitor, _, _ in win32api.EnumDisplayMonitors(None, None)]
    assert monitors
    with controller(exe, profile) as panel:
        hwnd = panel.handle
        def control(name):
            return panel.child_window(auto_id=name, found_index=0).wrapper_object()
        control("SettingsTab").iface_selection_item.Select()
        for monitor in monitors:
            label, area = monitor["Device"], monitor["Work"]
            print("Monitor", label, "screen", monitor["Monitor"], "work", area, flush=True)
            check_edges(hwnd, area, label + " full panel")
            check_display_notifications(hwnd, area)
            for skin, expand in (("经典", "ExpandButton"), ("初始图标", "ImeExpandButton"), ("输入法", "ImeExpandButton")):
                choose_item(panel, control("MiniStyleBox"), skin)
                control("CollapseButton").iface_invoke.Invoke()
                control(expand)
                check_edges(hwnd, area, f"{label} {skin}")
                control(expand).iface_invoke.Invoke()
                control("MiniStyleBox")
                # A panel expanded at the bottom-right must move far enough back into view.
                x, y, right, bottom = area
                until = time.monotonic() + 4
                while True:
                    a, b, c, d = rect(hwnd)
                    if x <= a < c <= right and y <= b < d <= bottom:
                        break
                    if time.monotonic() > until:
                        raise AssertionError((label, skin, rect(hwnd), area))
                    time.sleep(.05)
                print("PASS", skin, "expands inside", area, flush=True)

        # An Invoke callback can still be on WinUI's native focus stack when its button
        # disappears. Exercise repeated input-method frame changes on this same window.
        for _ in range(12):
            control("CollapseButton").iface_invoke.Invoke()
            control("ImeExpandButton").iface_invoke.Invoke()
            assert control("MiniStyleBox").selected_text() == "输入法"
        print("PASS repeated collapse and expand preserves Settings", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="sell-fish-work-area-") as folder:
        test(args.exe.resolve(), Path(folder))
