"""Desktop acceptance test using only a disposable target created by this script."""
import ctypes
import json
import os
import pathlib
import subprocess
import sys
import time
import uuid
from ctypes import wintypes

if len(sys.argv) > 1 and sys.argv[1] == "--target":
    import tkinter as tk
    ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(sys.argv[2])
    root = tk.Tk()
    root.title(sys.argv[2])
    root.geometry("280x180+16+90")
    root.configure(background="#CE2946")
    root.mainloop()
    raise SystemExit

from pywinauto import Desktop

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.FindWindowW.argtypes = [wintypes.LPCWSTR, wintypes.LPCWSTR]
user32.FindWindowW.restype = wintypes.HWND
user32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
user32.IsWindowVisible.argtypes = [wintypes.HWND]

def wait(check, label, timeout=18):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        value = check()
        if value:
            print("PASS", label, flush=True)
            return value
        time.sleep(0.15)
    raise RuntimeError("Timed out: " + label)

target_name = "WindowPilot-Smoke-" + uuid.uuid4().hex[:8]
if Desktop(backend="win32").windows(title="窗口轻控 · WindowPilot"):
    raise SystemExit("Close the controller before running this standalone acceptance test.")
recovery = pathlib.Path(os.environ["LOCALAPPDATA"]) / "WindowPilot" / "recovery.json"
if recovery.exists() and json.loads(recovery.read_text(encoding="utf-8-sig")) is not None:
    raise SystemExit("A recovery record exists. Restore that window from the controller first.")
target_process = subprocess.Popen([sys.executable, __file__, "--target", target_name])
app = None
shortcut_path = pathlib.Path(os.environ["LOCALAPPDATA"]) / "WindowPilot" / "shortcut.json"
fresh_config = not shortcut_path.exists()
try:
    target = wait(lambda: user32.FindWindowW(None, target_name), "fixture created")
    exe = pathlib.Path(sys.argv[1]).resolve()
    app = subprocess.Popen([str(exe)], cwd=exe.parent)
    desktop = Desktop(backend="uia")
    def taskbar_button():
        taskbar = desktop.window(class_name="Shell_TrayWnd")
        return any(entry.element_info.automation_id == target_name or target_name in entry.window_text()
                   for entry in taskbar.descendants(control_type="Button"))
    surface = desktop.window(process=app.pid, title="窗口轻控 · WindowPilot", class_name="WinUIDesktopWin32WindowClass")
    surface.wait("visible", timeout=45)
    combo = surface.child_window(auto_id="TargetBox", control_type="ComboBox").wrapper_object()
    def bind_fixture():
        combo.expand()
        time.sleep(.35)
        for node in surface.descendants(control_type="ListItem"):
            if target_name not in node.window_text() or not node.is_visible(): continue
            rect = node.rectangle()
            if rect.width() <= 0 or rect.height() <= 0: continue
            hit = desktop.from_point(rect.mid_point().x, rect.mid_point().y)
            if hit.element_info.process_id != app.pid: continue
            node.click_input()
            return
        raise RuntimeError("Disposable target's list item is not visible in the controller")
    bind_fixture()
    vis = surface.child_window(auto_id="VisibilityButton", control_type="Button")
    wait(lambda: vis.is_enabled(), "bound from dropdown")
    surface.child_window(auto_id="TopmostBox", control_type="CheckBox").toggle()
    wait(lambda: user32.GetWindowLongW(target, -20) & 8, "UI topmost")
    taskbar_visible = taskbar_button()
    vis.invoke()
    wait(lambda: not user32.IsWindowVisible(target), "UI hide")
    if taskbar_visible:
        wait(lambda: not taskbar_button(), "Explorer removed target's taskbar button")
    vis.invoke()
    wait(lambda: user32.IsWindowVisible(target), "UI show")
    if taskbar_visible:
        wait(taskbar_button, "Explorer restored target's taskbar button")
    else:
        print("Taskbar button not exposed by title on this shell", flush=True)
    label = surface.child_window(auto_id="ShortcutInfo").window_text()
    if "Ctrl + Shift + H" in label:
        import win32api
        import win32con
        def press(main=72):
            for key in [17, 16, main]:
                win32api.keybd_event(key, 0, 0, 0)
            for key in [main, 16, 17]:
                win32api.keybd_event(key, 0, win32con.KEYEVENTF_KEYUP, 0)
        press()
        wait(lambda: not user32.IsWindowVisible(target), "physical global shortcut hides bound target")
        press()
        wait(lambda: user32.IsWindowVisible(target), "physical global shortcut restores bound target")
        if fresh_config:
            selector = surface.child_window(auto_id="ShortcutKey",control_type="ComboBox").wrapper_object()
            selector.expand()
            time.sleep(.3)
            selector.type_keys("{HOME}" + "{DOWN}"*9 + "{ENTER}", pause=.04)
            save = surface.child_window(title="保存快捷键", control_type="Button")
            if not user32.RegisterHotKey(None, 1773, 6, ord("J")):
                raise RuntimeError("Test shortcut Ctrl+Shift+J already occupied")
            try:
                save.invoke()
                wait(lambda: "无法注册" in surface.child_window(auto_id="StatusText").window_text(), "shortcut conflict reported")
                press()
                wait(lambda: not user32.IsWindowVisible(target), "old shortcut survives conflict")
            finally:
                user32.UnregisterHotKey(None, 1773)
            save.invoke()
            wait(lambda: "Ctrl + Shift + J" in surface.child_window(auto_id="ShortcutInfo").window_text(), "replacement shortcut registered")
            press(ord("J"))
            wait(lambda: user32.IsWindowVisible(target), "custom shortcut restores target")
            saved = json.loads(shortcut_path.read_text(encoding="utf-8-sig"))
            assert saved["Key"] == ord("J") and saved["Modifiers"] == 6
            print("PASS custom shortcut persisted", flush=True)
    else:
        print("Non-default shortcut active: " + label)
    zero = surface.child_window(auto_id="ZeroButton", control_type="Button")
    zero.invoke()
    def alpha():
        out = ctypes.c_ubyte()
        if user32.GetLayeredWindowAttributes(target, None, ctypes.byref(out), None):
            return out.value
        return 255
    wait(lambda: alpha() == 0, "one-click zero opacity")
    zero.invoke()
    wait(lambda: alpha() == 255, "one-click previous opacity")
    def overlay():
        w = user32.FindWindowW("WindowPilot.EffectOverlay.v1", None)
        return bool(w and user32.IsWindowVisible(w))
    surface.child_window(auto_id="InvertBox", control_type="CheckBox").toggle()
    wait(overlay, "inverted target displayed via controller")
    surface.child_window(auto_id="GrayBox", control_type="CheckBox").toggle()
    wait(lambda: alpha() == 1 and overlay(), "combined filters retain hittable source")
    zero.invoke()
    wait(lambda: alpha() == 0 and not overlay(), "zero with both filters")
    zero.invoke()
    wait(lambda: alpha() == 1 and overlay(), "filters resume after zero")
    vis.invoke()
    wait(lambda: not user32.IsWindowVisible(target) and not overlay(), "hide both target and filter")
    surface.child_window(auto_id="BindAction", control_type="Button").invoke()
    wait(lambda: user32.IsWindowVisible(target) and not user32.GetWindowLongW(target,-20) & 8, "release restores visibility and topmost")
    bind_fixture()
    wait(lambda: vis.is_enabled(), "rebound disposable target")
    surface.child_window(auto_id="TopmostBox", control_type="CheckBox").toggle()
    vis.invoke()
    wait(lambda: not user32.IsWindowVisible(target), "hide before app exit")
    user32.PostMessageW(surface.handle, 0x10, 0, 0)
    wait(lambda: app.poll() is not None, "control panel closed normally")
    wait(lambda: user32.IsWindowVisible(target) and not (user32.GetWindowLongW(target, -20) & 8), "normal exit restores target")
    print("UI SMOKE PASSED", flush=True)
finally:
    if app is not None and app.poll() is None:
        windows = Desktop(backend="win32").windows(process=app.pid,visible_only=False)
        for w in windows:
            if "WindowPilot" in w.window_text():
                w.post_message(0x0010)
        try:
            app.wait(7)
        except subprocess.TimeoutExpired:
            app.terminate()
    target_process.terminate()
    target_process.wait()
    if fresh_config and shortcut_path.exists():
        saved = json.loads(shortcut_path.read_text(encoding="utf-8-sig"))
        if saved["Key"] == ord("J") and saved["Modifiers"] == 6:
            shortcut_path.unlink()
