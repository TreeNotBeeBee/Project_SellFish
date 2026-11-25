using System.Runtime.InteropServices;
using System.Text;

namespace WindowPilot;

public partial class App
{
    private async Task CheckNativeSendAsync(NativeControlBinding edit, nint root)
    {
        // Native dialogs have no web form boundary. Only the explicit pick may dispatch to this button.
        try { await edit.SendAsync("输入中文", "输入中文"); throw new Exception("dispatched without a button"); }
        catch (SendButtonSelectionRequiredException) { Check(Count() == "0", "native send requires explicit target"); }
        if (!GetWindowRect(GetDlgItem(root, 4), out var rect)) throw new Exception("Missing fixture button");
        await edit.AttachSendButtonAtAsync((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2, default);
        try { await edit.SendAsync("stale", "different"); throw new Exception("stale draft sent"); }
        catch (InvalidOperationException) { Check(Count() == "0", "conflict never invokes the button"); }
        await edit.SendAsync("输入中文", "输入中文");
        await Wait(() => Count() == "1");
        SetFixtureText(root, 4, "Changed action");
        try { await edit.SendAsync("输入中文", "输入中文"); throw new Exception("renamed button reused"); }
        catch (SendButtonSelectionRequiredException) { Check(Count() == "1", "renamed button cannot be invoked as the old action"); }
        string Count()
        {
            var text = new StringBuilder(32);
            SendMessageW(GetDlgItem(root, 5), 0x000D, text.Capacity, text);
            return text.ToString();
        }
    }

    private static int _sendCount;
    private static readonly SubclassProc CountButton = OnButton;
    private static void InstallSendCounter(nint root, nint module)
    {
        CreateWindowExW(0, "Static", "0", 0x40000000, 0, 0, 1, 1, root, 5, module, 0);
        if (!SetWindowSubclass(root, CountButton, 1, 0)) throw new Exception("Missing fixture click listener");
    }

    private static nint OnButton(nint hwnd, uint msg, nint wp, nint lp, nuint id, nuint data)
    {
        if (msg == 0x0111 && wp == 4 && lp == GetDlgItem(hwnd, 4)) SetFixtureText(hwnd, 5, (++_sendCount).ToString());
        return DefSubclassProc(hwnd, msg, wp, lp);
    }
    private delegate nint SubclassProc(nint hwnd, uint msg, nint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wp, nint lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageW(nint hwnd, int msg, nint wp, StringBuilder text);
}
