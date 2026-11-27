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
        # A native focus crash during teardown also closes the window. Check the exit
        # code on a successful test path, while preserving any earlier test failure.
        if sys.exc_info()[0] is None and proc.returncode != 0:
            raise AssertionError(f"controller exited with status {proc.returncode:#x}")


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


def field(panel, name):
    return panel.child_window(auto_id=name, found_index=0).wrapper_object()


def visible_ids(panel):
    return {item.element_info.automation_id for item in panel.descendants() if item.is_visible()}


def tab(panel, name):
    field(panel, name).iface_selection_item.Select()
    # SelectionItem.Select can return before WinUI rebuilds the page's automation tree.
    marker = {"BasicTab": "TargetBox", "AdvancedTab": "PickControl",
              "ShortcutsTab": "HideShortcutBox", "SettingsTab": "MiniStyleBox"}[name]
    field(panel, marker)


def invoke(panel, name):
    field(panel, name).iface_invoke.Invoke()


def toggle(panel, name):
    field(panel, name).iface_toggle.Toggle()


def collapse(panel, actions):
    invoke(panel, "CollapseButton")
    field(panel, "ImeExpandButton")
    expected = ["Mini.DragHandle", *["Mini." + action for action in actions], "ImeExpandButton"]

    def matches():
        elements = [e for e in panel.descendants() if e.is_visible() and
                    (e.element_info.automation_id.startswith("Mini.") or e.element_info.automation_id == "ImeExpandButton")]
        return [e.element_info.automation_id for e in sorted(elements, key=lambda e: e.rectangle().left)] == expected

    wait(matches, "mini toolbar order " + ", ".join(expected))
    return gui.GetClientRect(panel.handle)[2:]


def verify_migrations(exe, base):
    cases = [
        ("fresh", None, None, "初始", ["hide", "clickThrough", "topmost"]),
        ("legacy-only", None, ["INVERT", "hide", "invert", "unknown"], "初始", ["invert", "hide"]),
        ("classic", {"Style": "classic", "Slots": ["topmost", "gray", "hide"]},
         ["gray", "clickThrough", "topmost", "hide", "invert"], "初始", ["gray", "clickThrough", "topmost", "hide"]),
        ("old-input-method", {"Style": "input-method", "Slots": ["invert", "gray", "hide"]},
         ["topmost"], "输入法", ["invert", "gray", "hide"]),
        ("empty", {"Style": "input-method", "Slots": []}, ["hide"], "输入法", []),
    ]
    for name, appearance, classic, expected_style, actions in cases:
        profile = base / name
        profile.mkdir()
        if classic is not None:
            (profile / "mini-toolbar.json").write_text(json.dumps(classic), encoding="utf8")
        if appearance is not None:
            (profile / "mini-appearance.json").write_text(json.dumps(appearance), encoding="utf8")
        with controller(exe, profile) as panel:
            tab(panel, "SettingsTab")
            assert field(panel, "MiniStyleBox").selected_text() == expected_style, name
            collapse(panel, actions)
            saved = json.loads((profile / "mini-appearance.json").read_text(encoding="utf-8-sig"))
            assert saved["Slots"] == actions
            assert saved["Style"] == ("initial" if expected_style == "初始" else "input-method")
            print("PASS toolbar configuration", name, flush=True)


def check_pages(panel, profile):
    tabs = ["BasicTab", "AdvancedTab", "ShortcutsTab", "SettingsTab"]
    assert sorted(tabs, key=lambda key: field(panel, key).rectangle().left) == tabs
    assert [field(panel, key).window_text() for key in tabs] == ["基础", "进阶", "快捷键", "设置"]
    basic = {"TargetBox", "TopmostBox", "OpacityInput", "ZeroButton", "InvertBox", "GrayBox", "HiddenBox", "ClickThroughBox"}
    recorders = {stem + suffix for stem in ("Hide", "Through", "OpacityUp", "OpacityDown")
                 for suffix in ("ShortcutBox", "ShortcutClear")}
    settings = {"ThemeBox", "ToolPin", "MiniStyleBox", "EditToolbarActions"}
    tab(panel, "BasicTab")
    ids = visible_ids(panel)
    assert basic <= ids and ids.isdisjoint(recorders | settings)
    tab(panel, "AdvancedTab")
    ids = visible_ids(panel)
    assert {"PickControl", "PickRegion"} <= ids and ids.isdisjoint(recorders | settings), ids
    tab(panel, "ShortcutsTab")
    ids = visible_ids(panel)
    assert recorders <= ids and ids.isdisjoint(basic | settings)
    for stem in ("Hide", "Through", "OpacityUp", "OpacityDown"):
        box = field(panel, stem + "ShortcutBox").rectangle()
        clear = field(panel, stem + "ShortcutClear").rectangle()
        left, top, right, bottom = gui.GetWindowRect(panel.handle)
        assert left <= box.left < box.right <= clear.left < clear.right <= right
        assert top < max(box.top, clear.top) < min(box.bottom, clear.bottom) < bottom
    assert field(panel, "HideShortcutBox").get_value()
    invoke(panel, "ThroughShortcutClear")
    wait(lambda: json.loads((profile / "passthrough-shortcut.json").read_text(encoding="utf-8-sig")) is None,
         "clearing a shortcut from its own page persists")
    tab(panel, "SettingsTab")
    ids = visible_ids(panel)
    assert settings <= ids and ids.isdisjoint(basic | recorders)
    assert not any(key.startswith("ToolbarAction.") for key in ids)
    combo = field(panel, "MiniStyleBox")
    combo.expand()
    items = [item for item in combo.descendants(control_type="ListItem") if item.is_visible()]
    assert {item.window_text() for item in items} == {"初始", "输入法"}
    next(item for item in items if item.window_text() == "初始").iface_selection_item.Select()
    print("PASS four page order, controls, shortcut fields, style choices", flush=True)


def test(exe, profile, drag=False):
    verify_migrations(exe, profile)
    title = "0001-Mini-QA-" + uuid.uuid4().hex[:8]
    target = subprocess.Popen([sys.executable, "-B", str(Path(__file__).with_name("ui_smoke.py")), "--target", title])
    write_setting(profile / "shortcut.json", 0x84)
    write_setting(profile / "passthrough-shortcut.json", 0x85)
    original_shortcut = (profile / "shortcut.json").read_bytes()
    (profile / "mini-toolbar.json").write_text('["invert"]', encoding="utf8")
    appearance = profile / "mini-appearance.json"
    appearance.write_text('{"Style":"ime","Slots":["topmost","hide","clickThrough"]}', encoding="utf8")
    (profile / "theme.txt").write_text("dark", encoding="utf8")
    try:
        wait(lambda: gui.FindWindow(None, title), "test target opened")
        hwnd = gui.FindWindow(None, title)
        def flag(window, style):
            return bool(gui.GetWindowLong(window, wc.GWL_EXSTYLE) & style)

        with controller(exe, profile) as panel:
            collapse(panel, ["topmost", "hide", "clickThrough"])
            invoke(panel, "ImeExpandButton")
            check_pages(panel, profile)
            choose_item(panel, field(panel, "ThemeBox"), "白")
            wait(lambda: (profile / "theme.txt").read_text(encoding="utf-8-sig").strip() == "light", "theme applied from Settings")
            toggle(panel, "ToolPin")
            tab(panel, "BasicTab")
            choose_item(panel, field(panel, "TargetBox"), title)
            wait(lambda: field(panel, "HiddenBox").is_enabled(), "bound to the test window")
            tab(panel, "SettingsTab")
            full_client = gui.GetClientRect(panel.handle)
            invoke(panel, "EditToolbarActions")
            def selected(action):
                return field(panel, "ToolbarAction." + action).iface_toggle.CurrentToggleState == 1
            def change(action):
                toggle(panel, "ToolbarAction." + action)
            for action in ("topmost", "hide", "clickThrough"):
                assert selected(action)
                change(action)
            assert json.loads(appearance.read_text(encoding="utf-8-sig"))["Slots"] == []
            empty_size = collapse(panel, [])
            invoke(panel, "ImeExpandButton")
            assert field(panel, "ToolbarSettingsBack").is_visible()
            change("gray")
            one_size = collapse(panel, ["gray"])
            assert empty_size[0] < one_size[0]
            invoke(panel, "ImeExpandButton")

            for action in ("hide", "invert", "topmost"):
                change(action)
            assert not field(panel, "ToolbarAction.clickThrough").is_enabled()
            four = ["gray", "hide", "invert", "topmost"]
            full_size = collapse(panel, four)
            assert full_size[0] > one_size[0]
            invoke(panel, "ImeExpandButton")
            # Removing an action opens a slot. Reselecting it appends it to the end.
            change("gray")
            assert field(panel, "ToolbarAction.clickThrough").is_enabled()
            change("gray")
            four = ["hide", "invert", "topmost", "gray"]
            assert field(panel, "ToolbarOrder").window_text() == "隐藏 → 反色 → 始终置顶 → 灰度"
            invoke(panel, "ToolbarSettingsBack")
            choose_item(panel, field(panel, "MiniStyleBox"), "输入法")
            width, height = collapse(panel, four)
            scale = ctypes.windll.user32.GetDpiForWindow(panel.handle) / 96
            assert abs(width / scale - 164) <= 2 and abs(height / scale - 22) <= 1, (width, height, scale)
            logo = field(panel, "Mini.DragHandle")
            assert logo.element_info.control_type == "Image" and not logo.element_info.element.CurrentIsKeyboardFocusable
            assert not logo.element_info.element.GetCurrentPropertyValue(30031)
            if drag:
                check_drag(panel, logo)
            assert [field(panel, "Mini." + action).window_text() for action in four] == ["隐藏", "反色", "始终置顶", "灰度"]
            assert flag(panel.handle, wc.WS_EX_TOPMOST)
            toggle(panel, "Mini.topmost")
            wait(lambda: flag(hwnd, wc.WS_EX_TOPMOST), "third mini action pins the target")
            toggle(panel, "Mini.topmost")
            wait(lambda: not flag(hwnd, wc.WS_EX_TOPMOST), "target unpins independently of panel")
            toggle(panel, "Mini.hide")
            wait(lambda: not gui.IsWindowVisible(hwnd), "first mini action hides the target")
            toggle(panel, "Mini.invert")
            toggle(panel, "Mini.gray")
            invoke(panel, "ImeExpandButton")
            wait(lambda: gui.GetClientRect(panel.handle) == full_client, "expanded panel restores its client size")
            tab(panel, "BasicTab")
            assert all(field(panel, name).iface_toggle.CurrentToggleState for name in ("InvertBox", "GrayBox", "HiddenBox"))
            for name in ("InvertBox", "GrayBox", "HiddenBox"):
                toggle(panel, name)
            wait(lambda: gui.IsWindowVisible(hwnd), "original target restored")
            assert json.loads(appearance.read_text(encoding="utf-8-sig"))["Slots"] == four
            assert (profile / "shortcut.json").read_bytes() == original_shortcut

        with controller(exe, profile) as panel:
            tab(panel, "SettingsTab")
            assert field(panel, "MiniStyleBox").selected_text() == "输入法"
            invoke(panel, "EditToolbarActions")
            toggle(panel, "ToolbarAction.gray")
            toggle(panel, "ToolbarAction.clickThrough")
            invoke(panel, "ToolbarSettingsBack")
            choose_item(panel, field(panel, "MiniStyleBox"), "初始")
            tab(panel, "BasicTab")
            choose_item(panel, field(panel, "TargetBox"), title)
            collapse(panel, ["hide", "invert", "topmost", "clickThrough"])
            toggle(panel, "Mini.clickThrough")
            wait(lambda: flag(hwnd, wc.WS_EX_TRANSPARENT), "fourth initial-style action enables click-through")
            toggle(panel, "Mini.clickThrough")
            wait(lambda: not flag(hwnd, wc.WS_EX_TRANSPARENT), "restored click-through")
            print("PASS both skins, zero-to-four actions, append order, fifth-choice limit and restart", flush=True)
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
