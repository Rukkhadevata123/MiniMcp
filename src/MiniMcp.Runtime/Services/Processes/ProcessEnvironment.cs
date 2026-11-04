using System.Diagnostics;

namespace MiniMcp.Services.Processes;

/// <summary>
/// Child processes inherit only a small allowlist of environment variables, so inherited tokens,
/// tool-specific overrides and secrets never leak into Bash, queries or the tunnel.
/// </summary>
internal static class ProcessEnvironment
{
    private static readonly string[] Shared =
    [
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "TMP", "TEMP", "TMPDIR",
        "SystemRoot", "WINDIR", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"
    ];

    /// <summary>Removes every inherited variable except the shared allowlist, <c>LC_*</c> and <paramref name="additionalKeys"/>.</summary>
    public static void Restrict(ProcessStartInfo start, params string[] additionalKeys)
    {
        foreach (var key in start.Environment.Keys.ToArray())
            if (!Keep(key, additionalKeys))
                start.Environment.Remove(key);
    }

    private static bool Keep(string key, string[] additionalKeys) =>
        key.StartsWith("LC_", StringComparison.OrdinalIgnoreCase) ||
        Shared.Contains(key, StringComparer.OrdinalIgnoreCase) ||
        additionalKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
}
