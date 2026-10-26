namespace MiniMcp.Diagnostics;

/// <summary>
/// Uses the same bounded redaction policy as Runtime logs for ordinary local diagnostics.
/// The result is not an approval/tool body; terminal consumers must also escape control characters.
/// </summary>
public static class RuntimeDiagnosticText
{
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return LogSanitizer.Sanitize(text, RuntimeDiagnostics.MaxEntryCharacters, out _);
    }
}
