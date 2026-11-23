using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using WindowPilot;

internal static class NativeTests
{
    private static readonly List<string> Passed = [];
    private static readonly List<string> Skipped = [];
    private static readonly Dictionary<string, string[]> Taskbar = [];
    private static readonly Native.WndProc WindowProc = Procedure;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--target") return TargetWindow(args[1]);
        var report = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "WindowPilot-native-tests.json");
        var journal = Path.Combine(Path.GetTempPath(), "WindowPilot-test-" + Guid.NewGuid().ToString("N") + ".json");
        var children = new List<Process>();
        WindowService? service = null;
        try
        {
            var titleA = "WindowPilot test A " + Guid.NewGuid().ToString("N")[..8];
            var titleB = "WindowPilot test B " + Guid.NewGuid().ToString("N")[..8];
            var (processA, hwndA) = StartTarget(titleA); children.Add(processA);
            var (processB, hwndB) = StartTarget(titleB); children.Add(processB);
            service = new WindowService(journal);
            var a = service.Enumerate().Single(x => x.Handle == hwndA);
            var b = service.Enumerate().Single(x => x.Handle == hwndB);
            Check(a.Title == titleA && a.ProcessId == processA.Id, "enumeration identifies external target");
            ExpectError(() => service.Bind(a with { ProcessId = uint.MaxValue }), "invalid identity rejected");
            service.Bind(a);
            Check(service.Target?.Handle == hwndA && service.IsAlive(), "one target bound");
            var originalStyle = Native.GetWindowLongPtr(hwndA, -20).ToInt64();
            var foreground = Native.GetForegroundWindow();
            service.SetTopmost(true);
            Check(service.IsTopmost && Native.GetForegroundWindow() == foreground, "topmost does not activate target");
            service.SetTopmost(false);
            Check(!service.IsTopmost, "topmost can be removed");
            service.SetOpacity(0);
            Check(Alpha(hwndA) == 0 && Native.IsWindowVisible(hwndA), "zero opacity is exact byte zero without hiding");
            service.SetOpacity(38);
            Check(Alpha(hwndA) == 97, "opacity percentage maps to native alpha");
            service.SetOpacityByte(1);
            Check(Alpha(hwndA) == 1, "effect source can use exact byte one");
            service.SetOpacity(0);
            Check(Alpha(hwndA) == 0, "rounded zero percent still updates byte one to zero");
            service.SetOpacity(100);
            Check(Alpha(hwndA) == 255, "full opacity is exact byte 255");

            var placement = Placement(hwndA);
            Taskbar["beforeHide"] = AwaitTaskbar(2);
            var observableTaskbar = Taskbar["beforeHide"].Length == 2;
            if (observableTaskbar) Check(true, "both disposable targets have real running taskbar buttons");
            else Skipped.Add("Taskbar UIA button-count assertions: shell did not expose two distinct test application buttons.");
            service.SetHidden(true);
            Check(service.IsHidden && !Native.IsWindowVisible(hwndA), "hide removes actual target window");
            Check(service.Target?.Handle == hwndA && service.Enumerate().Any(x => x.Handle == hwndA), "hidden bound target remains the same selectable target");
            if (observableTaskbar)
            {
                Taskbar["whileHidden"] = AwaitTaskbar(1);
                Check(Taskbar["whileHidden"].Length == 1, "hidden target running taskbar button absent while other target remains");
            }
            service.SetHidden(false);
            Check(Native.IsWindowVisible(hwndA) && SamePlacement(placement, Placement(hwndA)), "show restores the same window and normal placement");
            if (observableTaskbar)
            {
                Taskbar["afterShow"] = AwaitTaskbar(2);
                Check(Taskbar["afterShow"].Length == 2, "same running taskbar button returns");
            }

            Native.ShowWindowAsync(hwndA, 6);
            Wait(() => Native.IsIconic(hwndA));
            service.SetHidden(true); service.SetHidden(false);
            Check(Native.IsIconic(hwndA) && Native.IsWindowVisible(hwndA), "minimized state survives hide/show");
            Native.ShowWindowAsync(hwndA, 9); Wait(() => !Native.IsIconic(hwndA));
            Native.ShowWindowAsync(hwndA, 3); Wait(() => Native.IsZoomed(hwndA));
            service.SetHidden(true); service.SetHidden(false);
            Check(Native.IsZoomed(hwndA) && Native.IsWindowVisible(hwndA), "maximized state survives hide/show");
            Native.ShowWindowAsync(hwndA, 9); Wait(() => !Native.IsZoomed(hwndA));
            service.SetTopmost(true); service.SetOpacity(0); service.SetHidden(true);
            service.Bind(b);
            Check(Native.IsWindowVisible(hwndA) && Native.GetWindowLongPtr(hwndA, -20).ToInt64() == originalStyle,
                "rebinding restores old visibility, original layered style and original topmost");
            Check(service.Target?.Handle == hwndB, "rebinding controls only the new window");
            service.Release();
            Check(service.Target == null, "release removes binding");

            Native.SetWindowLongPtr(hwndA, -20, (nint)(originalStyle | 0x80000));
            Native.SetLayeredWindowAttributes(hwndA, 0x00FF00, 1, 3);
            service.Bind(a); service.SetOpacity(0);
            Check(Alpha(hwndA) == 0, "original alpha byte one can become exact zero");
            service.Release();
            Native.GetLayeredWindowAttributes(hwndA, out var color, out var alpha, out var flags);
            Check(color == 0x00FF00 && alpha == 1 && flags == 3, "release preserves exact original alpha, color key and flags");
            Native.SetLayeredWindowAttributes(hwndA, 0, 254, 2);
            service.Bind(a); service.SetOpacity(100);
            Check(Alpha(hwndA) == 255, "original alpha 254 can become exact full opacity");
            service.Release();
            Check(Alpha(hwndA) == 254, "release restores original alpha byte 254");
            Native.SetWindowLongPtr(hwndA, -20, (nint)originalStyle);

            service.Bind(a); service.SetTopmost(true); service.SetOpacity(0); service.SetHidden(true);
            using (var recovered = new WindowService(journal))
                Check(recovered.Target == null && Native.IsWindowVisible(hwndA)
                    && Native.GetWindowLongPtr(hwndA, -20).ToInt64() == originalStyle,
                    "restart journal safely restores a stranded hidden window");
            service.Release();

            service.Bind(a);
            var document = JsonDocument.Parse(File.ReadAllText(journal));
            var marker = document.RootElement.GetProperty("Marker").GetString()!;
            Native.RemoveProp(hwndA, marker);
            Check(!service.IsAlive(), "missing GUID marker rejects recycled/replaced window identity");
            ExpectError(() => service.SetTopmost(true), "stale bound identity cannot mutate target");
            service.Release();

            var originalStyleB = Native.GetWindowLongPtr(hwndB, -20).ToInt64();
            Native.SetWindowLongPtr(hwndB, -20, (nint)(originalStyleB | 0x80000));
            MakePerPixelSurface(hwndB);
            service.Bind(b);
            Check(service.Opacity == null, "unreadable layered surface rejects opacity changes");
            ExpectError(() => service.SetOpacity(50), "unsupported layered surface remains unmodified");
            service.Release();
            Native.SetWindowLongPtr(hwndB, -20, (nint)originalStyleB);

            service.Bind(a);
            Native.PostMessage(hwndA, 0x8040, 0, 0);
            Thread.Sleep(100);
            var clock = Stopwatch.StartNew();
            ExpectError(() => service.SetHidden(true), "hung target produces bounded hide timeout");
            Check(clock.ElapsedMilliseconds < 2000, "hung foreign UI cannot indefinitely block hide");
            Thread.Sleep(4500);
            service.SetHidden(false); service.Release();
            Check(Native.IsWindowVisible(hwndA), "hung target remains recoverable after it resumes");

            WriteReport(report, true, null);
            Console.WriteLine($"PASS: {Passed.Count} assertions; report {report}");
            return 0;
        }
        catch (Exception ex)
        {
            WriteReport(report, false, ex.ToString());
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { service?.Release(); } catch { }
            foreach (var child in children)
            {
                try { Native.PostMessage(child.MainWindowHandle, 0x10, 0, 0); } catch { }
                try { if (!child.WaitForExit(1000)) child.Kill(); } catch { }
                child.Dispose();
            }
            try { File.Delete(journal); } catch { }
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException("FAILED: " + name);
        Passed.Add(name); Console.WriteLine("PASS " + name);
    }
    private static void ExpectError(Action operation, string name)
    {
        try { operation(); } catch (InvalidOperationException) { Check(true, name); return; }
        throw new InvalidOperationException("FAILED (expected an error): " + name);
    }
    private static void Wait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < 3000) Thread.Sleep(30);
        if (!condition()) throw new TimeoutException("Target state did not settle.");
    }
    private static byte Alpha(nint hwnd)
    {
        if (!Native.GetLayeredWindowAttributes(hwnd, out _, out var alpha, out _)) throw new InvalidOperationException("Cannot read native alpha");
        return alpha;
    }
    private static void MakePerPixelSurface(nint hwnd)
    {
        var screen = Native.GetDC(0);
        var memory = Native.CreateCompatibleDC(screen);
        var info = new Native.BitmapInfo { Size = 40, Width = 480, Height = -240, Planes = 1, BitCount = 32 };
        var bitmap = Native.CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
        var previous = Native.SelectObject(memory, bitmap);
        try
        {
            var pixels = Enumerable.Repeat(unchecked((int)0xFF2468AA), 480 * 240).ToArray();
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var size = new Native.Point { X = 480, Y = 240 };
            var source = new Native.Point();
            var blend = new Native.Blend { Alpha = 255, Format = 1 };
            if (!Native.UpdateLayeredWindow(hwnd, 0, 0, ref size, memory, ref source, 0, ref blend, 2))
                throw new InvalidOperationException("Cannot create per-pixel layered fixture: " + Marshal.GetLastPInvokeError());
        }
        finally
        {
            Native.SelectObject(memory, previous); Native.DeleteObject(bitmap);
            Native.DeleteDC(memory); Native.ReleaseDC(0, screen);
        }
    }
    private static Native.Placement Placement(nint hwnd)
    {
        var placement = new Native.Placement { Length = (uint)Marshal.SizeOf<Native.Placement>() };
        if (!Native.GetWindowPlacement(hwnd, ref placement)) throw new InvalidOperationException("Cannot read placement");
        return placement;
    }
    private static bool SamePlacement(Native.Placement a, Native.Placement b) => a.ShowCommand == b.ShowCommand
        && a.NormalPosition.Equals(b.NormalPosition);
    private static string[] AwaitTaskbar(int expectedCount)
    {
        string[] names = [];
        var clock = Stopwatch.StartNew();
        do
        {
            var hwnd = Native.FindWindow("Shell_TrayWnd", null);
            var root = AutomationElement.FromHandle(hwnd);
            var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            names = elements.Cast<AutomationElement>().Select(x =>
                { try { return x.Current.Name + " [id=" + x.Current.AutomationId + "]"; } catch { return ""; } })
                .Where(x => x.StartsWith("WindowPilot.NativeTests", StringComparison.Ordinal)).ToArray();
            if (names.Length == expectedCount) return names;
            Thread.Sleep(200);
        } while (clock.ElapsedMilliseconds < 5000);
        return names;
    }
    private static (Process Process, nint Hwnd) StartTarget(string title)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("--target"); start.ArgumentList.Add(title);
        var process = Process.Start(start)!;
        var read = process.StandardOutput.ReadLineAsync();
        if (!read.Wait(TimeSpan.FromSeconds(10)) || !long.TryParse(read.Result, out var handle))
            throw new InvalidOperationException("Disposable target did not start.");
        return (process, (nint)handle);
    }
    private static int TargetWindow(string title)
    {
        Native.SetCurrentProcessExplicitAppUserModelID("WindowPilot.NativeTests." + title);
        var klass = new Native.WindowClass { Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
            Instance = Native.GetModuleHandle(null), Procedure = Marshal.GetFunctionPointerForDelegate(WindowProc),
            ClassName = "WindowPilot.Disposable.Native.Test", Background = (nint)6 };
        if (Native.RegisterClassEx(ref klass) == 0) return 2;
        var hwnd = Native.CreateWindowEx(0, klass.ClassName, title, 0x00CF0000, 180, 180, 480, 240, 0, 0, klass.Instance, 0);
        if (hwnd == 0) return 3;
        Native.ShowWindowAsync(hwnd, 4); Native.UpdateWindow(hwnd);
        Native.SetTimer(hwnd, 1, 120000, 0);
        Console.WriteLine(hwnd.ToInt64()); Console.Out.Flush();
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
        GC.KeepAlive(WindowProc);
        return 0;
    }
    private static nint Procedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x8040) { Thread.Sleep(4000); return 0; }
        if (message == 0x113) { Native.DestroyWindow(hwnd); return 0; }
        if (message == 2) { Native.PostQuitMessage(0); return 0; }
        return Native.DefWindowProc(hwnd, message, wParam, lParam);
    }
    private static void WriteReport(string path, bool success, string? error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { success, utc = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.VersionString, sdkTarget = "net11.0-windows", assertions = Passed, skipped = Skipped,
            taskbar = Taskbar, error }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
        { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
            public string? MenuName; public string ClassName; public nint SmallIcon; }
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
        { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage;
            public int XPels, YPels; public uint ColorsUsed, ColorsImportant; public uint Color; }
        [StructLayout(LayoutKind.Sequential)] internal struct Blend { public byte Operation, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] internal struct Placement
        { public uint Length, Flags, ShowCommand; public Point MinPosition, MaxPosition; public Rect NormalPosition; }
        [StructLayout(LayoutKind.Sequential)] internal struct Message
        { public nint Hwnd; public uint Value; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowEx(uint ex, string klass, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
        [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint hwnd, int command);
        [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern nuint SetTimer(nint hwnd, nuint id, uint timeout, nint callback);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
        [DllImport("user32.dll")] internal static extern bool GetLayeredWindowAttributes(nint hwnd, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint color, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, nint destination, ref Point size, nint sourceDc, ref Point source, uint color, ref Blend blend, uint flags);
        [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint item);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool IsZoomed(nint hwnd);
        [DllImport("user32.dll")] internal static extern bool GetWindowPlacement(nint hwnd, ref Placement placement);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? klass, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint RemoveProp(nint hwnd, string name);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    }
}
