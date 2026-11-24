"""Exercise the production detached views, broker, packaged stdio host and browser DOM adapter.

Supply the fixture executable built by Prepare.ps1. SELL_FISH_PLAYWRIGHT may point
to a local playwright-core install. Retail Edge/Chrome run headless on localhost.
The coordinator substitutes the extension transport and the desktop foreground
pairing gesture. Test input is two Chinese text lines delivered to the WinUI editor.
"""
import argparse
import asyncio
from contextlib import suppress
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import threading
import uuid


FIXTURES = Path(__file__).resolve().parent.parent / "browser"
ORIGIN = "chrome-extension://kahblpkceponikaklinoblfmgdnahdfc/"


async def send_frame(output, message):
    frame = json.dumps(message, ensure_ascii=False).encode("utf-8")
    output.write(struct.pack("<I", len(frame)) + frame)
    await output.drain()


async def read_frame(stream):
    size, = struct.unpack("<I", await stream.readexactly(4))
    if not 0 < size <= 950 * 1024:
        raise ValueError("Invalid native messaging frame")
    return json.loads(await stream.readexactly(size))


async def line(driver, data):
    driver.stdin.write(json.dumps(data, ensure_ascii=False).encode("utf-8") + b"\n")
    await driver.stdin.drain()


async def wait_file(path, process):
    while not path.exists():
        if process.returncode is not None:
            raise RuntimeError(f"Probe exited {process.returncode} before publishing {path.name}")
        await asyncio.sleep(.05)


async def exercise(exe, browser, origin, profile):
    session = uuid.uuid4().hex
    env = {**os.environ, "WINDOWPILOT_PROFILE_DIR": str(profile), "SELL_FISH_TEST_BROWSER": browser}
    flags = subprocess.CREATE_NO_WINDOW
    for name, key in (("shortcut.json", 121), ("passthrough-shortcut.json", 120)):
        (profile / name).write_text(json.dumps({"Modifiers": 7, "Key": key}), encoding="utf8")
    panel = await asyncio.create_subprocess_exec(str(exe), cwd=exe.parent, env=env, creationflags=flags)
    host = driver = None
    jobs = []
    counts = asyncio.Queue()
    try:
        await wait_file(profile / "armed", panel)
        host = await asyncio.create_subprocess_exec(str(exe.parent / "SellFish.BrowserHost.exe"), ORIGIN,
            cwd=exe.parent, env=env, stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, creationflags=flags)
        await send_frame(host.stdin, {"type":"pair", "session":session, "browser":browser, "windowId":1, "tabId":1, "documentId":"local-fixture"})
        assert (await read_frame(host.stdout))["type"] == "accept"
        driver = await asyncio.create_subprocess_exec("node", str(FIXTURES / "adapter-driver.mjs"),
            "--browser=" + browser, "--session=" + session, "--origin=" + origin,
            env=env, stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, creationflags=flags)

        async def page_to_host():
            while data := await driver.stdout.readline():
                packet = json.loads(data)
                if packet.get("test") == "count":
                    counts.put_nowait(packet["count"])
                else:
                    await send_frame(host.stdin, packet)

        async def host_to_page():
            try:
                while True:
                    await line(driver, await read_frame(host.stdout))
            except asyncio.IncompleteReadError:
                return  # Closing the last desktop pairing ends the native host.

        jobs = [asyncio.create_task(page_to_host()), asyncio.create_task(host_to_page())]
        await wait_file(profile / "windows-ready", panel)
        await line(driver, {"test": "background"})
        await line(driver, {"test": "count"})
        assert await counts.get() == 0
        (profile / "background").touch()
        await wait_file(profile / "desktop-result.txt", panel)
        report = (profile / "desktop-result.txt").read_text(encoding="utf-8-sig")
        assert report == "PASS", report
        await line(driver, {"test": "count"})
        assert await counts.get() == 1
        print(browser + ": " + (profile / "assertions.txt").read_text(encoding="utf-8-sig").strip().replace("\n", "\n  "))
        print(f"  two Chinese lines acknowledged and exactly one submission in the background webpage", flush=True)
    finally:
        for j in jobs:
            j.cancel()
        if jobs:
            await asyncio.gather(*jobs, return_exceptions=True)
        if driver and driver.returncode is None:
            with suppress(BrokenPipeError, ConnectionResetError):
                await line(driver, {"test": "close"})
        for child in (driver, host, panel):
            if child and child.returncode is None:
                try:
                    await asyncio.wait_for(child.wait(), 4)
                except TimeoutError:
                    child.terminate()
                    await child.wait()


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("exe", type=Path)
    cli.add_argument("--browser", choices=("both", "edge", "chrome"), default="both")
    options = cli.parse_args()
    fixture = (FIXTURES / "fixture.html").read_bytes()
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.end_headers()
            self.wfile.write(fixture)
        def log_message(self, *args):
            pass
    with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
        producer = threading.Thread(target=server.serve_forever, daemon=True)
        producer.start()
        try:
            for browser in (("edge", "chrome") if options.browser == "both" else (options.browser,)):
                with tempfile.TemporaryDirectory(prefix="sell-fish-browser-qa-") as tmp:
                    profile = Path(tmp)
                    try:
                        asyncio.run(asyncio.wait_for(exercise(options.exe.resolve(), browser,
                            f"http://127.0.0.1:{server.server_port}", profile), 65))
                    except BaseException:
                        for result in ("desktop-result.txt", "assertions.txt"):
                            if (profile / result).exists(): print((profile / result).read_text(encoding="utf-8-sig"))
                        raise
        finally:
            server.shutdown()


if __name__ == "__main__":
    main()
