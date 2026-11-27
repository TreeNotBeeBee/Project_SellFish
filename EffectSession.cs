using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI;
using WinRT;

namespace WindowPilot;

/// <summary>
/// Captures exactly one HWND and applies color transforms to its content. The native
/// target remains the keyboard/mouse recipient; the presentation window is click-through.
/// The binding service owns the target's original styles and temporary native opacity.
/// </summary>
public sealed class EffectSession : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _tracking;
    private CanvasDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private CanvasRenderTarget? _renderTarget;
    private CanvasRenderTarget? _sourceCopy;
    private NativeEffectOverlay? _overlay;
    private SizeInt32 _size;
    private nint _target;
    private bool _invert, _grayscale, _hidden, _topmost, _disposed, _failed;
    private int _opacity = 100;
    private Color _tintColor;
    private int _tintStrength;
    private int _pending;
    private int _generation;
    private long _lastFrame;
    private bool _hasFrame;
    private readonly Stopwatch _startup = new();

    /// <summary>Always raised on the constructing UI thread. Owner should restore native opacity.</summary>
    public event Action<string>? Failed;
    public bool IsRunning => _session is not null && !_failed;
    public bool HasPresentedFrame => _hasFrame;
    public string? LastError { get; private set; }
    public long RenderedFrames { get; private set; }
    internal nint OverlayHandle => _overlay?.Handle ?? 0;
    internal byte[]? ReadRenderedPixelsForDiagnostics() => _renderTarget?.GetPixelBytes();

    public EffectSession()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("颜色效果必须在界面线程创建。");
        _tracking = _dispatcher.CreateTimer();
        _tracking.Interval = TimeSpan.FromMilliseconds(33);
        _tracking.Tick += (_, _) => { try { TrackTarget(); } catch (Exception ex) { Fail("颜色效果停止：" + ex.Message); } };
    }

    public void Update(nint hwnd, bool invert, bool grayscale, int opacity, bool hidden, bool topmost,
        Color tintColor = default, int tintStrength = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("颜色效果必须在界面线程更新。");
        if (opacity is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(opacity));
        if (tintStrength is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(tintStrength));
        if (hwnd == 0 || (!invert && !grayscale && tintStrength == 0)) { Stop(); return; }
        bool changed = _invert != invert || _grayscale != grayscale || _opacity != opacity ||
            _tintStrength != tintStrength || (tintStrength > 0 && !_tintColor.Equals(tintColor)) ||
            (_hidden && !hidden);
        bool restart = _target != hwnd || _session is null;
        if (restart) Stop();
        _invert = invert;
        _grayscale = grayscale;
        _opacity = opacity;
        _hidden = hidden;
        _topmost = topmost;
        _tintStrength = tintStrength;
        _tintColor = tintColor;
        if (restart) Start(hwnd);
        else if (changed)
        {
            _lastFrame = 0;
            if (_sourceCopy is not null) RenderStoredSource();
        }
        TrackTarget();
    }

    private void Start(nint target)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("此系统或显卡不支持窗口级颜色效果。");
        if (!EffectNative.IsWindow(target)) throw new InvalidOperationException("目标窗口已关闭。");
        _target = target;
        _failed = false; LastError = null; _hasFrame = false; RenderedFrames = 0;
        try
        {
            _device = new CanvasDevice();
            _item = CaptureInterop.ForWindow(target);
            _size = _item.Size;
            if (_size.Width <= 0 || _size.Height <= 0) throw new InvalidOperationException("窗口没有可捕获的画面，请先恢复显示。");
            _overlay = new NativeEffectOverlay(target);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device.As<IDirect3DDevice>(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += FrameArrived;
            _item.Closed += ItemClosed;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            // Capture indication is owned by Windows. Do not silently bypass border consent.
            _startup.Restart();
            _session.StartCapture();
            _tracking.Start();
        }
        catch { Stop(); throw; }
    }

    private void ItemClosed(GraphicsCaptureItem sender, object args)
    {
        int generation = _generation;
        _dispatcher.TryEnqueue(() => { if (generation == _generation && _item is not null) Fail("目标窗口已关闭，颜色效果已停止。"); });
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        int callbackGeneration = _generation;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null) return;
            long now = Stopwatch.GetTimestamp();
            long pixels = (long)_size.Width * _size.Height;
            int frameIntervalMs = pixels >= 16_000_000 ? 160 : pixels >= 7_000_000 ? 80 : pixels >= 3_000_000 ? 50 : 32;
            if ((_lastFrame != 0 && Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds < frameIntervalMs) || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) { frame.Dispose(); return; }
            _lastFrame = now;
            int generation = callbackGeneration;
            var held = frame; frame = null;
            if (!_dispatcher.TryEnqueue(() =>
            {
                try { if (generation == _generation && !_disposed && !_failed) Render(held); }
                catch (Exception ex) { Fail("颜色效果停止：" + ex.Message); }
                finally { held.Dispose(); Interlocked.Exchange(ref _pending, 0); }
            })) { held.Dispose(); Interlocked.Exchange(ref _pending, 0); }
        }
        catch (Exception ex)
        {
            frame?.Dispose();
            _dispatcher.TryEnqueue(() => { if (callbackGeneration == _generation) Fail("无法读取窗口画面：" + ex.Message); });
        }
    }

    private void Render(Direct3D11CaptureFrame frame)
    {
        if (_device is null || _overlay is null || _pool is null) return;
        var content = frame.ContentSize;
        if (content.Width <= 0 || content.Height <= 0) return;
        if (_size.Width != content.Width || _size.Height != content.Height)
        {
            _size = content;
            _renderTarget?.Dispose(); _renderTarget = null;
            _sourceCopy?.Dispose(); _sourceCopy = null;
            int generation = _generation;
            // Recreate only after the current capture frame has been returned to its pool.
            _dispatcher.TryEnqueue(() =>
            {
                if (generation != _generation || _pool is null || _device is null) return;
                try { _pool.Recreate(_device.As<IDirect3DDevice>(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, content); }
                catch (Exception ex) { Fail("窗口尺寸变化后无法更新颜色效果：" + ex.Message); }
            });
            return;
        }
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface, 96);
        _sourceCopy ??= new CanvasRenderTarget(_device, content.Width, content.Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var copy = _sourceCopy.CreateDrawingSession()) { copy.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0)); copy.DrawImage(bitmap); }
        RenderStoredSource();
    }

    private void RenderStoredSource()
    {
        if (_device is null || _overlay is null || _sourceCopy is null) return;
        if (_hidden || _opacity == 0 || !EffectNative.IsWindowVisible(_target) || EffectNative.IsIconic(_target)) { _overlay.Hide(); return; }
        ICanvasImage image = _sourceCopy;
        using var invert = _invert ? new InvertEffect { Source = image } : null;
        if (invert is not null) image = invert;
        using var grayscale = _grayscale ? new GrayscaleEffect { Source = image } : null;
        if (grayscale is not null) image = grayscale;
        using var tint = _tintStrength > 0 ? CreateTintEffect(image, _tintColor, _tintStrength) : null;
        if (tint is not null) image = tint;
        _renderTarget ??= new CanvasRenderTarget(_device, _size.Width, _size.Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var drawing = _renderTarget.CreateDrawingSession())
        {
            drawing.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            drawing.DrawImage(image, Vector2.Zero, new Windows.Foundation.Rect(0, 0, _size.Width, _size.Height), _opacity / 100f);
        }
        var pixels = _renderTarget.GetPixelBytes();
        _overlay.Present(pixels, _size.Width, _size.Height);
        _hasFrame = true; RenderedFrames++;
        TrackTarget();
    }

    internal static ColorMatrixEffect CreateTintEffect(ICanvasImage source, Color color, int strength)
    {
        float mix = strength / 100f;
        float original = 1f - mix;
        // Win2D 在非预乘的 RGB 上插值，再按原 alpha 预乘，保留半透明边缘和透明区域。
        return new ColorMatrixEffect
        {
            Source = source,
            AlphaMode = CanvasAlphaMode.Premultiplied,
            ColorMatrix = new Matrix5x4
            {
                M11 = original, M22 = original, M33 = original, M44 = 1,
                M51 = mix * color.R / 255f, M52 = mix * color.G / 255f, M53 = mix * color.B / 255f
            }
        };
    }

    private void TrackTarget()
    {
        if (_target == 0 || _overlay is null || _failed) return;
        if (!EffectNative.IsWindow(_target)) { Fail("目标窗口已关闭，颜色效果已停止。"); return; }
        if (_hidden || _opacity == 0 || !EffectNative.IsWindowVisible(_target) || EffectNative.IsIconic(_target)) { _overlay.Hide(); return; }
        if (_hasFrame) _overlay.Track(_size.Width, _size.Height, _topmost);
        else if (_startup.Elapsed > TimeSpan.FromSeconds(5)) Fail("该窗口未提供可用画面，可能限制捕获或使用不支持的绘制方式。");
    }

    private void Fail(string message)
    {
        if (_failed || _disposed) return;
        Stop();
        _failed = true; LastError = message;
        Failed?.Invoke(message);
    }

    public void Stop()
    {
        _generation++;
        _tracking.Stop();
        if (_pool is not null) _pool.FrameArrived -= FrameArrived;
        if (_item is not null) _item.Closed -= ItemClosed;
        _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
        _item = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _sourceCopy?.Dispose(); _sourceCopy = null;
        _overlay?.Dispose(); _overlay = null;
        _device?.Dispose(); _device = null;
        _target = 0; _hasFrame = false; _lastFrame = 0;
        _startup.Reset();
    }
    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }

    internal static async Task<byte[]> CaptureSampleAsync(nint hwnd, TimeSpan? timeout = null)
    {
        using var device = new CanvasDevice();
        var item = CaptureInterop.ForWindow(hwnd);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.As<IDirect3DDevice>(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface, 96);
                completion.TrySetResult(bitmap.GetPixelBytes());
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        session.StartCapture();
        return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
    }
}

internal static class CaptureInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint hwnd, in Guid iid, out nint item);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint item);
    }
    public static unsafe GraphicsCaptureItem ForWindow(nint hwnd)
    {
        var factory = WindowsRuntimeMarshalHelper.GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", typeof(IGraphicsCaptureItemInterop).GUID);
        try
        {
            Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            nint item = 0;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            Marshal.ThrowExceptionForHR(create(factory, hwnd, &iid, &item));
            try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(factory); }
    }
    private static class WindowsRuntimeMarshalHelper
    {
        public static nint GetActivationFactory(string name, Guid iid)
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out nint str));
            try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(str, iid, out nint factory)); return factory; }
            finally { WindowsDeleteString(str); }
        }
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out nint str);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint str);
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
    }
}
