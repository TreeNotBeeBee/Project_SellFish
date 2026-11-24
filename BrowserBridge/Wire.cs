using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WindowPilot;

internal static class BrowserWire
{
    public const int MaxBytes = 950 * 1024;
    public const string Origin = "chrome-extension://kahblpkceponikaklinoblfmgdnahdfc/";
    public const string HostExe = "SellFish.BrowserHost.exe";
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            string owner = identity.User!.Value + "/" + process.SessionId + "/" + Path.TrimEndingDirectorySeparator(AppProfile.DirectoryPath).ToUpperInvariant();
            return "SellFish.Browser." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)).AsSpan(0, 16));
        }
    }

    // Native messaging uses a little-endian uint32 length on Windows. Enforce a lower
    // bound than Chromium's 1 MiB limit before allocating a payload on either side.
    internal static async ValueTask<byte[]?> ReadAsync(Stream input, CancellationToken token)
    {
        byte[] header = new byte[sizeof(int)];
        if (await input.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxBytes) throw new InvalidDataException("浏览器消息长度无效。");
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        await input.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return payload;
    }

    internal static async ValueTask WriteAsync(Stream output, byte[] payload, CancellationToken token)
    {
        if (payload.Length is <= 0 or > MaxBytes) throw new InvalidDataException("浏览器消息过长。");
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await output.WriteAsync(header, token).ConfigureAwait(false);
        await output.WriteAsync(payload, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    internal static bool PeerIs(PipeStream pipe, string executable)
    {
        uint pid;
        bool found = pipe is NamedPipeClientStream ? GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid)
            : GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid);
        if (!found) return false;
        using SafeProcessHandle process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process.IsInvalid) return false;
        var name = new StringBuilder(32768);
        int size = name.Capacity;
        return QueryFullProcessImageNameW(process, 0, name, ref size) &&
            string.Equals(name.ToString(), Path.Combine(AppContext.BaseDirectory, executable), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder path, ref int size);
}
