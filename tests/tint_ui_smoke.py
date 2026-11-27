"""Exercise the color flyout and sample one pixel of a disposable target on an idle input desktop."""
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid

import win32api
import win32con as wc
import win32gui as gui
import win32process
from pywinauto import keyboard

from mini_toolbar_smoke import choose_item, controller, field
from ui_smoke import desktop, wait


def screen_rgb(point, target):
    # Include the layered filter while sampling only the visible test surface.
    assert gui.GetAncestor(gui.WindowFromPoint(point), 2) == target
    dc = gui.GetDC(0)
    memory = gui.CreateCompatibleDC(dc)
    bitmap = gui.CreateCompatibleBitmap(dc, 1, 1)
    old = gui.SelectObject(memory, bitmap)
    try:
        gui.BitBlt(memory, 0, 0, 1, 1, dc, *point, wc.SRCCOPY | 0x40000000)
        color = gui.GetPixel(memory, 0, 0)
        return tuple((color >> shift) & 255 for shift in (0, 8, 16))
    finally:
        gui.SelectObject(memory, old)
        gui.DeleteObject(bitmap)
        gui.DeleteDC(memory)
        gui.ReleaseDC(0, dc)


def test(exe, profile, title):
    target = gui.FindWindow(None, title)
    source = (206, 41, 70)  # ui_smoke's fixed Tk background.
    tint = (32, 176, 224)
    with controller(exe, profile) as panel:
        combo = field(panel, "TargetBox")
        color = field(panel, "TintColorButton")
        strength = field(panel, "TintStrengthInput")
        assert not color.is_enabled() and not strength.is_enabled()
        choose_item(panel, combo, title)
        wait(strength.is_enabled, "tint controls enabled on binding")
        assert strength.get_value() == "0"

        left, top, right, bottom = win32api.GetMonitorInfo(win32api.MonitorFromWindow(panel.handle))["Work"]
        # Keep the calibration point exposed while the controller has keyboard focus.
        x = right - 310 if panel.rectangle().mid_point().x < (left + right) / 2 else left + 12
        gui.SetWindowPos(target, wc.HWND_TOPMOST, x, bottom - 240, 0, 0, wc.SWP_NOACTIVATE | wc.SWP_NOSIZE)
        point = gui.ClientToScreen(target, (60, 58))
        baseline = screen_rgb(point, target)
        inverted = baseline == tuple(255 - channel for channel in source)
        assert inverted or baseline == source, ("unexpected calibration", baseline)

        def looks_like(rgb):
            expected = tuple(255 - channel for channel in rgb) if inverted else rgb
            return all(abs(a - b) <= 3 for a, b in zip(screen_rgb(point, target), expected))

        pid = panel.element_info.process_id
        def hex_field():
            # An unconstrained flyout can have its own top-level popup HWND.
            for window in desktop.windows(process=pid):
                for item in window.descendants(control_type="Edit"):
                    if item.element_info.automation_id == "HexTextBox" and item.is_visible():
                        return item
            return None

        assert hex_field() is None
        bounds = gui.GetWindowRect(panel.handle)
        panel.set_focus()
        color.iface_invoke.Invoke()
        wait(hex_field, "color square opens the native color picker")
        assert gui.GetWindowRect(panel.handle) == bounds
        hex_box = hex_field()
        hex_box.set_focus()
        hex_box.set_edit_text("#20B0E0")
        assert win32process.GetWindowThreadProcessId(gui.GetForegroundWindow())[1] == pid
        keyboard.send_keys("{TAB}")
        wait(lambda: "#20B0E0" in color.window_text(), "hex selection updates the swatch")
        assert win32process.GetWindowThreadProcessId(gui.GetForegroundWindow())[1] == pid
        keyboard.send_keys("{ESC}")
        wait(lambda: hex_field() is None, "dismiss the color picker")
        assert strength.get_value() == "0" and looks_like(source)
        assert gui.GetWindowRect(panel.handle) == bounds

        strength.set_edit_text("50")
        midpoint = tuple((a + b) / 2 for a, b in zip(source, tint))
        wait(lambda: looks_like(midpoint), "50% mixes the selected color with the target")
        strength.set_edit_text("100")
        wait(lambda: looks_like(tint), "100% presents the selected color")
        strength.set_edit_text("0")
        wait(lambda: looks_like(source), "0% restores the target's pixels")
        strength.set_edit_text("100")
        wait(lambda: looks_like(tint), "tint reapplied before release")
        choose_item(panel, combo, "释放")
        wait(lambda: not color.is_enabled() and looks_like(source), "release restores and disables tint")
        choose_item(panel, combo, title)
        wait(lambda: strength.is_enabled() and strength.get_value() == "0" and looks_like(source),
             "new binding starts without tint")


if __name__ == "__main__":
    title = "SellFishTintQA-" + uuid.uuid4().hex[:8]
    previous = gui.GetForegroundWindow()
    fixture = subprocess.Popen([sys.executable, "-B", str(Path(__file__).with_name("ui_smoke.py")), "--target", title])
    try:
        wait(lambda: gui.FindWindow(None, title), "disposable target starts")
        with tempfile.TemporaryDirectory(prefix="sell-fish-tint-") as temp:
            test(Path(sys.argv[1]).resolve(), Path(temp), title)
    finally:
        fixture.terminate()
        fixture.wait(timeout=5)
        if gui.IsWindow(previous):
            try:
                gui.SetForegroundWindow(previous)
            except gui.error:
                pass
