"""Verify the screen eyedropper and per-color transparency with disposable windows on an idle Windows desktop."""
import ctypes
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid

import win32api
import win32con as wc
import win32gui as gui


SIZE = (288, 144)
BLUE, NEAR_BLUE, AMBER = (40, 80, 216), (48, 89, 208), (251, 175, 74)
CHECKER = ((72, 119, 113), (198, 217, 196))


def fixture(title):
    edges = [0, 0]
    roles = {}

    def fill(dc, rect, rgb):
        brush = gui.CreateSolidBrush(win32api.RGB(*rgb))
        try:
            gui.FillRect(dc, rect, brush)
        finally:
            gui.DeleteObject(brush)

    def paint(hwnd, dc):
        role = roles.get(hwnd)
        if role == "-backdrop":
            for y in range(0, SIZE[1], 24):
                for x in range(0, SIZE[0], 24):
                    fill(dc, (x, y, x + 24, y + 24), CHECKER[(x // 24 + y // 24) % 2])
        elif role == "-palette":
            fill(dc, (0, 0, 40, 36), BLUE)
            fill(dc, (40, 0, 80, 36), tuple(255 - c for c in BLUE))
        else:
            fill(dc, (0, 0, *SIZE), BLUE)
            # Distinct shades make both the matching range and the retained colors visible.
            fill(dc, (16, 108, 80, 128), NEAR_BLUE)
            fill(dc, (235, 16, 263, 44), AMBER)
            fill(dc, (112, 94, 184, 102), (244, 239, 216))
            for line in range(50):
                fill(dc, (149 - line // 2, 39 + line, 150, 40 + line), (244, 239, 216))
                fill(dc, (153, 44 + line, 154 + line // 2, 45 + line), (230, 116, 88))

    def proc(hwnd, msg, wp, lp):
        if msg == wc.WM_PAINT:
            dc, ps = gui.BeginPaint(hwnd)
            try:
                paint(hwnd, dc)
            finally:
                gui.EndPaint(hwnd, ps)
            return 0
        if roles.get(hwnd) == "-palette":
            if msg == wc.WM_LBUTTONDOWN:
                edges[0] += 1
            if msg == wc.WM_LBUTTONUP:
                edges[1] += 1
            if msg == wc.WM_APP:
                return edges[wp]
        return gui.DefWindowProc(hwnd, msg, wp, lp)

    cls = gui.WNDCLASS()
    cls.hInstance = win32api.GetModuleHandle(None)
    cls.lpszClassName, cls.lpfnWndProc = title, proc
    gui.RegisterClass(cls)
    for suffix, size in (("-backdrop", SIZE), ("", SIZE), ("-palette", (80, 36))):
        hwnd = gui.CreateWindow(title, title + suffix, wc.WS_POPUP, 0, 0, *size, 0, 0, cls.hInstance, None)
        roles[hwnd] = suffix
        gui.ShowWindow(hwnd, wc.SW_SHOWNOACTIVATE)
    gui.PumpMessages()


if len(sys.argv) > 1 and sys.argv[1] == "--target":
    fixture(sys.argv[2])
    raise SystemExit

from mini_toolbar_smoke import choose_item, controller, field, tab
from tint_ui_smoke import screen_rgb
from ui_smoke import wait
from window_picker_smoke import checked_click, escape


def test(exe, profile, title, capture=None):
    target, backdrop, palette = [gui.FindWindow(title, title + suffix) for suffix in ("", "-backdrop", "-palette")]
    with controller(exe, profile) as panel:
        monitor = win32api.MonitorFromWindow(panel.handle)
        left, top, right, bottom = win32api.GetMonitorInfo(monitor)["Work"]
        x = right - SIZE[0] - 16 if panel.rectangle().mid_point().x < (left + right) / 2 else left + 16
        y = bottom - SIZE[1] - 48
        flags = wc.SWP_NOSIZE | wc.SWP_NOACTIVATE
        for hwnd in (backdrop, target):
            gui.SetWindowPos(hwnd, wc.HWND_TOPMOST, x, y, 0, 0, flags)
        # Sample from another window, on a secondary monitor when one is available.
        px, py = x, y - 60
        for other, _, _ in win32api.EnumDisplayMonitors(None, None):
            if int(other) != int(monitor):
                ml, mt, mr, mb = win32api.GetMonitorInfo(other)["Work"]
                px, py = ml + 20, mb - 80
                break
        gui.SetWindowPos(palette, wc.HWND_TOPMOST, px, py, 0, 0, flags)

        tab(panel, "AdvancedTab")
        enabled = field(panel, "KeyEnabled")
        dropper = field(panel, "KeyEyedropper")
        assert not enabled.is_enabled() and not dropper.is_enabled()
        tab(panel, "BasicTab")
        choose_item(panel, field(panel, "TargetBox"), title + " ")
        tab(panel, "AdvancedTab")
        color, strength, match_range = [field(panel, name) for name in ("KeyColorButton", "KeyStrengthInput", "KeyRangeInput")]
        wait(dropper.is_enabled, "bound target enables screen color picking")
        assert enabled.get_toggle_state() == 0 and strength.get_value() == "100" and match_range.get_value() == "0"

        source_point = gui.ClientToScreen(palette, (12, 14))
        inverted = screen_rgb(source_point, palette) == tuple(255 - c for c in BLUE)
        if inverted:
            source_point = gui.ClientToScreen(palette, (52, 14))
        assert screen_rgb(source_point, palette) == BLUE

        def looks_like(xx, yy, rgb):
            expected = tuple(255 - c for c in rgb) if inverted else rgb
            actual = screen_rgb(gui.ClientToScreen(target, (xx, yy)), target)
            return all(abs(a - b) <= 3 for a, b in zip(actual, expected))

        def arm():
            p = dropper.rectangle().mid_point()
            checked_click((p.x, p.y), panel.handle)
            wait(lambda: not dropper.is_enabled(), "eyedropper armed")

        original = color.window_text()
        arm()
        assert gui.GetForegroundWindow() == panel.handle
        escape()
        wait(dropper.is_enabled, "Esc disarms eyedropper")
        assert color.window_text() == original
        arm()
        checked_click(source_point, palette)
        wait(lambda: "#2850D8" in color.window_text() and dropper.is_enabled(), "picked screen RGB from the other window")
        assert [gui.SendMessage(palette, wc.WM_APP, n, 0) for n in range(2)] == [0, 0]
        assert looks_like(12, 12, BLUE) and looks_like(36, 12, BLUE)
        if capture:
            capture(target, "before")
        enabled.iface_toggle.Toggle()
        # Wait for the first captured frame: when the native target is dimmed ahead
        # of that frame the background may already be visible across the entire test surface.
        wait(lambda: (looks_like(12, 12, CHECKER[0]) and looks_like(36, 12, CHECKER[1]) and
                      looks_like(30, 116, NEAR_BLUE) and looks_like(250, 30, AMBER)),
             "background shows through matching pixels while near-blue and amber stay visible")
        if capture:
            capture(target, "after")
        match_range.set_edit_text("15")
        wait(lambda: looks_like(30, 116, CHECKER[1]), "range includes neighboring blue")
        strength.set_edit_text("50")
        mixed = tuple((a + b) / 2 for a, b in zip(BLUE, CHECKER[0]))
        wait(lambda: looks_like(12, 12, mixed), "half strength blends matching pixels with the background")
        assert looks_like(250, 30, AMBER)
        strength.set_edit_text("0")
        wait(lambda: looks_like(12, 12, BLUE), "zero strength restores the target")
        strength.set_edit_text("100")
        wait(lambda: looks_like(12, 12, CHECKER[0]), "filter reapplied")
        tab(panel, "BasicTab")
        choose_item(panel, field(panel, "TargetBox"), "释放")
        wait(lambda: looks_like(12, 12, BLUE) and looks_like(30, 116, NEAR_BLUE), "release restores original appearance")
        tab(panel, "AdvancedTab")
        assert enabled.get_toggle_state() == 0 and not dropper.is_enabled()


def run(exe, capture=None):
    ctypes.windll.user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))
    title = "0001-KeyQA-" + uuid.uuid4().hex[:7]
    cursor, foreground = gui.GetCursorPos(), gui.GetForegroundWindow()
    process = subprocess.Popen([sys.executable, "-B", __file__, "--target", title])
    try:
        wait(lambda: gui.IsWindowVisible(gui.FindWindow(title, title + "-palette")), "disposable surfaces opened")
        with tempfile.TemporaryDirectory(prefix="sell-fish-key-") as folder:
            test(exe, Path(folder), title, capture)
    finally:
        process.terminate()
        process.wait(timeout=5)
        win32api.SetCursorPos(cursor)
        if gui.IsWindow(foreground):
            ctypes.windll.user32.SetForegroundWindow(foreground)


if __name__ == "__main__":
    run(Path(sys.argv[1]).resolve())
