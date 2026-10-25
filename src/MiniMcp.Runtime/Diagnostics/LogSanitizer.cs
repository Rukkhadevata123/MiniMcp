using System.Text.RegularExpressions;

namespace MiniMcp.Runtime;

/// <summary>
/// Redacts recognizable credential fields and authentication forms. This is not a classifier for
/// arbitrary secrets: callers must not log token-file contents, tool bodies or approval contents.
/// Oversized/unparseable input is omitted as a whole, before clipping could hide a sensitive key.
/// </summary>
internal static partial class LogSanitizer
{
    internal const int MaxInputCharacters = 64 * 1024;
    internal const string Redacted = "[REDACTED]";
    internal const string Omitted = "[log entry omitted: input limit exceeded]";

    internal static bool IsSensitiveKey(string key) => SensitiveKey().IsMatch(key);

    internal static string Sanitize(string input, int limit, out bool truncated)
    {
        truncated = input.Length > MaxInputCharacters;
        if (truncated) return Clip(Omitted, limit, out _);
        try
        {
            if (input.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
                input = "[log entry omitted: private key]";
            else
            {
                input = CredentialField().Replace(input, match => match.Groups["prefix"].Value + Redacted);
                input = AuthenticationValue().Replace(input, match => match.Groups["scheme"].Value + Redacted);
                input = JwtValue().Replace(input, Redacted);
                input = UrlCredentials().Replace(input, match => match.Groups["scheme"].Value + Redacted + "@");
            }
            var result = Clip(input, limit, out var clipped);
            truncated |= clipped;
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            truncated = true;
            return Clip("[log entry omitted: redaction limit exceeded]", limit, out _);
        }
    }

    internal static string Clip(string value, int limit, out bool truncated)
    {
        truncated = value.Length > limit;
        if (!truncated) return value;
        const string suffix = "…";
        var length = limit - suffix.Length;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + suffix;
    }

    [GeneratedRegex(@"token|secret|password|passwd|authorization|cookie|jwt|api[-_ ]?key|credential|private[-_ ]?key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex SensitiveKey();

    // Quoted values may span lines. An unquoted credential consumes the rest of the line, including
    // folded header continuations; sacrificing adjacent text is safer than retaining part of a value.
    [GeneratedRegex("""(?<prefix>["']?\b[\w.-]*(?:token|secret|password|passwd|authorization|cookie|jwt|api[-_ ]?key|credential|private[-_ ]?key)[\w.-]*["']?\s*[:=]\s*)(?:"(?:\\[\s\S]|[^"\\])*(?:"|\\?$)|'(?:\\[\s\S]|[^'\\])*(?:'|\\?$)|[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex CredentialField();

    [GeneratedRegex(@"\b(?<scheme>(?:Bearer|Basic)\s+)[A-Za-z0-9_~+./=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex AuthenticationValue();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex JwtValue();

    [GeneratedRegex(@"(?<scheme>https?://)[^\s/@]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 100)]
    private static partial Regex UrlCredentials();
}
