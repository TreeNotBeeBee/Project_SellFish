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

from ui_smoke import alpha, choose, desktop, u, wait, write_setting


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


def close_panel(process, spec):
    if process.poll() is None and spec.exists(timeout=.5):
        win32gui.PostMessage(spec.handle, win32con.WM_CLOSE, 0, 0)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.terminate()
        process.wait(timeout=5)


def run(exe, profile, title):
    hide = profile / "shortcut.json"
    through = profile / "passthrough-shortcut.json"
    increase = profile / "opacity-increase-shortcut.json"
    decrease = profile / "opacity-decrease-shortcut.json"
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
            wait(lambda: path.exists() and (json.loads(path.read_text(encoding="utf-8-sig")) or {}).get("Key") == keys[-1]
                 and box.get_value() != "", "shortcut recorded")
            return box.get_value()

        def through_enabled():
            return bool(win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE) & win32con.WS_EX_TRANSPARENT)

        def unchanged(message):
            time.sleep(.18)
            assert win32gui.IsWindowVisible(hwnd) and not through_enabled(), message
            print("PASS", message, flush=True)

        assert field("OpacityUpShortcutBox").get_value() == field("OpacityDownShortcutBox").get_value() == ""
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

        record("OpacityUpShortcutBox", increase, LCTRL, initial_hide)
        record("OpacityDownShortcutBox", decrease, LCTRL, initial_through)
        field("OpacityInput").set_edit_text("95")
        wait(lambda: alpha(hwnd) == round(95 * 255 / 100), "95 percent fixture opacity")
        focus(hwnd)
        chord(LCTRL, initial_hide)
        wait(lambda: alpha(hwnd) == 255, "increase stops at 100 percent")
        chord(LCTRL, initial_through)
        wait(lambda: alpha(hwnd) == round(90 * 255 / 100), "decrease subtracts ten percentage points")
        assert win32gui.GetForegroundWindow() == hwnd
        field("OpacityInput").set_edit_text("4")
        wait(lambda: alpha(hwnd) == round(4 * 255 / 100), "four percent fixture opacity")
        chord(LCTRL, initial_through)
        wait(lambda: alpha(hwnd) == 0, "decrease stops at zero")
        chord(LCTRL, initial_hide)
        wait(lambda: alpha(hwnd) == round(10 * 255 / 100), "increase restores visible opacity from zero")

        # Attempt to record the increase chord on the hide field. The former binding must survive.
        previous = hide.read_bytes()
        box = field("HideShortcutBox")
        box.click_input()
        wait(lambda: box.get_value() == "", "recording cross-action conflict")
        chord(LCTRL, initial_hide)
        wait(lambda: "右 Ctrl" in box.get_value(), "opacity chord cannot replace hide")
        assert hide.read_bytes() == previous

        for stem, path in (("Hide", hide), ("Through", through), ("OpacityUp", increase), ("OpacityDown", decrease)):
            field(stem + "ShortcutClear").invoke()
            wait(lambda: json.loads(path.read_text(encoding="utf-8-sig")) is None and
                 field(stem + "ShortcutBox").get_value() == "", stem + " clear removes saved binding")
        focus(hwnd)
        for former_key in (initial_hide, initial_through):
            chord(LCTRL, former_key)
            time.sleep(.18)
            assert alpha(hwnd) == round(10 * 255 / 100)
        chord(RCTRL, letter)
        chord(LCTRL, letter)
        unchanged("cleared hide and passthrough chords no longer fire")
        record("OpacityUpShortcutBox", increase, RCTRL, initial_through)
        box = field("OpacityUpShortcutBox")
        box.click_input()
        wait(lambda: box.get_value() == "", "recording clear key")
        chord(win32con.VK_BACK)
        wait(lambda: json.loads(increase.read_text(encoding="utf-8-sig")) is None, "bare Backspace clears")
    finally:
        close_panel(controller, spec)
    # An explicitly cleared hide/through binding must remain empty across an actual process restart.
    controller = subprocess.Popen([str(exe)], cwd=exe.parent, env=env)
    spec = desktop.window(process=controller.pid, class_name="WinUIDesktopWin32WindowClass", title="sell fish")
    try:
        spec.wait("visible", timeout=40)
        for stem in ("Hide", "Through", "OpacityUp", "OpacityDown"):
            assert field(stem + "ShortcutBox").get_value() == ""
        print("PASS all four cleared shortcuts stay unassigned after restart", flush=True)
    finally:
        close_panel(controller, spec)


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
