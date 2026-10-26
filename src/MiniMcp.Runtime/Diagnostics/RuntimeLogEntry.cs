using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace MiniMcp.Diagnostics;

/// <summary>A sanitized, immutable diagnostic. Sequence numbers span runs and reveal dropped entries.</summary>
public sealed record RuntimeLogEntry(long Sequence, DateTimeOffset Timestamp, Guid RunId,
    LogLevel Level, string Category, int EventId, string Message, bool Truncated);

/// <summary>Only sanitized strings are retained. CharacterCount includes categories and messages.</summary>
public sealed record RuntimeLogSnapshot(ImmutableArray<RuntimeLogEntry> Entries, int CharacterCount);
