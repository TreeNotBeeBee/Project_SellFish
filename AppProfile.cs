using System.Security.Cryptography;
using System.Text;

namespace WindowPilot;

// UI acceptance tests use their own shortcut settings, window journal and instance.
internal static class AppProfile
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowPilot");

    internal static string DirectoryPath { get; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("WINDOWPILOT_PROFILE_DIR") is { Length: > 0 } path
            ? path : DefaultPath);

    internal static string MutexName
    {
        get
        {
            const string name = @"Local\WindowPilot.Desktop.Instance";
            string path = Path.TrimEndingDirectorySeparator(DirectoryPath);
            if (string.Equals(path, DefaultPath, StringComparison.OrdinalIgnoreCase)) return name;
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            return name + "." + Convert.ToHexString(hash.AsSpan(0, 12));
        }
    }
}
