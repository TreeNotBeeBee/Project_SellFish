using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowPilot;

/// <summary>Nonactivating per-pixel-alpha presentation window; owns no taskbar entry.</summary>
internal sealed class NativeEffectOverlay : IDisposable
{
    private const uint WsExLayered = 0x80000, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private readonly nint _target;
    private nint _hwnd, _dc, _bitmap, _bits, _oldBitmap;
    private int _width, _height;
    private static readonly EffectNative.WndProc Proc = WndProc;
    private static readonly string ClassName = "WindowPilot.EffectOverlay.v1";
    private static ushort _class;
    private bool _shown;
    private bool? _lastTopmost;
    private int _x = int.MinValue, _y = int.MinValue, _trackedWidth, _trackedHeight;
    internal nint Handle => _hwnd;

    public NativeEffectOverlay(nint target)
    {
        _target = target;
        if (_class == 0)
        {
            var wc = new EffectNative.WndClass { Size = (uint)Marshal.SizeOf<EffectNative.WndClass>(), Proc = Proc, Instance = EffectNative.GetModuleHandle(null), ClassName = ClassName };
            _class = EffectNative.RegisterClassEx(ref wc);
            if (_class == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建窗口颜色效果图层。");
        }
        // Keep this a separate top-level surface. Making it an owned popup can cause
        // older window-capture implementations to include it in the source content.
        _hwnd = EffectNative.CreateWindowEx(WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate, ClassName, "WindowPilot effect", 0x80000000, 0, 0, 1, 1, 0, 0, EffectNative.GetModuleHandle(null), 0);
        if (_hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建窗口颜色效果图层。");
        BackdropOpacityLayer.RegisterEffect(target, _hwnd);
        _dc = EffectNative.CreateCompatibleDC(0);
        if (_dc == 0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x84) return -1; // HTTRANSPARENT; layered style supplies cross-process click-through.
        if (message == 0x21) return 3; // MA_NOACTIVATE
        return EffectNative.DefWindowProc(hwnd, message, wParam, lParam);
    }
    public void Present(byte[] pixels, int width, int height)
    {
        if (_width != width || _height != height)
        {
            ReleaseBitmap();
            var info = new EffectNative.BitmapInfo { Header = new EffectNative.BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0 } };
            _bitmap = EffectNative.CreateDIBSection(_dc, ref info, 0, out _bits, 0, 0);
            if (_bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法分配颜色效果图像。");
            _oldBitmap = EffectNative.SelectObject(_dc, _bitmap);
            _width = width; _height = height;
        }
        Marshal.Copy(pixels, 0, _bits, checked(width * height * 4));
        var size = new EffectNative.Size(width, height);
        var source = new EffectNative.Point(0, 0);
        var blend = new EffectNative.BlendFunction { Operation = 0, Alpha = 255, Format = 1 };
        if (!EffectNative.UpdateLayeredWindow(_hwnd, 0, 0, ref size, _dc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "颜色效果图层更新失败。");
    }
    public void Track(int width, int height, bool topmost)
    {
        if (_hwnd == 0) return;
        if (EffectNative.DwmGetWindowAttribute(_target, 9, out EffectNative.Rect rect, Marshal.SizeOf<EffectNative.Rect>()) != 0 && !EffectNative.GetWindowRect(_target, out rect)) { Hide(); return; }
        // The capture surface uses extended frame bounds, excluding invisible resize borders.
        int x = rect.Left, y = rect.Top;
        nint anchor = BackdropOpacityLayer.AnchorForEffect(_target);
        nint previous = EffectNative.GetWindow(anchor, 3); // GW_HWNDPREV
        while (previous == _hwnd) previous = EffectNative.GetWindow(previous, 3);
        bool changeBand = _lastTopmost != topmost;
        bool needsZOrder = EffectNative.GetWindow(anchor, 3) != _hwnd;
        bool needsMove = _x != x || _y != y || _trackedWidth != width || _trackedHeight != height;
        if (changeBand)
        {
            // Change bands only once, never reorder the owner, then restore the exact
            // predecessor observed before that change (including other topmost apps).
            EffectNative.SetWindowPos(_hwnd, topmost ? -1 : -2, 0, 0, 0, 0, 0x10 | 0x200 | 0x1 | 0x2);
            _lastTopmost = topmost;
            needsZOrder = true;
        }
        if (!_shown || needsMove || needsZOrder)
        {
            uint flags = 0x10 | 0x200 | 0x40;
            if (!needsZOrder) flags |= 0x4; // SWP_NOZORDER
            if (!EffectNative.SetWindowPos(_hwnd, previous == 0 ? 0 : previous, x, y, width, height, flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "颜色效果图层无法跟随窗口。");
            _x = x; _y = y; _trackedWidth = width; _trackedHeight = height;
        }
        _shown = true;
    }
    public void Hide() { if (_shown && _hwnd != 0) { EffectNative.ShowWindow(_hwnd, 0); _shown = false; } }
    private void ReleaseBitmap()
    {
        if (_oldBitmap != 0 && _dc != 0) { EffectNative.SelectObject(_dc, _oldBitmap); _oldBitmap = 0; }
        if (_bitmap != 0) { EffectNative.DeleteObject(_bitmap); _bitmap = 0; _bits = 0; }
    }
    public void Dispose()
    {
        if (_hwnd != 0) { BackdropOpacityLayer.RegisterEffect(_target, 0); EffectNative.DestroyWindow(_hwnd); _hwnd = 0; }
        ReleaseBitmap();
        if (_dc != 0) { EffectNative.DeleteDC(_dc); _dc = 0; }
    }
}

internal static class EffectNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Size(int width, int height) { public int Width = width, Height = height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct BlendFunction { public byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    internal delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WndClass { public uint Size, Style; public WndProc Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, nint destinationPoint, ref Size size, nint sourceDc, ref Point sourcePoint, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
}
