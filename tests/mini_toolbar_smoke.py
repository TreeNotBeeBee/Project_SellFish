"""Test the mini toolbar using a disposable target and profile. On an idle input desktop, add --drag."""
import argparse
import ctypes
from contextlib import contextmanager
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

from pywinauto import mouse
import win32con as wc
import win32gui as gui

from ui_smoke import desktop, wait, write_setting


def choose_item(panel, box, text):
    box.expand()
    def select():
        for row in panel.descendants(control_type="ListItem"):
            if row.is_visible() and text in row.window_text():
                row.iface_selection_item.Select()
                return True
        return False
    wait(select, f"selected {text}")


@contextmanager
def controller(exe, profile):
    proc = subprocess.Popen([str(exe)], cwd=exe.parent, env={**os.environ, "WINDOWPILOT_PROFILE_DIR": str(profile)})
    panel = desktop.window(process=proc.pid, title="sell fish", class_name="WinUIDesktopWin32WindowClass")
    try:
        panel.wait("visible", timeout=35)
        yield panel
    finally:
        if proc.poll() is None and panel.exists(timeout=.1):
            gui.PostMessage(panel.handle, wc.WM_CLOSE, 0, 0)
        try:
            proc.wait(timeout=8)
        except subprocess.TimeoutExpired:
            proc.terminate()
            proc.wait(timeout=5)


def check_drag(panel, logo):
    point = logo.rectangle().mid_point()
    assert gui.GetAncestor(gui.WindowFromPoint((point.x, point.y)), 2) == panel.handle
    before = gui.GetWindowRect(panel.handle)
    mouse.click(coords=(point.x, point.y))
    assert gui.GetWindowRect(panel.handle) == before
    mouse.press(coords=(point.x, point.y))
    try:
        mouse.move(coords=(point.x + 40, point.y + 32))
        wait(lambda: gui.GetWindowRect(panel.handle)[:2] != before[:2], "dragging the logo moves the toolbar")
    finally:
        mouse.release(coords=(point.x + 40, point.y + 32))
    time.sleep(.12)
    after = gui.GetWindowRect(panel.handle)
    mouse.move(coords=(point.x + 60, point.y + 45))
    time.sleep(.12)
    assert gui.GetWindowRect(panel.handle) == after


def verify_default_styles(exe, base):
    # 1.3.5 users with only the legacy toolbar file keep their classic layout.
    for previous in (False, True):
        profile = base / ("existing-classic" if previous else "fresh")
        profile.mkdir()
        if previous:
            (profile / "mini-toolbar.json").write_text('["hide"]', encoding="utf8")
        for _ in range(1 if previous else 2):
            with controller(exe, profile) as panel:
                panel.child_window(auto_id="SettingsTab").wrapper_object().iface_selection_item.Select()
                box = panel.child_window(auto_id="MiniStyleBox").wrapper_object()
                expected = "经典" if previous else "初始图标"
                assert box.selected_text() == expected, (expected, box.selected_text())
                print("PASS default style", expected, flush=True)


def test(exe, profile, drag=False):
    verify_default_styles(exe, profile)
    title = "0001-Mini-QA-" + uuid.uuid4().hex[:8]
    target = subprocess.Popen([sys.executable, "-B", str(Path(__file__).with_name("ui_smoke.py")), "--target", title])
    write_setting(profile / "shortcut.json", 0x84)
    write_setting(profile / "passthrough-shortcut.json", 0x85)
    legacy = profile / "mini-toolbar.json"
    original = b'["topmost", "hide"]'
    legacy.write_bytes(original)
    appearance = profile / "mini-appearance.json"
    old_icons = b'{"Style":"ime","Slots":["topmost","hide","clickThrough"]}'
    appearance.write_bytes(old_icons)
    (profile / "theme.txt").write_text("dark", encoding="utf8")
    try:
        wait(lambda: gui.FindWindow(None, title), "test target opened")
        hwnd = gui.FindWindow(None, title)
        def style(handle, flag):
            return bool(gui.GetWindowLong(handle, wc.GWL_EXSTYLE) & flag)
        for restart in (False, True):
            with controller(exe, profile) as panel:
                def field(name):
                    return panel.child_window(auto_id=name, found_index=0).wrapper_object()
                def tab(name):
                    field(name).iface_selection_item.Select()
                def active(name):
                    return panel.child_window(auto_id=name, found_index=0).exists(timeout=.1)
                def invoke(name):
                    field(name).iface_invoke.Invoke()
                def toggle(name):
                    field(name).iface_toggle.Toggle()
                def mini_ids():
                    return [n.element_info.automation_id for n in panel.descendants()
                            if n.element_info.automation_id.startswith("Mini.") and n.is_visible()]
                def collapse(expected):
                    invoke("CollapseButton")
                    wait(lambda: mini_ids() == expected, "toolbar order " + ", ".join(expected))
                classic = ["Mini.topmost", "Mini.hide"]
                if restart:
                    collapse(["Mini.DragHandle", "Mini.gray", "Mini.invert", "Mini.hide"])
                    assert field("Mini.DragHandle").element_info.element.CurrentHelpText == "输入法图标"
                    assert all(not field(name).is_enabled() for name in mini_ids()[1:])
                    assert style(panel.handle, wc.WS_EX_TOPMOST)
                    invoke("ImeExpandButton")
                    tab("SettingsTab")
                    assert field("MiniStyleBox").selected_text() == "输入法"
                    choose_item(panel, field("MiniStyleBox"), "经典")
                    collapse(classic)
                    assert active("ExitButton") and not active("Mini.DragHandle")
                    assert legacy.read_bytes() == original
                    continue
                collapse(["Mini.DragHandle", "Mini.topmost", "Mini.hide", "Mini.clickThrough"])
                assert field("Mini.DragHandle").element_info.element.CurrentHelpText == "初始图标"
                assert appearance.read_bytes() == old_icons
                invoke("ImeExpandButton")
                choose_item(panel, field("TargetBox"), title)
                wait(lambda: field("HiddenBox").is_enabled(), "bound to the test window")
                tab("SettingsTab")
                assert field("MiniStyleBox").selected_text() == "初始图标"
                toggle("ToolPin")
                choose_item(panel, field("MiniStyleBox"), "输入法")
                choose_item(panel, field("MiniSlot2"), "页面穿透")
                layout = ["Mini.DragHandle", "Mini.topmost", "Mini.clickThrough", "Mini.hide"]
                full_client = gui.GetClientRect(panel.handle)
                collapse(layout)
                assert not active("ExitButton")
                logo = field("Mini.DragHandle")
                assert logo.element_info.control_type == "Image" and not logo.element_info.element.CurrentIsKeyboardFocusable
                assert logo.element_info.element.CurrentHelpText == "输入法图标"
                assert [field(name).window_text() for name in layout[1:]] == ["始终置顶", "页面穿透", "隐藏"]
                assert not logo.element_info.element.GetCurrentPropertyValue(30031)  # UIA_IsInvokePatternAvailablePropertyId
                buttons = [*mini_ids(), "ImeExpandButton"]
                assert sorted(buttons, key=lambda name: field(name).rectangle().left) == [*layout, "ImeExpandButton"]
                commands = {node.element_info.automation_id for node in panel.descendants()
                            if node.element_info.control_type in ("Button", "CheckBox") and node.is_visible()}
                assert commands == set(layout[1:] + ["ImeExpandButton"]), commands
                # The invisible native frame varies with Windows. The reference strip is 164 × 22 DIPs inside it.
                _, _, width, height = gui.GetClientRect(panel.handle)
                scale = ctypes.windll.user32.GetDpiForWindow(panel.handle) / 96
                assert abs(width / scale - 164) <= 2 and abs(height / scale - 22) <= 1, (width, height, scale)
                assert style(panel.handle, wc.WS_EX_TOPMOST)
                if drag:
                    check_drag(panel, logo)
                toggle("Mini.topmost")
                wait(lambda: style(hwnd, wc.WS_EX_TOPMOST), "mini pin applies to target")
                toggle("Mini.topmost")
                wait(lambda: not style(hwnd, wc.WS_EX_TOPMOST), "target unpins independently of the panel")
                toggle("Mini.clickThrough")
                wait(lambda: style(hwnd, wc.WS_EX_TRANSPARENT), "mini click-through applies to target")
                toggle("Mini.clickThrough")
                toggle("Mini.hide")
                wait(lambda: not gui.IsWindowVisible(hwnd), "mini hide applies to target")
                invoke("ImeExpandButton")
                wait(lambda: active("MiniStyleBox"), "expanding returns to Settings")
                wait(lambda: gui.GetClientRect(panel.handle) == full_client, "expanding restores the full client size")
                choose_item(panel, field("MiniSlot1"), "灰度")
                choose_item(panel, field("MiniSlot2"), "反色")
                tab("BasicTab")
                choose_item(panel, field("ThemeBox"), "白")
                collapse(["Mini.DragHandle", "Mini.gray", "Mini.invert", "Mini.hide"])
                assert [field("Mini." + name).window_text() for name in ("gray", "invert", "hide")] == ["灰度", "反色", "隐藏"]
                toggle("Mini.gray")
                toggle("Mini.invert")
                invoke("ImeExpandButton")
                assert field("GrayBox").iface_toggle.CurrentToggleState and field("InvertBox").iface_toggle.CurrentToggleState
                toggle("GrayBox")
                toggle("InvertBox")
                toggle("HiddenBox")
                wait(lambda: gui.IsWindowVisible(hwnd), "original target restored")
                assert legacy.read_bytes() == original
                assert json.loads(appearance.read_text(encoding="utf8")) == {"Style": "input-method", "Slots": ["gray", "invert", "hide"]}
                print("PASS all five IME action assignments and independent classic configuration", flush=True)
    finally:
        target.terminate()
        target.wait(timeout=6)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", type=Path)
    parser.add_argument("--drag", action="store_true", help="use the real pointer for the drag check")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="sell-fish-mini-") as profile:
        test(args.exe.resolve(), Path(profile), args.drag)
