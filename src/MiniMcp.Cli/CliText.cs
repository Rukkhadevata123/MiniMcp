using MiniMcp.Diagnostics;
using System.Text;

namespace MiniMcp.Cli;

internal static class CliText
{
    /// <summary>Sanitized text for log lines and messages: secrets redacted, every control character escaped.</summary>
    internal static string Visible(string value) => Escape(RuntimeDiagnosticText.Sanitize(value));

    /// <summary>
    /// Escapes control characters as <c>\uXXXX</c> so terminal output cannot be spoofed. Recovery
    /// previews pass <paramref name="keepNewlines"/> because a diff must stay complete and unredacted.
    /// </summary>
    internal static string Escape(string value, bool keepNewlines = false)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsControl(character) && !(keepNewlines && character == '\n'))
                result.Append($"\\u{(int)character:x4}");
            else
                result.Append(character);
        return result.ToString();
    }
}
