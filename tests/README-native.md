# Native window integration tests

Run on an interactive Windows desktop with the .NET 11 SDK. The SDK includes the Windows Desktop runtime used by this test harness. The tests do not require Windows App SDK or application NuGet dependencies.

From the WindowPilot source directory in PowerShell:

```powershell
dotnet run --project .\tests\NativeTests.csproj -c Release -- .\tests\native-test-results.json
```

The harness creates two disposable windows in separate child processes with unique application IDs. It enumerates existing desktop windows for discovery, but all mutations target only those child processes. The windows close in the cleanup block and also have a two-minute fallback timer. Test recovery state uses a unique temporary file, separate from the application's recovery record.

The assertions cover:

- Window identity, single binding, and restoration when rebinding or releasing.
- Topmost changes without activating the target.
- Exact opacity bytes, including zero, byte one for effects, and original alpha/color-key/flags restoration.
- A fixture that overwrites its alpha on every mouse move. Desktop pixel samples reproduce the former full-opacity flash and confirm 24 samples at the requested 38% after switching to independent composition. The original remains opaque and interactive.
- A live background color change, removal of the target's layered style, and compatibility-mode zero, hide/show and click-through transitions.
- Real mouse clicks through a visible semitransparent fixture into a second process underneath, followed by restored hit testing when pass-through is disabled.
- Click-through for a window using per-pixel alpha while preserving that window's native rendering API.
- Real target visibility and retention of the same bound HWND through hide/show.
- Normal, minimized and maximized placement preservation.
- Explorer's real running taskbar buttons changing from two test buttons to one and back to two.
- Recovery after a simulated controller restart, stale window-property rejection, and refusal to alter an unreadable per-pixel layered surface.
- A bounded hide timeout for an intentionally stalled child UI thread, followed by successful recovery.

Taskbar observations use read-only UI Automation and retain only the test application's button data in the JSON report. If another Windows shell/grouping arrangement does not expose two distinct test buttons, those taskbar assertions are recorded under `skipped`. The native window assertions still run. Run on an available desktop because the pointer-routing check moves the cursor briefly over its two fixtures and restores the cursor position.

`SW_HIDE` removes a running window's actual taskbar representation. A separately pinned launcher icon may remain by Windows design. The service deliberately avoids `ITaskbarList.AddTab`, which could create a button for an owned or tool window that never originally had one.

The test exits with code zero on success and one on failure. The JSON report records assertions, skips, platform, timestamp and error details.
