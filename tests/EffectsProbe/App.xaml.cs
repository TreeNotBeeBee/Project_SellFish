using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Windows.UI;
using WindowPilot;

namespace EffectsTest;
public partial class App : Application
{
    private static readonly Dictionary<nint, nint> Brushes = new();
    private static readonly EffectNative.WndProc Proc = HandleMessage;
    private nint _target;
    private nint _backdrop, _occluder;
    private readonly List<nint> _wells = new();
    public App() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var result = new Dictionary<string, object>();
        string resultPath = Environment.GetEnvironmentVariable("WINDOWPILOT_EFFECTS_RESULT")
            ?? Path.Combine(AppContext.BaseDirectory, "effects-probe.json");
        // WGC or the display driver may stall before finally can write the full results.
        void Checkpoint(string phase) =>
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { phase, success = false }));
        EffectSession? effects = null;
        nint brush = 0;
        try
        {
            Checkpoint("create test windows");
            brush = CreateSolidBrush(0x000000FF);
            var wc = new EffectNative.WndClass { Size = (uint)Marshal.SizeOf<EffectNative.WndClass>(), ClassName = "WindowPilot.EffectsProbe", Proc = Proc, Instance = EffectNative.GetModuleHandle(null), Background = brush };
            EffectNative.RegisterClassEx(ref wc);
            _backdrop = EffectNative.CreateWindowEx(0, wc.ClassName, "WindowPilot test backdrop", 0x90000000, 100, 80, 720, 500, 0, 0, wc.Instance, 0);
            SetBackground(_backdrop, 0);
            EffectNative.ShowWindow(_backdrop, 5); EffectNative.ShowWindow(_backdrop, 5);
            string[] wellNames = ["black", "white", "red", "green", "blue"];
            uint[] wellColors = [0, 0x00FFFFFF, 0x000000FF, 0x0000FF00, 0x00FF0000];
            for (int i = 0; i < wellColors.Length; i++)
            {
                nint well = EffectNative.CreateWindowEx(0, wc.ClassName, "WindowPilot calibration " + wellNames[i], 0x90000000, 700, 100 + i * 80, 80, 60, 0, 0, wc.Instance, 0);
                _wells.Add(well); SetBackground(well, wellColors[i]); EffectNative.ShowWindow(well, 5); EffectNative.ShowWindow(well, 5);
            }
            _target = EffectNative.CreateWindowEx(0, wc.ClassName, "WindowPilot disposable color probe", 0x10CF0000, 160, 140, 480, 320, 0, 0, wc.Instance, 0);
            if (_target == 0) throw new Exception("CreateWindow failed " + Marshal.GetLastWin32Error());
            SetBackground(_target, 0x000000FF);
            EffectNative.ShowWindow(_target, 5); EffectNative.ShowWindow(_target, 5);
            EffectNative.SetWindowPos(_target, 0, 160, 140, 480, 320, 0x40);
            await Task.Delay(700);
            result["window"] = new { Handle = _target.ToInt64(), Alive = EffectNative.IsWindow(_target), Visible = EffectNative.IsWindowVisible(_target), CaptureSupported = Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported() };
            var calibration = Enumerable.Range(0, 5).ToDictionary(i => wellNames[i], i => ScreenPixel(740, 130 + i * 80));
            result["displayCalibration"] = calibration;
            bool displayInverted = calibration["black"] == "FFFFFF" && calibration["white"] == "000000" && calibration["red"] == "00FFFF" && calibration["green"] == "FF00FF" && calibration["blue"] == "FFFF00";
            result["desktopInversionDetected"] = displayInverted;
            string Calibrate(string rgb) => displayInverted ? (0xFFFFFF ^ Convert.ToUInt32(rgb, 16)).ToString("X6") : rgb;
            Checkpoint("capture source at native alpha levels");
            byte[] baselinePixels = await CaptureSampleAsync(_target);
            result["baseline"] = Histogram(baselinePixels);
            SetWindowLongPtr(_target, -20, GetWindowLongPtr(_target, -20) | 0x80000);
            SetLayeredWindowAttributes(_target, 0, 3, 2);
            await Task.Delay(500);
            byte[] alpha3Pixels = await CaptureSampleAsync(_target);
            result["nativeAlpha3"] = Histogram(alpha3Pixels);
            SetLayeredWindowAttributes(_target, 0, 1, 2);
            await Task.Delay(500);
            byte[] alpha1Pixels = await CaptureSampleAsync(_target);
            result["nativeAlpha1"] = Histogram(alpha1Pixels);
            SetLayeredWindowAttributes(_target, 0, 0, 2);
            await Task.Delay(500);
            try { result["nativeAlpha0"] = Histogram(await CaptureSampleAsync(_target, TimeSpan.FromSeconds(2))); } catch (Exception ex) { result["nativeAlpha0Error"] = ex.Message; }
            result["lowAlphaSourceCorrect"] = Dominant(baselinePixels) == 0xFFFF0000 && Dominant(alpha3Pixels) == 0xFFFF0000 && Dominant(alpha1Pixels) == 0xFFFF0000;
            SetLayeredWindowAttributes(_target, 0, 1, 2);
            await Task.Delay(100);
            result["beforeOverlayScreen"] = ScreenPixel(400, 320);
            GetLayeredWindowAttributes(_target, out uint sourceKey, out byte sourceAlpha, out uint sourceFlags);
            result["sourceAlpha"] = sourceAlpha;
            Checkpoint("present inversion and grayscale");
            effects = new EffectSession();
            effects.Failed += error => result["rendererFailure"] = error;
            effects.Update(_target, true, false, 100, false, false);
            await Task.Delay(1600);
            result["invertFrames"] = effects.RenderedFrames;
            result["invertPresented"] = effects.HasPresentedFrame;
            result["invertRenderedPixels"] = Histogram(effects.ReadRenderedPixelsForDiagnostics()!);
            result["invertScreenCenter"] = ScreenPixel(400, 320);
            result["invertScreenCalibrated"] = Calibrate((string)result["invertScreenCenter"]);
            result["invertCorrect"] = Dominant(effects.ReadRenderedPixelsForDiagnostics()!) == 0xFF00FFFF && (string)result["invertScreenCalibrated"] == "00FFFF";
            result["sourceWhileInverting"] = Histogram(await CaptureSampleAsync(_target));
            result["inputTarget"] = WindowFromPoint(new EffectNative.Point(400, 320)).ToInt64();
            result["inputPassThroughCorrect"] = WindowFromPoint(new EffectNative.Point(400, 320)) == _target;
            effects.Update(_target, true, true, 50, false, false);
            await Task.Delay(700);
            result["combinedFrames"] = effects.RenderedFrames;
            result["combinedRenderedPixels"] = Histogram(effects.ReadRenderedPixelsForDiagnostics()!);
            result["combinedScreenCenter"] = ScreenPixel(400, 320);
            result["combinedScreenCalibrated"] = Calibrate((string)result["combinedScreenCenter"]);
            uint combinedPixel = Dominant(effects.ReadRenderedPixelsForDiagnostics()!);
            uint combinedScreen = Convert.ToUInt32((string)result["combinedScreenCalibrated"], 16);
            result["combinedCorrect"] = (combinedPixel >> 24) == 128 && ((combinedPixel >> 16) & 255) == ((combinedPixel >> 8) & 255) && ((combinedPixel >> 8) & 255) == (combinedPixel & 255) && Math.Abs((int)((combinedScreen >> 16) & 255) - (int)((combinedPixel >> 16) & 255)) <= 2 && Math.Abs((int)((combinedScreen >> 8) & 255) - (int)((combinedPixel >> 8) & 255)) <= 2 && Math.Abs((int)(combinedScreen & 255) - (int)(combinedPixel & 255)) <= 2;
            // Simulate the client owning its alpha again while the color filter remains
            // enabled. The backdrop must mask the red original under the 50% gray overlay.
            Checkpoint("verify backdrop and occlusion");
            using var stableBackdrop = new BackdropOpacityLayer(_target);
            SetLayeredWindowAttributes(_target, 0, 255, 2);
            for (int frame = 0; frame < 15; frame++)
            {
                SetLayeredWindowAttributes(_target, 0, 255, 2);
                stableBackdrop.Track(1, false);
                await Task.Delay(33);
            }
            string stableColor = Calibrate(ScreenPixel(400, 320));
            result["stableFilteredPixel"] = stableColor;
            uint stableRgb = Convert.ToUInt32(stableColor, 16);
            result["stableFilteredCorrect"] = Math.Abs((int)(stableRgb >> 16) - (int)(combinedScreen >> 16)) <= 2
                && Math.Abs((int)((stableRgb >> 8) & 255) - (int)((combinedScreen >> 8) & 255)) <= 2
                && Math.Abs((int)(stableRgb & 255) - (int)(combinedScreen & 255)) <= 2;
            result["stableFilteredInputCorrect"] = WindowFromPoint(new EffectNative.Point(400, 320)) == _target;
            _occluder = EffectNative.CreateWindowEx(0, wc.ClassName, "WindowPilot occlusion probe", 0x90000000, 350, 270, 160, 130, 0, 0, wc.Instance, 0);
            SetBackground(_occluder, 0x00FF0000);
            EffectNative.ShowWindow(_occluder, 5); EffectNative.ShowWindow(_occluder, 5);
            EffectNative.SetWindowPos(_occluder, 0, 350, 270, 160, 130, 0x40);
            await Task.Delay(700);
            stableBackdrop.Track(1, false);
            await Task.Delay(120);
            result["occludedScreenCenter"] = ScreenPixel(400, 320);
            result["occludedScreenCalibrated"] = Calibrate((string)result["occludedScreenCenter"]);
            result["sourceWhileOccluded"] = Histogram(await CaptureSampleAsync(_target));
            result["zOrder"] = new { Target = _target.ToInt64(), Overlay = effects.OverlayHandle.ToInt64(), Occluder = _occluder.ToInt64(), BeforeTarget = EffectNative.GetWindow(_target, 3).ToInt64(), BeforeOverlay = EffectNative.GetWindow(effects.OverlayHandle, 3).ToInt64() };
            result["occlusionCorrect"] = Calibrate(ScreenPixel(400, 320)) == "0000FF";
            EffectNative.SetWindowPos(_target, -1, 0, 0, 0, 0, 0x13);
            stableBackdrop.Track(1, true);
            effects.Update(_target, true, true, 50, false, true);
            EffectNative.SetWindowPos(_occluder, -1, 350, 270, 160, 130, 0x50);
            await Task.Delay(300);
            result["topmostOcclusionCorrect"] = Calibrate(ScreenPixel(400, 320)) == "0000FF" && EffectNative.GetWindow(effects.OverlayHandle, 3) == _occluder;
            EffectNative.SetWindowPos(_target, -2, 0, 0, 0, 0, 0x13);
            effects.Update(_target, true, true, 50, false, false);
            EffectNative.DestroyWindow(_occluder); _occluder = 0;
            stableBackdrop.Track(1, false);
            await Task.Delay(180);
            result["stableRestoredAfterOcclusion"] = Calibrate(ScreenPixel(400,320)) == stableColor;
            stableBackdrop.Hide();
            SetLayeredWindowAttributes(_target, 0, 1, 2);
            Checkpoint("resize, minimize and hide");
            int oldPixelLength = effects.ReadRenderedPixelsForDiagnostics()!.Length;
            EffectNative.SetWindowPos(_target, 0, 160, 140, 560, 360, 0x14);
            await Task.Delay(650);
            result["resizeCorrect"] = effects.ReadRenderedPixelsForDiagnostics()!.Length > oldPixelLength && effects.IsRunning;
            long framesBeforeStaticChange = effects.RenderedFrames;
            effects.Update(_target, false, true, 50, false, false);
            result["staticRepaintImmediate"] = effects.RenderedFrames > framesBeforeStaticChange;
            effects.Update(_target, true, true, 50, false, false);
            EffectNative.ShowWindow(_target, 6);
            await Task.Delay(200);
            result["minimizedOverlay"] = !EffectNative.IsWindowVisible(effects.OverlayHandle);
            EffectNative.ShowWindow(_target, 9);
            await Task.Delay(350);
            result["restoredOverlay"] = EffectNative.IsWindowVisible(effects.OverlayHandle);
            effects.Update(_target, true, true, 50, true, false);
            await Task.Delay(150);
            result["hiddenOverlay"] = !EffectNative.IsWindowVisible(effects.OverlayHandle);
            effects.Update(_target, true, true, 50, false, false);
            await Task.Delay(150);
            result["reshownOverlay"] = EffectNative.IsWindowVisible(effects.OverlayHandle);
            effects.Update(_target, true, true, 0, false, false);
            await Task.Delay(200);
            result["zeroOverlay"] = !EffectNative.IsWindowVisible(effects.OverlayHandle);
            Checkpoint("check tint strengths, combinations and source alpha");
            await CheckTintAsync(effects, result, Calibrate, combinedPixel);
            Checkpoint("key out part of the target over a changing background");
            await CheckColorKeyAsync(effects, result, Calibrate);
            effects.Stop();
            string[] checks = [
                "lowAlphaSourceCorrect", "invertCorrect", "combinedCorrect", "stableFilteredCorrect",
                "stableFilteredInputCorrect", "stableRestoredAfterOcclusion", "occlusionCorrect",
                "topmostOcclusionCorrect", "inputPassThroughCorrect", "resizeCorrect",
                "staticRepaintImmediate", "minimizedOverlay", "restoredOverlay", "hiddenOverlay",
                "reshownOverlay", "zeroOverlay", "tintQuarter", "tintFull", "tintColorOnlyChange",
                "tintStacked", "tintZeroWithEffects", "tintHitTarget", "tintHidden", "tintReshown", "tintZeroAlone",
                "tintRestoredOriginal", "tintAlphaMask", "keyExact", "keyLiveBackdrop", "keyPartial",
                "keyRange", "keyStacked", "keyDisabled", "keySourceAlpha", "keyCustomRgb", "keyNeutralRgb"
            ];
            result["success"] = !result.ContainsKey("rendererFailure") && Convert.ToInt64(result["invertFrames"]) > 0
                && checks.All(check => (bool)result[check]);
        }
        catch (Exception ex) { result["exception"] = ex.ToString(); result["success"] = false; }
        finally
        {
            effects?.Dispose();
            if (_target != 0) EffectNative.DestroyWindow(_target);
            if (_occluder != 0) EffectNative.DestroyWindow(_occluder);
            if (_backdrop != 0) EffectNative.DestroyWindow(_backdrop);
            foreach (nint well in _wells) EffectNative.DestroyWindow(well);
            if (brush != 0) EffectNative.DeleteObject(brush);
            foreach (var ownedBrush in Brushes.Values) EffectNative.DeleteObject(ownedBrush);
            Brushes.Clear();
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Exit();
        }
    }

    private static Task<byte[]> CaptureSampleAsync(nint hwnd, TimeSpan? timeout = null) => Task.Run(async () =>
    {
        // Keep the target's message loop free while its capture session opens and closes.
        // Only one frame is needed. Poll the free-threaded pool without a competing FrameArrived reader.
        using var device = new CanvasDevice();
        var item = CaptureInterop.ForWindow(hwnd);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.As<IDirect3DDevice>(),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (elapsed.Elapsed < limit)
        {
            using var frame = pool.TryGetNextFrame();
            if (frame != null)
            {
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface, 96);
                return bitmap.GetPixelBytes();
            }
            await Task.Delay(16).ConfigureAwait(false);
        }
        throw new TimeoutException("No image arrived from the disposable window.");
    });

    private async Task CheckTintAsync(EffectSession effects, Dictionary<string, object> result,
        Func<string, string> calibrate, uint untintedGrayAtHalfOpacity)
    {
        var blue = Color.FromArgb(255, 32, 176, 224);
        var black = Color.FromArgb(255, 0, 0, 0);
        async Task Sample(string name, Color color, int strength, uint expected,
            bool invertAndGray = false, int opacity = 100)
        {
            long frames = effects.RenderedFrames;
            effects.Update(_target, invertAndGray, invertAndGray, opacity, false, false, color, strength);
            // The input is stationary. A parameter edit must redraw the stored frame immediately.
            bool repainted = effects.RenderedFrames > frames;
            uint gpu = Dominant(effects.ReadRenderedPixelsForDiagnostics()!);
            uint expectedScreen = expected | 0xFF000000;
            uint desktop = 0;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(32);
                desktop = 0xFF000000 | Convert.ToUInt32(calibrate(ScreenPixel(400, 320)), 16);
                if (MatchesPixel(desktop, expectedScreen, 2)) break;
            }
            result[name + "Pixels"] = new { Expected = expected.ToString("X8"), GPU = gpu.ToString("X8"), Desktop = desktop.ToString("X8"), Repainted = repainted };
            result[name] = repainted && MatchesPixel(gpu, expected, 1) && MatchesPixel(desktop, expectedScreen, 2);
        }

        await Sample("tintQuarter", blue, 25, 0xFFC72C38); // red * .75 + chosen RGB * .25
        result["tintHitTarget"] = WindowFromPoint(new EffectNative.Point(400, 320)) == _target;
        await Sample("tintFull", blue, 100, 0xFF20B0E0);
        await Sample("tintColorOnlyChange", black, 100, 0xFF000000);
        // Compare with the actual invert+grayscale baseline at the same opacity.
        // Both terms are premultiplied before comparison with Win2D's BGRA output.
        uint gray = untintedGrayAtHalfOpacity & 255;
        uint Midpoint(uint tintedChannel) => (gray + tintedChannel + 1) / 2;
        uint mixed = 0x80000000 | (Midpoint(16) << 16) | (Midpoint(88) << 8) | Midpoint(112);
        await Sample("tintStacked", blue, 50, mixed, invertAndGray: true, opacity: 50);
        await Sample("tintZeroWithEffects", blue, 0, untintedGrayAtHalfOpacity, invertAndGray: true, opacity: 50);
        effects.Update(_target, false, false, 100, true, false, blue, 100);
        result["tintHidden"] = !EffectNative.IsWindowVisible(effects.OverlayHandle);
        effects.Update(_target, false, false, 100, false, false, blue, 100);
        await Task.Delay(250);
        result["tintReshown"] = calibrate(ScreenPixel(400, 320)) == "20B0E0";
        effects.Update(_target, false, false, 100, false, false, blue, 0);
        result["tintZeroAlone"] = !effects.IsRunning && effects.OverlayHandle == 0;
        SetLayeredWindowAttributes(_target, 0, 255, 2);
        await Task.Delay(250);
        result["tintRestoredOriginal"] = calibrate(ScreenPixel(400, 320)) == "FF0000";

        // A real window's shape varies with corner settings. This two-pixel source makes
        // both a fully transparent pixel and a half-opaque red pixel deterministic.
        using var device = new CanvasDevice();
        byte[] sourcePixels = [0, 0, 0, 0, 0, 0, 128, 128];
        using var source = CanvasBitmap.CreateFromBytes(device, sourcePixels, 2, 1,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        using var output = new CanvasRenderTarget(device, 2, 1, 96);
        using var tint = EffectSession.CreateTintEffect(source, blue, 50);
        using (var drawing = output.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.DrawImage(tint);
        }
        byte[] actual = output.GetPixelBytes();
        uint translucent = BitConverter.ToUInt32(actual, 4);
        result["tintAlphaPixel"] = translucent.ToString("X8");
        result["tintAlphaMask"] = BitConverter.ToUInt32(actual, 0) == 0 && MatchesPixel(translucent, 0x80482C38, 1);

        using var keepRed = EffectSession.CreateColorKeyEffect(source, blue, 0);
        using (var drawing = output.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.DrawImage(keepRed);
        }
        actual = output.GetPixelBytes();
        result["keySourcePixels"] = Convert.ToHexString(actual);
        result["keySourceAlpha"] = actual.SequenceEqual(sourcePixels);

        // Distinguish luminosity as well as hue. An exact white key must keep black and gray.
        uint[] swatches = [0xFF3486DE, 0xFF3486DD, 0xFF000000, 0xFF808080, 0xFFFFFFFF];
        using var palette = CanvasBitmap.CreateFromBytes(device, swatches.SelectMany(BitConverter.GetBytes).ToArray(),
            swatches.Length, 1, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var paletteOutput = new CanvasRenderTarget(device, swatches.Length, 1, 96);
        void CheckSwatch(string name, Color keyColor, int keyedIndex)
        {
            using var keyed = EffectSession.CreateColorKeyEffect(palette, keyColor, 0);
            using (var drawing = paletteOutput.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.DrawImage(keyed);
            }
            byte[] rgba = paletteOutput.GetPixelBytes();
            result[name + "Pixels"] = Convert.ToHexString(rgba);
            result[name] = swatches.Select((c, i) => BitConverter.ToUInt32(rgba, i * 4) == (i == keyedIndex ? 0 : c)).All(x => x);
        }
        CheckSwatch("keyCustomRgb", Color.FromArgb(255, 52, 134, 222), 0);
        CheckSwatch("keyNeutralRgb", Color.FromArgb(255, 255, 255, 255), 4);
    }

    private async Task CheckColorKeyAsync(EffectSession effects, Dictionary<string, object> result, Func<string, string> calibrate)
    {
        // Two child swatches belong to the same captured HWND, alongside the red client background.
        void AddSwatch(int x, uint colorRef)
        {
            nint child = EffectNative.CreateWindowEx(0, "WindowPilot.EffectsProbe", "", 0x50000000,
                x, 70, 70, 70, _target, 0, EffectNative.GetModuleHandle(null), 0);
            if (child == 0) throw new InvalidOperationException("Cannot create test swatch.");
            SetBackground(child, colorRef);
        }
        AddSwatch(30, 0x00FF0000);
        AddSwatch(130, 0x00FE0000); // Off by just one blue level.
        var blue = Color.FromArgb(255, 0, 0, 255);
        var yellow = Color.FromArgb(255, 255, 255, 0);
        var samples = new[] { new EffectNative.Point(65, 100), new EffectNative.Point(165, 100), new EffectNative.Point(265, 100) };
        for (int i = 0; i < samples.Length; i++) ClientToScreen(_target, ref samples[i]);
        async Task Check(string name, params uint[] expected)
        {
            uint[] actual = [];
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(32);
                actual = samples.Select(p => 0xFF000000 | Convert.ToUInt32(calibrate(ScreenPixel(p.X, p.Y)), 16)).ToArray();
                if (actual.Zip(expected, (a, e) => MatchesPixel(a, e, 2)).All(v => v)) break;
            }
            result[name + "Pixels"] = actual.Select(x => x.ToString("X8")).ToArray();
            result[name] = actual.Zip(expected, (a, e) => MatchesPixel(a, e, 2)).All(v => v);
        }

        SetLayeredWindowAttributes(_target, 0, 1, 2);
        SetBackground(_backdrop, 0x0000FF00);
        effects.Update(_target, false, false, 100, false, false, keyColor: blue, keyStrength: 100);
        await Check("keyExact", 0xFF00FF00, 0xFF0000FE, 0xFFFF0000);
        SetBackground(_backdrop, 0x00FFFFFF);
        await Check("keyLiveBackdrop", 0xFFFFFFFF, 0xFF0000FE, 0xFFFF0000);
        effects.Update(_target, false, false, 100, false, false, keyColor: blue, keyStrength: 50);
        await Check("keyPartial", 0xFF8080FF, 0xFF0000FE, 0xFFFF0000);
        effects.Update(_target, false, false, 100, false, false, keyColor: blue, keyRange: 8, keyStrength: 100);
        await Check("keyRange", 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFF0000);
        effects.Update(_target, true, true, 50, false, false, yellow, 100, blue, 0, 100);
        await Check("keyStacked", 0xFFFFFFFF, 0xFFFFFF80, 0xFFFFFF80);
        effects.Update(_target, false, false, 100, false, false, keyColor: blue, keyStrength: 0);
        SetLayeredWindowAttributes(_target, 0, 255, 2);
        await Check("keyDisabled", 0xFF0000FF, 0xFF0000FE, 0xFFFF0000);
    }

    private static bool MatchesPixel(uint actual, uint expected, int tolerance)
    {
        if ((actual >> 24) != (expected >> 24)) return false;
        for (int shift = 0; shift <= 16; shift += 8)
            if (Math.Abs((int)((actual >> shift) & 255) - (int)((expected >> shift) & 255)) > tolerance) return false;
        return true;
    }

    private static string ScreenPixel(int x, int y)
    {
        nint dc = GetDC(0);
        nint memory = EffectNative.CreateCompatibleDC(dc), bitmap = CreateCompatibleBitmap(dc, 1, 1);
        nint old = EffectNative.SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, 1, 1, dc, x, y, 0x40CC0020);
            uint c = GetPixel(memory, 0, 0); return $"{c & 255:X2}{(c >> 8) & 255:X2}{(c >> 16) & 255:X2}";
        }
        finally { EffectNative.SelectObject(memory, old); EffectNative.DeleteObject(bitmap); EffectNative.DeleteDC(memory); ReleaseDC(0, dc); }
    }
    private static void SetBackground(nint hwnd, uint color)
    {
        if (Brushes.TryGetValue(hwnd, out nint oldBrush)) EffectNative.DeleteObject(oldBrush);
        Brushes[hwnd] = CreateSolidBrush(color);
        InvalidateRect(hwnd, 0, true);
    }
    private static nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x14 && Brushes.TryGetValue(hwnd, out nint brush))
        {
            GetClientRect(hwnd, out EffectNative.Rect rect);
            FillRect(wParam, ref rect, brush);
            return 1;
        }
        return EffectNative.DefWindowProc(hwnd, message, wParam, lParam);
    }
    private static object Histogram(byte[] pixels)
    {
        var colors = new Dictionary<uint, int>();
        for (int i = 0; i < pixels.Length; i += 4) { uint color = BitConverter.ToUInt32(pixels, i); colors[color] = colors.GetValueOrDefault(color) + 1; }
        return colors.OrderByDescending(x => x.Value).Take(6).Select(x => new { BGRA = x.Key.ToString("X8"), Count = x.Value }).ToArray();
    }
    private static uint Dominant(byte[] pixels)
    {
        var colors = new Dictionary<uint, int>();
        for (int i = 0; i < pixels.Length; i += 4) { uint color = BitConverter.ToUInt32(pixels, i); colors[color] = colors.GetValueOrDefault(color) + 1; }
        return colors.MaxBy(x => x.Value).Key;
    }
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(EffectNative.Point point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out EffectNative.Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref EffectNative.Point point);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref EffectNative.Rect rect, nint brush);
}
