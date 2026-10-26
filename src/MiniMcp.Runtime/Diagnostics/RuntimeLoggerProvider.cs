using Microsoft.Extensions.Logging;

namespace MiniMcp.Diagnostics;

/// <summary>A per-run ILogger adapter; the Runtime, rather than Host disposal, owns its buffer.</summary>
internal sealed class RuntimeLoggerProvider(RuntimeDiagnostics diagnostics, Guid runId) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(diagnostics, runId, categoryName);
    public void Dispose() { } // No owned IO; EndRun disables even cached loggers from a disposed host.

    private sealed class Logger(RuntimeDiagnostics diagnostics, Guid runId, string category) : ILogger
    {
        private readonly (string Value, bool Truncated) _category = SanitizeCategory(category);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel is >= LogLevel.Information and < LogLevel.None && diagnostics.IsAccepting(runId);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            var truncated = false;
            try
            {
                message = formatter(state, exception) ?? "";
                if (message.Length > LogSanitizer.MaxInputCharacters)
                {
                    message = LogSanitizer.Omitted;
                    truncated = true;
                }
                if (exception is not null && !truncated)
                {
                    var details = exception.ToString();
                    message = message.Length > LogSanitizer.MaxInputCharacters || details.Length > LogSanitizer.MaxInputCharacters
                        ? LogSanitizer.Omitted : message + "\n" + details;
                    truncated |= message == LogSanitizer.Omitted;
                }
                // Custom formatters may encode, split or reformat a credential value. Withhold the
                // whole rendered entry rather than assuming that replacing its ToString is enough.
                if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                {
                    var count = 0;
                    foreach (var field in fields)
                    {
                        if (++count > 128) { message = LogSanitizer.Omitted; truncated = true; break; }
                        if (field.Key.Length > 256) { message = LogSanitizer.Omitted; truncated = true; break; }
                        if (field.Key == "{OriginalFormat}" || !LogSanitizer.IsSensitiveKey(field.Key)) continue;
                        message = "[log entry redacted: credential field]";
                        break;
                    }
                }
            }
            catch
            {
                // A broken formatter/value must not break request execution or leak its exception.
                message = "[log entry omitted: formatting failed]";
                truncated = true;
            }
            message = LogSanitizer.Sanitize(message, RuntimeDiagnostics.MaxEntryCharacters, out var messageClipped);
            diagnostics.Write(runId, logLevel, _category.Value, eventId.Id, message, truncated || messageClipped || _category.Truncated);
        }

        private static (string Value, bool Truncated) SanitizeCategory(string category)
        {
            var value = LogSanitizer.Sanitize(category, RuntimeDiagnostics.MaxCategoryCharacters, out var truncated);
            return (value, truncated);
        }
    }
}
