"""Exercise the Basic window picker on an idle Windows input desktop using two disposable windows."""
import ctypes
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid

import win32api
import win32con as wc
import win32gui as gui


def run_target(title):
    edges = [0, 0]

    def procedure(hwnd, msg, wparam, lparam):
        if msg == wc.WM_LBUTTONDOWN:
            edges[0] += 1
        if msg == wc.WM_LBUTTONUP:
            edges[1] += 1
        if msg == wc.WM_APP:
            return edges[wparam]
        return gui.DefWindowProc(hwnd, msg, wparam, lparam)

    cls = gui.WNDCLASS()
    cls.hInstance = win32api.GetModuleHandle(None)
    cls.lpszClassName = title
    cls.lpfnWndProc = procedure
    gui.RegisterClass(cls)
    root = gui.CreateWindow(title, title, wc.WS_OVERLAPPEDWINDOW, 0, 0, 220, 140, 0, 0, cls.hInstance, None)
    gui.CreateWindow(title, "Mouse receiver", wc.WS_VISIBLE | wc.WS_CHILD,
                     8, 8, 135, 48, root, 101, cls.hInstance, None)
    gui.ShowWindow(root, wc.SW_SHOWNOACTIVATE)
    gui.PumpMessages()


if len(sys.argv) > 1 and sys.argv[1] == "--target":
    run_target(sys.argv[2])
    raise SystemExit

from pywinauto import mouse
from mini_toolbar_smoke import choose_item, controller, field
from ui_smoke import alpha, wait


def checked_click(point, root):
    # Stop before injecting input if another application's window covers the test point.
    hit = gui.GetAncestor(gui.WindowFromPoint(point), 2)
    assert hit == root, f"expected test surface at {point}, got {gui.GetClassName(hit) if hit else 'no window'}"
    mouse.click(coords=point)


def escape():
    win32api.keybd_event(wc.VK_ESCAPE, 0, 0, 0)
    win32api.keybd_event(wc.VK_ESCAPE, 0, wc.KEYEVENTF_KEYUP, 0)


def test(exe, profile, title):
    target = gui.FindWindow(title, title)
    child = gui.GetDlgItem(target, 101)
    original_style = gui.GetWindowLong(target, wc.GWL_EXSTYLE)

    def edges():
        return tuple(gui.SendMessage(child, wc.WM_APP, index, 0) for index in range(2))

    with controller(exe, profile) as panel:
        flags = wc.SWP_NOMOVE | wc.SWP_NOSIZE | wc.SWP_NOACTIVATE
        gui.SetWindowPos(panel.handle, wc.HWND_TOPMOST, 0, 0, 0, 0, flags)
        picker = field(panel, "PickWindowButton")
        hidden = field(panel, "HiddenBox")
        combo = field(panel, "TargetBox")
        opacity = field(panel, "OpacityInput")
        work = win32api.GetMonitorInfo(win32api.MonitorFromWindow(panel.handle))["Work"]
        left, top, right, bottom = work
        # Park the target at the opposite vertical edge from the controller.
        y = bottom - 160 if panel.rectangle().mid_point().y < (top + bottom) / 2 else top + 12
        gui.SetWindowPos(target, wc.HWND_TOP, left + 20, y, 0, 0, wc.SWP_NOSIZE | wc.SWP_NOACTIVATE)
        point = gui.ClientToScreen(child, (30, 20))
        assert gui.WindowFromPoint(point) == child

        def arm():
            middle = picker.rectangle().mid_point()
            checked_click((middle.x, middle.y), panel.handle)
            wait(lambda: not picker.is_enabled() and not combo.is_enabled(), "picker awaits the next click")

        arm()
        own_tab = field(panel, "SettingsTab").rectangle().mid_point()
        checked_click((own_tab.x, own_tab.y), panel.handle)
        wait(lambda: picker.is_enabled() and not hidden.is_enabled(), "own window rejected")
        assert field(panel, "BasicTab").iface_selection_item.CurrentIsSelected, "self-selection must not switch tabs"
        assert "自身" in field(panel, "StatusText").window_text()

        with controller(exe, profile / "second") as second:
            other = second.rectangle()
            gui.SetWindowPos(second.handle, wc.HWND_TOPMOST, right - other.width() - 20, min(y, bottom - other.height()),
                             0, 0, wc.SWP_NOSIZE | wc.SWP_NOACTIVATE)
            arm()
            tab = field(second, "SettingsTab").rectangle().mid_point()
            checked_click((tab.x, tab.y), second.handle)
            wait(lambda: picker.is_enabled() and not hidden.is_enabled(), "second sell fish instance rejected")
            assert field(second, "BasicTab").iface_selection_item.CurrentIsSelected

        arm()
        assert gui.GetForegroundWindow() == panel.handle, "Esc must target the disposable controller"
        escape()
        wait(lambda: picker.is_enabled() and not hidden.is_enabled(), "Esc cancels without binding")
        checked_click(point, target)
        wait(lambda: edges() == (1, 1), "clicks reach the native child after cancellation")

        arm()
        assert gui.WindowFromPoint(point) == child
        mouse.press(coords=point)
        try:
            escape()
        finally:
            mouse.release(coords=point)
        wait(lambda: picker.is_enabled() and not hidden.is_enabled(), "Esc during a held click cancels the selection")
        assert edges() == (1, 1), "cancelling after mouse-down must swallow the matching release"

        arm()
        assert gui.WindowFromPoint(point) == child
        checked_click(point, target)
        wait(lambda: hidden.is_enabled() and title in combo.selected_text(), "child resolves to its top-level window")
        assert not picker.is_enabled() and edges() == (1, 1), "picker must swallow both click edges and lock the target"
        for value, expected in (("0", 0), ("100", 255)):
            opacity.set_edit_text(value)
            wait(lambda: alpha(target) == expected, "typed opacity " + value)
        hidden.iface_toggle.Toggle()
        wait(lambda: not gui.IsWindowVisible(target) and gui.IsWindowVisible(panel.handle), "only the target is hidden")
        choose_item(panel, combo, "释放")
        wait(lambda: picker.is_enabled() and gui.IsWindowVisible(target) and not hidden.is_enabled(),
             "release restores target and enables another pick")
        assert gui.GetWindowLong(target, wc.GWL_EXSTYLE) == original_style
        arm()
        # The controller context closes the app while the global picker is armed.
    checked_click(point, target)
    wait(lambda: edges() == (2, 2), "closing an armed picker releases the mouse")


if __name__ == "__main__":
    title = "SellFishPickerQA-" + uuid.uuid4().hex[:8]
    cursor, foreground = gui.GetCursorPos(), gui.GetForegroundWindow()
    target = subprocess.Popen([sys.executable, "-B", __file__, "--target", title])
    try:
        wait(lambda: gui.FindWindow(title, title), "disposable target starts")
        with tempfile.TemporaryDirectory(prefix="sell-fish-picker-") as folder:
            test(Path(sys.argv[1]).resolve(), Path(folder), title)
    finally:
        target.terminate()
        target.wait(timeout=5)
        win32api.SetCursorPos(cursor)
        if gui.IsWindow(foreground):
            ctypes.windll.user32.SetForegroundWindow(foreground)
