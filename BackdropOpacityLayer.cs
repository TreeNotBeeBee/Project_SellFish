using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPilot;

/// <summary>
/// Shows a 1:1 live view of the desktop behind one window. When the target is opaque,
/// drawing this view above it with alpha (1 - p) gives the target visual opacity p
/// without changing how the target draws or routes mouse and keyboard input.
/// </summary>
internal sealed class BackdropOpacityLayer : IDisposable
{
    private readonly nint _target;
    private nint _host, _magnifier;
    private bool _initialized, _shown;
    private bool? _topmost;
    private byte? _alpha;
    private EffectNative.Rect _bounds;
    private nint[] _excluded = [];
    // The native color overlay and the backdrop share the window controller's UI thread.
    [ThreadStatic] private static Dictionary<nint, nint>? _backdrops;
    [ThreadStatic] private static Dictionary<nint, nint>? _effects;
    internal static nint AnchorForEffect(nint target) =>
        _backdrops?.TryGetValue(target, out var handle) == true && EffectNative.IsWindowVisible(handle) ? handle : target;
    internal static void RegisterEffect(nint target, nint effect)
    {
        if (effect != 0) (_effects ??= [])[target] = effect;
        else _effects?.Remove(target);
    }

    public BackdropOpacityLayer(nint target)
    {
        _target = target;
        if (!MagInitialize()) throw Error("初始化稳定透明图层");
        _initialized = true;
        try
        {
            const uint tool = 0x80, layered = 0x80000, transparent = 0x20, noActivate = 0x08000000;
            _host = EffectNative.CreateWindowEx(tool | layered | transparent | noActivate, "Static", "", 0x80000000,
                0, 0, 1, 1, 0, 0, EffectNative.GetModuleHandle(null), 0);
            if (_host == 0) throw Error("创建稳定透明图层");
            _magnifier = EffectNative.CreateWindowEx(0, "Magnifier", "", 0x50000000,
                0, 0, 1, 1, _host, 0, EffectNative.GetModuleHandle(null), 0);
            if (_magnifier == 0) throw Error("创建实时背景图层");
            (_backdrops ??= [])[target] = _host;
        }
        catch { Dispose(); throw; }
    }

    public void Track(byte targetWeight, bool topmost)
    {
        if (targetWeight == 255 || !EffectNative.IsWindowVisible(_target) || EffectNative.IsIconic(_target)
            || (DwmGetWindowAttribute(_target, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
        { Hide(); return; }
        if (EffectNative.DwmGetWindowAttribute(_target, 9, out var bounds, Marshal.SizeOf<EffectNative.Rect>()) != 0
            && !EffectNative.GetWindowRect(_target, out bounds)) { Hide(); return; }
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) { Hide(); return; }

        // Exclude foreground windows as well: a translucent dialog or the color-effects
        // overlay must be drawn exactly once, above this layer, by the desktop compositor.
        List<nint> excluded = [_target, _host];
        if (_effects?.TryGetValue(_target, out var effect) == true) excluded.Add(effect);
        EnumWindows((window, _) =>
        {
            if (window == _target) return false;
            if (window != _host && EffectNative.IsWindowVisible(window) && !excluded.Contains(window)
                && EffectNative.GetWindowRect(window, out var other) && other.Left < bounds.Right
                && other.Right > bounds.Left && other.Top < bounds.Bottom && other.Bottom > bounds.Top)
                excluded.Add(window);
            return true;
        }, 0);
        if (!_excluded.SequenceEqual(excluded))
        {
            nint[] handles = excluded.ToArray();
            if (!MagSetWindowFilterList(_magnifier, 0, handles.Length, handles)) throw Error("排除目标窗口");
            _excluded = handles;
        }

        // A filtered foreground will supply all the source pixels. Under it the backdrop
        // must cover the unfiltered original completely (the UI normally requests alpha 1).
        byte alpha = targetWeight == 1 && _effects?.ContainsKey(_target) == true
            ? (byte)255 : (byte)(255 - targetWeight);
        if (_alpha != alpha)
        {
            if (!SetLayeredWindowAttributes(_host, 0, alpha, 2)) throw Error("设置稳定透明度");
            _alpha = alpha;
        }

        bool moved = !_shown || !bounds.Equals(_bounds);
        if (moved && !EffectNative.SetWindowPos(_magnifier, 0, 0, 0, width, height, 0x14))
            throw Error("调整实时背景大小");
        // Refresh even at fixed coordinates so an independently animating background stays live.
        if (!MagSetWindowSource(_magnifier, bounds)) throw Error("刷新实时背景");
        const uint noFocus = 0x10 | 0x200;
        if (_topmost != topmost)
        {
            if (!EffectNative.SetWindowPos(_host, topmost ? -1 : -2, 0, 0, 0, 0, noFocus | 3))
                throw Error("同步稳定透明图层置顶状态");
            _topmost = topmost;
        }
        nint previous = EffectNative.GetWindow(_target, 3);
        if (moved || previous != _host)
        {
            uint flags = noFocus | 0x40;
            if (previous == _host) flags |= 4;
            if (!EffectNative.SetWindowPos(_host, previous == _host ? 0 : previous, bounds.Left, bounds.Top,
                width, height, flags)) throw Error("对齐稳定透明图层");
        }
        _bounds = bounds; _shown = true;
    }

    public void Hide()
    {
        if (_shown) EffectNative.ShowWindow(_host, 0);
        _shown = false;
    }
    public void Dispose()
    {
        if (_host != 0)
        {
            _backdrops?.Remove(_target);
            EffectNative.DestroyWindow(_host);
            _host = _magnifier = 0;
        }
        if (_initialized) { MagUninitialize(); _initialized = false; }
    }
    private static InvalidOperationException Error(string operation) =>
        new($"{operation}失败。{new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    private delegate bool EnumWindowProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("magnification.dll", SetLastError = true)] private static extern bool MagInitialize();
    [DllImport("magnification.dll")] private static extern bool MagUninitialize();
    [DllImport("magnification.dll", SetLastError = true)] private static extern bool MagSetWindowFilterList(nint control, uint mode, int count, [In] nint[] windows);
    [DllImport("magnification.dll", SetLastError = true)] private static extern bool MagSetWindowSource(nint control, EffectNative.Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
