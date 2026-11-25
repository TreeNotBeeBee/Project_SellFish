"""Exercise the packaged UIA binding on a disposable Chromium profile and non-input desktop."""
import argparse
import ctypes
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import uuid

import win32con
import win32event
import win32gui
import win32job
import win32process
import pywintypes


HERE = Path(__file__).resolve().parent
TITLE = "sell fish UIA fixture"


def wait_for(fn, seconds=12):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = fn()
        if value:
            return value
        time.sleep(0.1)
    raise TimeoutError(fn.__name__)


def serve(bundle):
    state = {}
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            if self.path == "/":
                data, kind = (HERE / "page.html").read_bytes(), "text/html; charset=utf-8"
            elif self.path == "/editor.js":
                data, kind = bundle.read_bytes(), "text/javascript; charset=utf-8"
            elif self.path == "/state":
                data, kind = json.dumps(state).encode(), "application/json"
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_POST(self):
            if self.path != "/state":
                self.send_error(404)
                return
            record = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            state.update(record)
            self.send_response(204)
            self.end_headers()

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, state


def browser_window():
    windows = []
    def collect(hwnd, _):
        # Edge may relaunch its browser process. This desktop only contains the temporary profile.
        if win32gui.GetClassName(hwnd) == "Chrome_WidgetWin_1" and TITLE in win32gui.GetWindowText(hwnd):
            windows.append(hwnd)
    try:
        win32gui.EnumWindows(collect, None)
    except pywintypes.error as error:
        if error.winerror not in (0, 122, 1400):
            raise
    return next(iter(windows), 0)


def provider_baseline(hwnd, state):
    from pywinauto import Desktop
    from pywinauto.uia_element_info import UIAElementInfo
    window = Desktop(backend="uia").window(handle=hwnd)
    edit = window.child_window(control_type="Document").child_window(control_type="Edit").wrapper_object()
    element = edit.element_info.element
    print("Empty editor", repr(edit.iface_value.CurrentValue), repr(edit.iface_text.DocumentRange.GetText(-1)),
          repr(element.CurrentClassName), element.CurrentAriaRole, element.CurrentAriaProperties, state)
    doc = window.child_window(control_type="Document").wrapper_object()
    print("Document legacy value exposes URL", str(doc.element_info.element.GetCurrentPropertyValue(30093)).startswith("http://127.0.0.1:"))
    for wrapper in (doc, edit, window.child_window(title="First paragraph", control_type="Text").wrapper_object()):
        rect = wrapper.rectangle()
        x, y = (rect.left + 3, rect.bottom - 8) if wrapper is doc else (rect.left + 3, (rect.top + rect.bottom) // 2)
        for delay in (0, 0.05, 0.15):
            time.sleep(delay)
            hit = UIAElementInfo.from_point(x, y)
            print("Hit", x, y, delay, hit.control_type, hit.name)
    for name in ["First paragraph", "Latest answer", "发送"]:
        item = window.child_window(title=name, found_index=0).wrapper_object()
        path = []
        while item is not None:
            path.append((item.element_info.control_type, item.window_text()))
            if item.handle == hwnd:
                break
            item = item.parent()
        print("UIA", name, path, flush=True)
    for value in ["first", "第一行\n第二行", "\n故意空行\n"]:
        edit.iface_value.SetValue(value)
        time.sleep(0.6)
        print("SetValue", repr(value), "UIA", repr(edit.iface_value.CurrentValue), "editor", state, flush=True)


def document_point(document, origin):
    """Find a real hit on Chromium's Document, whose accessibility value is its URL."""
    from pywinauto.uia_element_info import UIAElementInfo
    wrapper = document.wrapper_object()
    element = wrapper.element_info.element
    address = element.GetCurrentPropertyValue(30093)  # LegacyIAccessible.Value
    if address != origin + "/":
        raise AssertionError(f"Unexpected test document address: {address!r}")
    rect = wrapper.rectangle()
    for y in (rect.bottom - 12, (rect.top + rect.bottom) // 2, rect.top + 1):
        for x in ((rect.left + rect.right) // 2, rect.left + 4, rect.right - 20):
            if UIAElementInfo.from_point(x, y).control_type == "Document":
                return x, y
    raise AssertionError("Browser provider did not expose a Document hit in the test viewport")


def child(args, root):
    import contextlib
    with (root / "driver.log").open("w", encoding="utf8") as log, contextlib.redirect_stdout(log), contextlib.redirect_stderr(log):
        server, state = serve(args.bundle)
        origin = "http://127.0.0.1:" + str(server.server_port)
        flags = ["--force-renderer-accessibility"] if args.force_accessibility else []
        proc = subprocess.Popen([str(args.browser), "--user-data-dir=" + str(root / "profile"), "--no-first-run",
                                 "--no-default-browser-check", "--disable-extensions", *flags,
                                 "--window-size=930,750", "--app=" + origin])
        hwnd = 0
        try:
            hwnd = wait_for(browser_window, 20)
            wait_for(lambda: "draft" in state)
            if args.baseline:
                provider_baseline(hwnd, state)
            else:
                from pywinauto import Desktop
                window = Desktop(backend="uia").window(handle=hwnd)
                document = window.child_window(control_type="Document")
                controls = [document.child_window(control_type="Edit"),
                            document.child_window(title="发送", control_type="Button"),
                            document.child_window(title="First paragraph", control_type="Text")]
                points = []
                for control in controls:
                    rect = control.wrapper_object().rectangle()
                    points.extend([rect.left + 3, (rect.top + rect.bottom) // 2])
                points.extend(document_point(document, origin))
                env = dict(os.environ, SELL_FISH_CHROMIUM_HWND=str(hwnd), SELL_FISH_CHROMIUM_ORIGIN=origin,
                           SELL_FISH_CHROMIUM_POINTS=",".join(map(str, points)),
                           SELL_FISH_CHROMIUM_SEND="automatic" if args.automatic else "explicit",
                           SELL_FISH_DETACHED_RESULT=str(root / "probe.log"))
                subprocess.run([str(args.probe), "--chromium-fixture"], env=env, timeout=60, check=True)
                result = (root / "probe.log").read_text(encoding="utf-8-sig")
                if not result.splitlines() or result.splitlines()[-1] != "PASS":
                    raise RuntimeError("WinUI probe did not complete all assertions")
                print(result)
            return 0
        except Exception:
            traceback.print_exc()
            if not hwnd:
                print("Test-desktop windows:")
                def record(window, _):
                    if win32gui.IsWindowVisible(window):
                        print(win32process.GetWindowThreadProcessId(window)[1], win32gui.GetWindowText(window))
                win32gui.EnumWindows(record, None)
            if (root / "probe.log").is_file():
                print((root / "probe.log").read_text(encoding="utf-8-sig"))
            return 1
        finally:
            if hwnd:
                win32gui.PostMessage(hwnd, win32con.WM_CLOSE, 0, 0)
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.terminate()
            server.shutdown()
            server.server_close()


def isolated(root):
    # lpDesktop isolates test windows without switching the user's input desktop.
    name = "SellFishUIA" + uuid.uuid4().hex
    ui = ctypes.WinDLL("user32", use_last_error=True)
    ui.CreateDesktopW.restype = ctypes.c_void_p
    desktop = ui.CreateDesktopW(name, None, None, 0, 0x10000000, None)
    if not desktop:
        raise ctypes.WinError(ctypes.get_last_error())
    job = win32job.CreateJobObject(None, name)
    try:
        # Both browsers can relaunch themselves and briefly leave children alive after the last window closes.
        limits = win32job.QueryInformationJobObject(job, win32job.JobObjectExtendedLimitInformation)
        limits["BasicLimitInformation"]["LimitFlags"] |= win32job.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        win32job.SetInformationJobObject(job, win32job.JobObjectExtendedLimitInformation, limits)
        start = win32process.STARTUPINFO()
        start.lpDesktop = "WinSta0\\" + name
        command = [sys.executable, "-X", "utf8", __file__, *sys.argv[1:], "--child", str(root)]
        proc, thread, _, _ = win32process.CreateProcess(sys.executable, subprocess.list2cmdline(command), None, None,
                                                         False, win32con.CREATE_NO_WINDOW | win32con.CREATE_SUSPENDED,
                                                         None, None, start)
        try:
            win32job.AssignProcessToJobObject(job, proc)
            win32process.ResumeThread(thread)
            if win32event.WaitForSingleObject(proc, 100000) == win32event.WAIT_TIMEOUT:
                win32process.TerminateProcess(proc, 2)
                raise TimeoutError("Chromium UIA test timed out")
            if (root / "driver.log").exists():
                print((root / "driver.log").read_text(encoding="utf8"))
            return win32process.GetExitCodeProcess(proc)
        finally:
            thread.Close()
            proc.Close()
    finally:
        job.Close()
        ui.CloseDesktop(ctypes.c_void_p(desktop))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--browser", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True, help="Bundled editor.js with ProseMirror")
    parser.add_argument("--automatic", action="store_true", help="Use the paired editor's local form to locate Send")
    parser.add_argument("--force-accessibility", action="store_true", help="Force Chromium accessibility at startup")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--probe", type=Path)
    mode.add_argument("--baseline", action="store_true")
    parser.add_argument("--child", type=Path, help=argparse.SUPPRESS)
    options = parser.parse_args()
    if options.child:
        sys.exit(child(options, options.child))
    with tempfile.TemporaryDirectory(prefix="sell-fish-browser-uia-", ignore_cleanup_errors=True) as folder:
        sys.exit(isolated(Path(folder)))
