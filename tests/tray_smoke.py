"""Check a disposable instance's Explorer icon. --shell-click briefly uses the pointer on the input desktop."""
import argparse
import ctypes as ct
from ctypes import wintypes as wt
import os
from pathlib import Path
import subprocess
import tempfile

import win32api
import win32gui as gui
import win32process

from mini_toolbar_smoke import controller
from ui_smoke import desktop, wait
from comtypes import COMError
from pywinauto import mouse


class IconIdentifier(ct.Structure):
    _fields_ = [("size", wt.DWORD), ("hwnd", wt.HWND), ("id", wt.UINT), ("guid", wt.BYTE * 16)]


class IconData(ct.Structure):
    _fields_ = [
        ("size", wt.DWORD), ("hwnd", wt.HWND), ("id", wt.UINT), ("flags", wt.UINT),
        ("callback", wt.UINT), ("icon", wt.HICON), ("tip", wt.WCHAR * 128),
        ("state", wt.DWORD), ("mask", wt.DWORD), ("info", wt.WCHAR * 256),
        ("version", wt.UINT), ("title", wt.WCHAR * 64), ("info_flags", wt.DWORD),
        ("guid", wt.BYTE * 16), ("balloon", wt.HICON),
    ]


shell = ct.WinDLL("shell32")
shell.Shell_NotifyIconGetRect.argtypes = [ct.POINTER(IconIdentifier), ct.POINTER(wt.RECT)]
shell.Shell_NotifyIconGetRect.restype = ct.c_long
shell.Shell_NotifyIconW.argtypes = [wt.DWORD, ct.POINTER(IconData)]
shell.Shell_NotifyIconW.restype = wt.BOOL


def icon_rect(hwnd):
    key = IconIdentifier(ct.sizeof(IconIdentifier), hwnd, 1)
    rect = wt.RECT()
    if shell.Shell_NotifyIconGetRect(ct.byref(key), ct.byref(rect)) < 0:
        return None
    return rect if rect.right > rect.left and rect.bottom > rect.top else None


def click_explorer_icon(hwnd):
    taskbar = gui.FindWindow("Shell_TrayWnd", None)
    explorer_pid = win32process.GetWindowThreadProcessId(taskbar)[1]
    expected = "sell fish · 单击找回主面板"

    def button():
        rect = icon_rect(hwnd)
        if rect is None:
            return None
        x, y = (rect.left + rect.right) // 2, (rect.top + rect.bottom) // 2
        # Only click the shell button at this test HWND's registered icon position.
        # Some Windows versions return the chevron until its overflow is open.
        element = desktop.from_point(x, y)
        return (x, y) if element.element_info.process_id == explorer_pid and element.window_text() == expected else None

    target = button()
    if target is None:
        controls = desktop.window(handle=taskbar).descendants(control_type="Button")
        chevron = next(c for c in controls if c.element_info.automation_id == "1502"
                       or "chevron" in c.element_info.automation_id.lower()
                       or c.window_text() in ("显示隐藏的图标", "Show hidden icons"))
        chevron.iface_invoke.Invoke()
        wait(button, "the test icon is visible in Explorer's overflow")
    point = button()
    assert point, "the test icon moved before clicking"
    original = win32api.GetCursorPos()
    try:
        mouse.click(coords=point)
    finally:
        if win32api.GetCursorPos() == point:
            win32api.SetCursorPos(original)


def test(exe, profile, shell_click):
    assert gui.FindWindow("Shell_TrayWnd", None), "requires a desktop with Explorer's taskbar"
    for name in ("shortcut.json", "passthrough-shortcut.json"):
        (profile / name).write_text("null", encoding="utf8")
    callback = gui.RegisterWindowMessage("WindowPilot.NotificationArea.Callback")
    created = gui.RegisterWindowMessage("TaskbarCreated")
    foreground = gui.GetForegroundWindow()
    try:
        with controller(exe, profile) as panel:
            hwnd = panel.handle
            wait(lambda: icon_rect(hwnd), "Explorer registered the test panel's notification icon")
            panel.child_window(auto_id="SettingsTab").wrapper_object().iface_selection_item.Select()

            def collapse():
                try:
                    panel.child_window(auto_id="CollapseButton").wrapper_object().iface_invoke.Invoke()
                except COMError as error:
                    # WinUI may remove the invoking peer before UIA finishes delivering its event.
                    if error.hresult != -2147220991:  # EVENT_E_ALL_SUBSCRIBERS_FAILED
                        raise
                wait(lambda: gui.GetWindowRect(hwnd)[3] - gui.GetWindowRect(hwnd)[1] < 150,
                     "controller collapsed")

            def expanded():
                return panel.child_window(auto_id="MiniStyleBox").exists(timeout=.15)

            collapse()
            if shell_click:
                click_explorer_icon(hwnd)
                wait(expanded, "Explorer's own notification button reopened Settings")
                wait(lambda: gui.GetForegroundWindow() == hwnd, "recovered panel is in the foreground")
                collapse()
            gui.PostMessage(hwnd, callback, 0, (1 << 16) | 0x401)  # NOTIFYICON_VERSION_4, NIN_KEYSELECT
            wait(expanded, "keyboard selection reopened Settings")

            # Reproduce loss of just this icon; never restart the user's Explorer.
            data = IconData(ct.sizeof(IconData), hwnd, 1)
            assert shell.Shell_NotifyIconW(2, ct.byref(data)), "could not remove the test icon"
            wait(lambda: icon_rect(hwnd) is None, "removed the test icon to simulate Explorer losing it")
            gui.PostMessage(hwnd, created, 0, 0)
            wait(lambda: icon_rect(hwnd), "TaskbarCreated restored the icon in Explorer")

            collapse()
            subprocess.run([str(exe)], cwd=exe.parent, check=True, timeout=15,
                           env={**os.environ, "WINDOWPILOT_PROFILE_DIR": str(profile)})
            wait(expanded, "second launch reveals the same panel and Settings page")
        wait(lambda: icon_rect(hwnd) is None, "exit removed the registered icon")
    finally:
        if foreground and gui.IsWindow(foreground):
            ct.windll.user32.SetForegroundWindow(foreground)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", type=Path)
    parser.add_argument("--shell-click", action="store_true", help="click the actual Explorer notification button")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="sell-fish-tray-") as folder:
        test(args.exe.resolve(), Path(folder), args.shell_click)
