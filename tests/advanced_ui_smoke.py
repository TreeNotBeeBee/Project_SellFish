"""Exercise the release UI and two real native edit controls using a private profile."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

import win32api
import win32con as wc
import win32gui as gui


def run_target(title):
    cls = gui.WNDCLASS()
    cls.hInstance = win32api.GetModuleHandle(None)
    cls.lpszClassName = title
    cls.lpfnWndProc = {wc.WM_DESTROY: lambda *_: gui.PostQuitMessage(0)}
    gui.RegisterClass(cls)
    window = gui.CreateWindow(title, title, wc.WS_OVERLAPPEDWINDOW, 36, 75, 310, 210, 0, 0, cls.hInstance, None)
    for index, text in enumerate(("first value", "second value")):
        gui.CreateWindow("EDIT", text, wc.WS_CHILD | wc.WS_VISIBLE | wc.WS_BORDER | wc.ES_AUTOHSCROLL,
                         15, 20 + 55 * index, 235, 30, window, 101 + index, cls.hInstance, None)
    gui.ShowWindow(window, wc.SW_SHOWNORMAL)
    gui.PumpMessages()


if len(sys.argv) > 1 and sys.argv[1] == "--target":
    run_target(sys.argv[2])
    raise SystemExit

from pywinauto import mouse
from ui_smoke import choose, desktop, wait, write_setting


def test(exe, profile, title):
    import win32process
    target = gui.FindWindow(None, title)
    fields = [gui.GetDlgItem(target, 101 + i) for i in range(2)]
    write_setting(profile / "shortcut.json", 0x76)
    write_setting(profile / "passthrough-shortcut.json", 0x77)
    (profile / "mini-appearance.json").write_text('{"Style":"classic"}', encoding="utf8")
    env = {**os.environ, "WINDOWPILOT_PROFILE_DIR": str(profile)}
    app = None

    def start():
        proc = subprocess.Popen([str(exe)], cwd=exe.parent, env=env)
        spec = desktop.window(process=proc.pid, title="sell fish", class_name="WinUIDesktopWin32WindowClass")
        spec.wait("visible", timeout=35)
        return proc, spec

    def pinned(hwnd):
        return bool(gui.GetWindowLong(hwnd, wc.GWL_EXSTYLE) & wc.WS_EX_TOPMOST)

    def floating_windows():
        windows = []
        def visit(hwnd, _):
            if (gui.IsWindowVisible(hwnd) and gui.GetClassName(hwnd) == "WinUIDesktopWin32WindowClass" and
                    win32process.GetWindowThreadProcessId(hwnd)[1] == app.pid and hwnd != panel.handle):
                windows.append(hwnd)
        gui.EnumWindows(visit, None)
        return windows

    try:
        app, panel = start()
        def element(key):
            return panel.child_window(auto_id=key, found_index=0).wrapper_object()
        def page(tab):
            element(tab).click_input()

        assert not pinned(panel.handle) and not pinned(target)
        page("SettingsTab")
        element("ToolPin").toggle()
        wait(lambda: pinned(panel.handle) and not pinned(target), "controller pin works without a target")
        page("BasicTab")
        choose(panel.wrapper_object(), element("TargetBox"), title)
        page("AdvancedTab")
        assert element("PickControl").is_enabled()
        assert not panel.child_window(auto_id="PairWeb").exists(timeout=0.5)
        for index, field in enumerate(fields):
            element("PickControl").invoke()
            wait(lambda: not element("PickControl").is_enabled(), "picker waits for a bound control")
            xy = gui.ClientToScreen(field, (25, 10))
            assert gui.WindowFromPoint(xy) == field, "fixture point must be exposed"
            mouse.click(coords=xy)
            wait(lambda: len(floating_windows()) == index + 1, "independent text window opened")
            assert all(pinned(h) for h in floating_windows())
        first, second = sorted(floating_windows(), key=lambda h: gui.GetWindowText(h))
        views = [desktop.window(handle=h) for h in (first, second)]
        view = next(v for v in views if v.child_window(auto_id="NativeText").wrapper_object().get_value() == "first value")
        view.child_window(auto_id="NativeText").wrapper_object().set_edit_text("控件一已更新")
        view.child_window(auto_id="ApplyNativeText").wrapper_object().invoke()
        source_edits = [desktop.window(handle=field).wrapper_object() for field in fields]
        wait(lambda: source_edits[0].get_value() == "控件一已更新" and source_edits[1].get_value() == "second value",
             "write reaches only the selected native control")
        gui.PostMessage(view.handle, wc.WM_CLOSE, 0, 0)
        wait(lambda: len(floating_windows()) == 1, "closing one view keeps its sibling")
        page("SettingsTab")
        element("ToolPin").toggle()
        wait(lambda: not pinned(panel.handle) and all(not pinned(h) for h in floating_windows()), "one switch unpins all tools")
        element("CollapseButton").invoke()
        element("ExpandButton").invoke()
        wait(lambda: element("ToolPin").is_visible(), "expand restores the settings tab")
        element("ToolPin").toggle()
        page("BasicTab")
        choose(panel.wrapper_object(), element("TargetBox"), "释放")
        wait(lambda: not floating_windows(), "release closes all detached views")
        element("ExitButton").invoke()
        app.wait(timeout=10)
        app, panel = start()
        assert element("TargetBox").is_visible() and pinned(panel.handle)
        print("PASS restart opens Basic and restores tool pin", flush=True)
        element("ExitButton").invoke()
        app.wait(timeout=10)
    finally:
        if app and app.poll() is None:
            gui.PostMessage(panel.handle, wc.WM_CLOSE, 0, 0)
            try: app.wait(timeout=5)
            except subprocess.TimeoutExpired: app.terminate()


if __name__ == "__main__":
    title = "000-UIA-" + uuid.uuid4().hex[:8]
    child = subprocess.Popen([sys.executable, __file__, "--target", title])
    try:
        wait(lambda: gui.FindWindow(None, title), "native test application starts")
        with tempfile.TemporaryDirectory() as temp:
            test(Path(sys.argv[1]).resolve(), Path(temp), title)
    finally:
        child.terminate()
        child.wait(timeout=5)
