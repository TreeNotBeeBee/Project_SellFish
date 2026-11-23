"""Run on an available desktop: python tests/shortcut_smoke.py "path/to/sell fish.exe"."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

from pywinauto import mouse
import win32api
import win32con
import win32gui

from ui_smoke import choose, desktop, u, wait, write_setting


LCTRL, RCTRL, RALT = 0xA2, 0xA3, 0xA5


def edge(key, down):
    scan = u.MapVirtualKeyW(key, 4)
    flags = win32con.KEYEVENTF_EXTENDEDKEY if scan >> 8 == 0xE0 else 0
    if not down:
        flags |= win32con.KEYEVENTF_KEYUP
    win32api.keybd_event(key, scan & 0xFF, flags, 0)


def chord(*keys):
    for key in keys:
        edge(key, True)
    for key in reversed(keys):
        edge(key, False)


def free_keys():
    keys = []
    for vk in range(0x70, 0x7B):
        for mods in (2, 7):
            if not u.RegisterHotKey(None, 0x5419, mods, vk):
                break
            u.UnregisterHotKey(None, 0x5419)
        else:
            keys.append(vk)
        if len(keys) == 3:
            return keys
    raise RuntimeError("The test needs three free Ctrl+F-key and Ctrl+Alt+Shift+F-key combinations.")


def run(exe, profile, title):
    hide = profile / "shortcut.json"
    through = profile / "passthrough-shortcut.json"
    initial_hide, initial_through, letter = free_keys()
    write_setting(hide, initial_hide)
    write_setting(through, initial_through)
    env = {**os.environ, "WINDOWPILOT_PROFILE_DIR": str(profile)}
    controller = subprocess.Popen([str(exe)], cwd=exe.parent, env=env)
    spec = desktop.window(process=controller.pid, class_name="WinUIDesktopWin32WindowClass")
    try:
        spec.wait("visible", timeout=40)
        panel = spec.wrapper_object()
        hwnd = win32gui.FindWindow(None, title)

        def field(auto_id):
            return spec.child_window(auto_id=auto_id, found_index=0).wrapper_object()

        def focus(handle=panel.handle):
            # Wait for native show/activation to settle. Send clicks only to a
            # blank point owned by one of this test's two disposable windows.
            def activate():
                if win32gui.GetForegroundWindow() == handle:
                    return True
                point = (7, 9) if handle == panel.handle else (24, 32)
                pt = win32gui.ClientToScreen(handle, point)
                if win32gui.GetAncestor(win32gui.WindowFromPoint(pt), 2) == handle:
                    mouse.click(coords=pt)
                else:
                    desktop.window(handle=handle).set_focus()
                return win32gui.GetForegroundWindow() == handle
            wait(activate, "test window has foreground focus")

        def record(auto_id, path, *keys):
            box = field(auto_id)
            box.click_input()
            wait(lambda: box.get_value() == "", "recorder is listening")
            assert win32gui.GetForegroundWindow() == panel.handle
            chord(*keys)
            wait(lambda: json.loads(path.read_text(encoding="utf-8-sig"))["Key"] == keys[-1]
                 and box.get_value() != "", "shortcut recorded")
            return box.get_value()

        def through_enabled():
            return bool(win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) & win32con.WS_EX_TRANSPARENT)

        def unchanged(message):
            time.sleep(.18)
            assert win32gui.IsWindowVisible(hwnd) and not through_enabled(), message
            print("PASS", message, flush=True)

        choose(panel, field("TargetBox"), title)
        wait(field("HiddenBox").is_enabled, "bound to disposable target")
        assert "左 Ctrl" in record("ThroughShortcutBox", through, LCTRL)
        assert "右 Ctrl" in record("HideShortcutBox", hide, RCTRL)
        focus()
        edge(LCTRL, True)
        try:
            unchanged("holding left Ctrl does not fire early")
            edge(LCTRL, True)
        finally:
            edge(LCTRL, False)
        wait(through_enabled, "left Ctrl tap with autorepeat fires once on release")
        chord(LCTRL)
        wait(lambda: not through_enabled(), "left Ctrl tap restores pointer input")

        chord(RCTRL)
        wait(lambda: not win32gui.IsWindowVisible(hwnd), "right Ctrl fires only the hide binding")
        chord(RCTRL)
        wait(lambda: win32gui.IsWindowVisible(hwnd), "right Ctrl unhides target")
        # Send the ordinary editing chord to our empty Tk fixture, not to an
        # editor with a selection or to another application on the test desktop.
        focus(hwnd)
        chord(LCTRL, ord("C"))
        chord(RCTRL, ord("C"))
        chord(LCTRL, RCTRL)
        unchanged("Ctrl+C and two modifiers do not fire a tap")

        # The panel's blank corner is a drag surface. Use a stationary click only.
        focus()
        pt = win32gui.ClientToScreen(panel.handle, (7, 9))
        assert win32gui.GetAncestor(win32gui.WindowFromPoint(pt), 2) == panel.handle
        edge(LCTRL, True)
        try:
            mouse.click(coords=pt)
        finally:
            edge(LCTRL, False)
        unchanged("Ctrl+mouse click does not fire a tap")

        # The same standalone shortcut cannot replace the other action.
        previous = hide.read_bytes()
        field("HideShortcutBox").click_input()
        chord(LCTRL)
        wait(lambda: "右 Ctrl" in field("HideShortcutBox").get_value(), "duplicate binding rejected")
        assert hide.read_bytes() == previous

        assert "右 Alt" in record("ThroughShortcutBox", through, RALT)
        focus()
        chord(RALT)
        wait(through_enabled, "right Alt tap activates")
        chord(RALT)
        wait(lambda: not through_enabled(), "right Alt tap toggles off")

        assert "左 Ctrl" in record("ThroughShortcutBox", through, LCTRL, letter)
        assert "右 Ctrl" in record("HideShortcutBox", hide, RCTRL, letter)
        focus()
        chord(LCTRL, letter)
        wait(through_enabled, "left Ctrl chord recognized")
        assert win32gui.IsWindowVisible(hwnd)
        chord(RCTRL, letter)
        wait(lambda: not win32gui.IsWindowVisible(hwnd), "opposite-side chord can bind a different action")
        chord(RCTRL, letter)
        wait(lambda: win32gui.IsWindowVisible(hwnd), "right Ctrl chord restores target")
    finally:
        if controller.poll() is None and spec.exists(timeout=.5):
            win32gui.PostMessage(spec.handle, win32con.WM_CLOSE, 0, 0)
        try:
            controller.wait(timeout=5)
        except subprocess.TimeoutExpired:
            controller.terminate()
            controller.wait(timeout=5)


def main(exe):
    title = "0001-Shortcut-QA-" + uuid.uuid4().hex[:8]
    target = subprocess.Popen([sys.executable, str(Path(__file__).with_name("ui_smoke.py")), "--target", title])
    try:
        wait(lambda: win32gui.FindWindow(None, title), "disposable target starts")
        with tempfile.TemporaryDirectory() as directory:
            run(exe, Path(directory), title)
    finally:
        target.terminate()
        target.wait(timeout=5)


if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve())
