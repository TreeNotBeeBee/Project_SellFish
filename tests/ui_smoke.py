"""Interactive acceptance of the compact panel. All bindings and settings are disposable."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

if len(sys.argv) > 1 and sys.argv[1] == "--target":
    import tkinter as tk
    ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(sys.argv[2])
    root = tk.Tk()
    root.title(sys.argv[2])
    root.geometry("260x140+20+90")
    root.configure(background="#CE2946")
    root.mainloop()
    raise SystemExit

from pywinauto import Desktop, mouse
import win32api
import win32con
import win32gui

u = ctypes.WinDLL("user32", use_last_error=True)
u.GetLayeredWindowAttributes.argtypes = [wintypes.HWND, ctypes.c_void_p, ctypes.POINTER(ctypes.c_ubyte), ctypes.c_void_p]
desktop = Desktop(backend="uia")

def wait(condition, label, timeout=10):
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        if condition():
            print("PASS", label, flush=True)
            return
        time.sleep(.1)
    raise AssertionError(label)

def chord(vk):
    for key in [17, 18, 16, vk]:
        win32api.keybd_event(key, 0, 0, 0)
    for key in [vk, 16, 18, 17]:
        win32api.keybd_event(key, 0, win32con.KEYEVENTF_KEYUP, 0)

def write_setting(path, vk):
    path.write_text(json.dumps({"Modifiers": 7, "Key": vk}), encoding="utf8")

def read_setting(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))["Key"]

def alpha(hwnd):
    value = ctypes.c_ubyte()
    if u.GetLayeredWindowAttributes(hwnd, None, ctypes.byref(value), None): return value.value
    return 255

def choose(panel, box, text):
    box.expand()
    time.sleep(.4)
    candidates = [n for n in panel.descendants(control_type="ListItem") if text in n.window_text()]
    for node in reversed(candidates):
        if not node.is_visible() or node.rectangle().width() < 1: continue
        pt = node.rectangle().mid_point()
        hit = desktop.from_point(pt.x, pt.y)
        if os.environ.get("WINDOWPILOT_TEST_VERBOSE"):
            print("item", text, node.rectangle(), "hit", hit.element_info.control_type, hit.window_text(), flush=True)
        if hit.element_info.process_id == panel.element_info.process_id and text in hit.window_text():
            mouse.click(coords=(pt.x, pt.y))
            return
    raise AssertionError("Could not select " + text)

def main(exe):
    name = "0001-WindowPilot-QA-" + uuid.uuid4().hex[:8]
    target = subprocess.Popen([sys.executable, __file__, "--target", name])
    controller = None
    hwnd = 0
    try:
        wait(lambda: win32gui.FindWindow(None, name), "disposable target starts")
        hwnd = win32gui.FindWindow(None, name)
        with tempfile.TemporaryDirectory() as temp:
            profile = Path(temp)
            hide_key, pass_key = profile / "shortcut.json", profile / "passthrough-shortcut.json"
            keys = []
            for vk in range(0x70, 0x7b):
                if u.RegisterHotKey(None, 0x5715, 7, vk):
                    u.UnregisterHotKey(None, 0x5715)
                    keys.append(vk)
                if len(keys) == 4: break
            assert len(keys) == 4
            initial_hide, initial_pass, new_hide, new_pass = keys
            write_setting(hide_key, initial_hide)
            write_setting(pass_key, initial_pass)
            (profile / "theme.txt").write_text("light", encoding="utf8")
            (profile / "mini-toolbar.json").write_text('["hide", "clickThrough", "topmost"]', encoding="utf8")
            env = {**os.environ, "WINDOWPILOT_PROFILE_DIR": str(profile)}
            controller = subprocess.Popen([str(exe)], cwd=exe.parent, env=env)
            spec = desktop.window(process=controller.pid, class_name="WinUIDesktopWin32WindowClass", title="sell fish")
            spec.wait("visible", timeout=40)
            panel = spec.wrapper_object()
            def control(id):
                return spec.child_window(auto_id=id, found_index=0).wrapper_object()
            combo = control("TargetBox")
            def size():
                left, top, right, bottom = win32gui.GetWindowRect(panel.handle)
                return right-left, bottom-top
            def verify_fixed():
                style = win32gui.GetWindowLong(panel.handle, win32con.GWL_STYLE)
                assert not (style & (win32con.WS_THICKFRAME | win32con.WS_MAXIMIZEBOX)), hex(style)
            def probe_mini():
                full = size()
                verify_fixed()
                control("CollapseButton").invoke()
                wait(lambda: size()[1] < full[1] / 2 and size()[0] < full[0], "visible input-method-sized toolbar")
                verify_fixed()
                assert win32gui.IsWindowVisible(panel.handle) and not win32gui.IsIconic(panel.handle)
                assert [c.element_info.automation_id for c in panel.descendants(control_type="CheckBox")
                        if c.is_visible()] == ["Mini.hide", "Mini.clickThrough", "Mini.topmost"]
                assert win32gui.SendMessage(panel.handle, 0x007F, 1, 0) or win32gui.SendMessage(panel.handle, 0x007F, 0, 0)
                taskbar = desktop.window(class_name="Shell_TrayWnd")
                if taskbar.exists():
                    assert not any("sell fish" in n.window_text().lower() for n in taskbar.descendants(control_type="Button"))
                second = subprocess.run([str(exe)], cwd=exe.parent, env=env, timeout=15)
                assert second.returncode == 0 and controller.poll() is None
                preview = os.environ.get("SELL_FISH_MINI_PREVIEW")
                if preview: panel.capture_as_image().save(preview)
                print("PASS fixed mini frame, icon, controller absent from taskbar, duplicate launch", size(), flush=True)
                control("ExpandButton").invoke()
                wait(lambda: size() == full, "restore full controller dimensions")
            # Drag from a blank point inside the client, not from a control or resize border.
            x, y = win32gui.ClientToScreen(panel.handle, (9, 14))
            before = win32gui.GetWindowRect(panel.handle)
            mouse.press(coords=(x, y))
            try: mouse.move(coords=(x + 42, y + 35))
            finally: mouse.release(coords=(x + 42, y + 35))
            wait(lambda: win32gui.GetWindowRect(panel.handle)[:2] != before[:2], "drag panel by blank background")
            time.sleep(.15)
            released = win32gui.GetWindowRect(panel.handle)[:2]
            mouse.move(coords=(x + 70, y + 50))
            time.sleep(.15)
            assert win32gui.GetWindowRect(panel.handle)[:2] == released, "movement must stop on button release"
            choose(panel, control("ThemeBox"), "黑")
            wait(lambda: (profile/"theme.txt").read_text(encoding="utf8").strip()=="dark", "black theme persists")
            probe_mini()
            choose(panel, control("ThemeBox"), "白")
            choose(panel, combo, name)
            hidden = control("HiddenBox")
            wait(hidden.is_enabled, "target chosen in fixed binding list")
            control("TopmostBox").toggle()
            style = lambda: win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
            wait(lambda: style() & win32con.WS_EX_TOPMOST, "target placed on top")
            box = control("OpacityInput")
            box.set_edit_text("37")
            wait(lambda: alpha(hwnd)==94, "typed 37 percent")
            # Repeated target-owned alpha resets are covered by the pixel-based native fixture.
            press_box = control("ClickThroughBox")
            press_box.toggle()
            wait(lambda: style() & win32con.WS_EX_TRANSPARENT and alpha(hwnd)==94, "through plus opacity")
            chord(initial_pass)
            wait(lambda: not (style() & win32con.WS_EX_TRANSPARENT), "initial passthrough hotkey")
            control("InvertBox").toggle()
            control("GrayBox").toggle()
            wait(lambda: alpha(hwnd)==1, "both filters active")
            control("ZeroButton").invoke()
            wait(lambda: alpha(hwnd)==0, "zero across both filters")
            control("ZeroButton").invoke()
            wait(lambda: alpha(hwnd)==1, "restore filters after zero")
            # Test the recorder with real keys, including another registered shortcut.
            hide_field = control("HideShortcutBox")
            hide_field.click_input()
            wait(lambda: hide_field.get_value()=="", "click clears shortcut editor")
            chord(initial_pass)
            time.sleep(.15)
            assert read_setting(hide_key) == initial_hide and not (style() & win32con.WS_EX_TRANSPARENT)
            hide_field.click_input()
            chord(new_hide)
            wait(lambda: read_setting(hide_key)==new_hide, "record hide shortcut and auto-save")
            assert win32gui.IsWindowVisible(hwnd)
            pass_field = control("ThroughShortcutBox")
            pass_field.click_input()
            chord(new_pass)
            wait(lambda: read_setting(pass_key)==new_pass, "record passthrough shortcut and auto-save")
            chord(new_pass)
            wait(lambda: style() & win32con.WS_EX_TRANSPARENT, "custom through shortcut with filters")
            def taskbar_entry():
                taskbar = desktop.window(class_name="Shell_TrayWnd")
                return any(n.element_info.automation_id == name or name in n.window_text()
                           for n in taskbar.descendants(control_type="Button"))
            had_entry = taskbar_entry()
            chord(new_hide)
            wait(lambda: not win32gui.IsWindowVisible(hwnd), "custom shortcut hides the actual window")
            if had_entry: wait(lambda: not taskbar_entry(), "Explorer removed the taskbar button")
            chord(new_hide)
            wait(lambda: win32gui.IsWindowVisible(hwnd), "custom shortcut restores the actual window")
            if had_entry: wait(taskbar_entry, "Explorer restored the taskbar button")
            choose(panel, combo, "释放")
            wait(lambda: alpha(hwnd)==255 and not (style() & (8|0x20|0x80000)), "release restores native window styles")
            choose(panel, combo, name)
            (profile/"mini-toolbar.json").write_text('["topmost", "clickThrough", "hide"]', encoding="utf8")
            control("CollapseButton").invoke()
            wait(lambda: spec.child_window(auto_id="Mini.hide").exists(), "open customized toolbar")
            actions = [c.element_info.automation_id for c in panel.descendants(control_type="CheckBox") if c.is_visible()]
            assert actions == ["Mini.topmost", "Mini.clickThrough", "Mini.hide"], actions
            control("Mini.topmost").toggle()
            wait(lambda: style() & win32con.WS_EX_TOPMOST, "mini button controls target z-order")
            control("Mini.clickThrough").toggle()
            wait(lambda: style() & win32con.WS_EX_TRANSPARENT, "mini button enables passthrough")
            control("Mini.hide").toggle()
            wait(lambda: not win32gui.IsWindowVisible(hwnd), "mini button hides target")
            control("ExpandButton").invoke()
            wait(lambda: hidden.get_toggle_state()==1 and press_box.get_toggle_state()==1, "main and mini controls share state")
            control("CollapseButton").invoke()
            control("ExitButton").invoke()
            wait(lambda: controller.poll() is not None, "close compact panel")
            wait(lambda: win32gui.IsWindowVisible(hwnd) and not (style() & (8|0x20|0x80000)), "exit restores hidden mouse-transparent target")
    finally:
        if controller is not None and controller.poll() is None:
            for w in Desktop(backend="win32").windows(process=controller.pid, visible_only=False):
                if w.class_name()=="WinUIDesktopWin32WindowClass": w.post_message(win32con.WM_CLOSE)
            try: controller.wait(timeout=5)
            except subprocess.TimeoutExpired: controller.terminate()
        target.terminate()
        target.wait()

if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve())
