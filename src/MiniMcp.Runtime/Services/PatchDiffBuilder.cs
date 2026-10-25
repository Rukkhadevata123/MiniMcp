using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace MiniMcp.Services;

// Display-only inputs. Exact target permissions can describe restoration without manufacturing a PatchPlan.
internal sealed record PatchDiffChange(PatchChange Summary, TextFile? Before, ImmutableArray<byte>? After,
    UnixFileMode? AfterMode, bool AfterModeIsCreateRequest);
internal sealed record PatchDiff(string Diff, ImmutableArray<PatchFileDiff> Files, PatchStatistics Statistics);

internal static class PatchDiffBuilder
{
    internal const int MaxDiffUtf8Bytes = 256 * 1024;
    internal const int MaxMatrixCells = 1_000_000;
    internal const int MaxWorkUnits = 8_000_000;
    private const int ContextLines = 3;

    public static PatchPreview Create(PatchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var diff = Create(plan.Changes.Select(change => new PatchDiffChange(change.Summary, change.Before, change.After,
            change.Before is null ? plan.NewFileCreateMode : change.Before.Mode, change.Before is null && plan.NewFileCreateMode is not null)).ToImmutableArray(), cancellationToken);
        return new(plan, diff.Diff, diff.Files, diff.Statistics);
    }

    internal static PatchDiff Create(ImmutableArray<PatchDiffChange> changes, CancellationToken cancellationToken = default, bool forRecovery = false)
    {
        var budget = new Budget(cancellationToken, forRecovery);
        var output = new Output(budget);
        var files = ImmutableArray.CreateBuilder<PatchFileDiff>();
        var total = new PatchStatistics(0, 0, 0, 0);
        output.Append("# Review diff: quoted lines escape controls and include their original line endings.\n");
        foreach (var change in changes)
        {
            budget.Spend((change.Before?.Bytes.Length ?? 0) + (change.After?.Length ?? 0) + 1);
            var beforeText = change.Before?.Text ?? "";
            var afterBom = change.After is { Length: >= 3 } bytes && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
            var afterText = change.After is { } after
                ? ProjectWorkspace.Utf8.GetString(after.AsSpan()[(afterBom ? 3 : 0)..]) : "";
            var beforeLines = ProjectWorkspace.SplitLines(beforeText);
            var afterLines = ProjectWorkspace.SplitLines(afterText);
            budget.Spend(beforeLines.Count + afterLines.Count);

            output.Append("\n# action: " + change.Summary.Action + "\n--- ");
            output.Quote(change.Before is null ? "/dev/null" : "a/" + change.Summary.Path);
            output.Append("\n+++ ");
            output.Quote(change.After is null ? "/dev/null" : "b/" + (change.Summary.MoveTo ?? change.Summary.Path));
            output.Append("\n");
            Metadata(output, "before", change.Before is not null, change.Before?.HasBom ?? false,
                beforeLines, change.Before?.Mode, creation: false);
            Metadata(output, "after", change.After is not null, afterBom, afterLines,
                change.AfterMode, creation: change.AfterModeIsCreateRequest);

            var edits = Compare(beforeLines, afterLines, budget);
            int addedLines = 0, removedLines = 0;
            long addedBytes = afterBom && change.Before?.HasBom != true ? 3 : 0;
            long removedBytes = change.Before?.HasBom == true && !afterBom ? 3 : 0;
            foreach (var edit in edits)
            {
                budget.Spend(edit.Kind == ' ' ? 1 : edit.Line.Text.Length + edit.Line.Ending.Length + 1);
                if (edit.Kind == '+') { addedLines++; addedBytes += ProjectWorkspace.Utf8.GetByteCount(edit.Line.Text) + edit.Line.Ending.Length; }
                if (edit.Kind == '-') { removedLines++; removedBytes += ProjectWorkspace.Utf8.GetByteCount(edit.Line.Text) + edit.Line.Ending.Length; }
            }
            Hunks(output, edits, budget);
            var statistics = new PatchStatistics(addedLines, removedLines, addedBytes, removedBytes);
            files.Add(new PatchFileDiff(change.Summary, statistics));
            total += statistics;
        }
        return new(output.ToString(), files.ToImmutable(), total);
    }

    private static void Metadata(Output output, string label, bool exists, bool bom,
        List<TextLine> lines, UnixFileMode? mode, bool creation)
    {
        output.Append("# " + label + ": ");
        if (!exists) { output.Append("absent\n"); return; }
        var lf = lines.Any(line => line.Ending == "\n");
        var crlf = lines.Any(line => line.Ending == "\r\n");
        var endings = lf && crlf ? "mixed" : crlf ? "CRLF" : lf ? "LF" : "none";
        var permission = mode is { } value ? Convert.ToString((int)value, 8).PadLeft(4, '0') : "not-tracked";
        output.Append($"utf8-bom={(bom ? "yes" : "no")}; eol={endings}; eof-newline={(lines.Count > 0 && lines[^1].Ending.Length != 0 ? "yes" : "no")}; mode={permission}");
        if (creation && mode is not null) output.Append(" (creation request; umask applies)");
        output.Append("\n");
    }

    private readonly record struct Edit(char Kind, TextLine Line);

    private static List<Edit> Compare(List<TextLine> before, List<TextLine> after, Budget budget)
    {
        var comparer = new LineComparer(budget);
        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && comparer.Equals(before[prefix], after[prefix])) prefix++;
        var suffix = 0;
        while (suffix < before.Count - prefix && suffix < after.Count - prefix &&
            comparer.Equals(before[before.Count - suffix - 1], after[after.Count - suffix - 1])) suffix++;
        var oldCount = before.Count - prefix - suffix;
        var newCount = after.Count - prefix - suffix;
        var edits = new List<Edit>();
        for (var i = 0; i < prefix; i++) { budget.Spend(1); edits.Add(new(' ', before[i])); }
        if (oldCount == 0 || newCount == 0)
        {
            for (var i = 0; i < oldCount; i++) { budget.Spend(1); edits.Add(new('-', before[prefix + i])); }
            for (var i = 0; i < newCount; i++) { budget.Spend(1); edits.Add(new('+', after[prefix + i])); }
        }
        else
        {
            var cells = budget.Matrix(oldCount + 1, newCount + 1);
            // Intern exact lines once, so matrix comparisons do not repeatedly scan long strings.
            var ids = new Dictionary<TextLine, int>(comparer);
            int Id(TextLine line)
            {
                if (!ids.TryGetValue(line, out var id)) { id = ids.Count; ids.Add(line, id); }
                return id;
            }
            var oldIds = before.Skip(prefix).Take(oldCount).Select(Id).ToArray();
            var newIds = after.Skip(prefix).Take(newCount).Select(Id).ToArray();
            var stride = newCount + 1;
            var lengths = new int[cells];
            for (var i = oldCount - 1; i >= 0; i--)
            {
                budget.CheckCancellation();
                for (var j = newCount - 1; j >= 0; j--)
                    lengths[i * stride + j] = oldIds[i] == newIds[j] ? lengths[(i + 1) * stride + j + 1] + 1
                        : Math.Max(lengths[(i + 1) * stride + j], lengths[i * stride + j + 1]);
            }
            int oldIndex = 0, newIndex = 0;
            while (oldIndex < oldCount || newIndex < newCount)
            {
                budget.Spend(1);
                if (oldIndex < oldCount && newIndex < newCount && oldIds[oldIndex] == newIds[newIndex])
                { edits.Add(new(' ', before[prefix + oldIndex++])); newIndex++; }
                else if (oldIndex < oldCount && (newIndex == newCount ||
                    lengths[(oldIndex + 1) * stride + newIndex] >= lengths[oldIndex * stride + newIndex + 1]))
                    edits.Add(new('-', before[prefix + oldIndex++]));
                else edits.Add(new('+', after[prefix + newIndex++]));
            }
        }
        for (var i = before.Count - suffix; i < before.Count; i++) { budget.Spend(1); edits.Add(new(' ', before[i])); }
        return edits;
    }

    private static void Hunks(Output output, List<Edit> edits, Budget budget)
    {
        var changes = new List<int>();
        for (var i = 0; i < edits.Count; i++) { budget.Spend(1); if (edits[i].Kind != ' ') changes.Add(i); }
        if (changes.Count == 0) { output.Append("# No line changes.\n"); return; }
        int cursor = 0, oldLine = 1, newLine = 1;
        void Advance(Edit edit) { if (edit.Kind != '+') oldLine++; if (edit.Kind != '-') newLine++; }
        for (var change = 0; change < changes.Count; change++)
        {
            var start = Math.Max(0, changes[change] - ContextLines);
            while (change + 1 < changes.Count && changes[change + 1] - changes[change] - 1 <= 2 * ContextLines) change++;
            var end = Math.Min(edits.Count, changes[change] + ContextLines + 1);
            while (cursor < start) { budget.Spend(1); Advance(edits[cursor++]); }
            int oldCount = 0, newCount = 0;
            for (var i = start; i < end; i++)
            { budget.Spend(1); if (edits[i].Kind != '+') oldCount++; if (edits[i].Kind != '-') newCount++; }
            output.Append(FormattableString.Invariant($"@@ -{(oldCount == 0 ? oldLine - 1 : oldLine)},{oldCount} +{(newCount == 0 ? newLine - 1 : newLine)},{newCount} @@\n"));
            while (cursor < end)
            {
                var edit = edits[cursor++];
                output.Append(edit.Kind.ToString());
                output.Quote(edit.Line.Text + edit.Line.Ending);
                output.Append("\n");
                Advance(edit);
            }
        }
    }

    private sealed class LineComparer(Budget budget) : IEqualityComparer<TextLine>
    {
        public bool Equals(TextLine? left, TextLine? right)
        {
            budget.Spend((left?.Text.Length ?? 0) + (right?.Text.Length ?? 0) + 5);
            return left == right;
        }
        public int GetHashCode(TextLine line)
        {
            budget.Spend(line.Text.Length + line.Ending.Length + 1);
            return HashCode.Combine(StringComparer.Ordinal.GetHashCode(line.Text), StringComparer.Ordinal.GetHashCode(line.Ending));
        }
    }

    private sealed class Budget(CancellationToken cancellationToken, bool forRecovery)
    {
        private long _work = MaxWorkUnits;
        private long _cells = MaxMatrixCells;
        public void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();
        public void Spend(long amount)
        {
            CheckCancellation();
            if (amount > _work) throw TooComplex();
            _work -= amount;
        }
        public int Matrix(int rows, int columns)
        {
            var size = (long)rows * columns;
            if (size > _cells) throw TooComplex();
            Spend(size);
            _cells -= size;
            return (int)size;
        }
        private WorkspaceException TooComplex() => new(forRecovery
            ? "Complete recovery preview exceeds the computation limit; inspect this record locally."
            : "Patch diff exceeds the computation limit; split the patch into smaller changes.");
        public WorkspaceException DisplayLimit() => new(forRecovery
            ? "Complete recovery preview exceeds the 256 KiB UTF-8 display limit; inspect this record locally."
            : "Complete patch diff exceeds the 256 KiB UTF-8 display limit; split the patch into smaller changes.");
    }

    private sealed class Output(Budget budget)
    {
        private readonly StringBuilder _text = new();
        private int _bytes;
        public void Append(string text)
        {
            budget.Spend(text.Length);
            var bytes = ProjectWorkspace.Utf8.GetByteCount(text);
            if (bytes > MaxDiffUtf8Bytes - _bytes)
                throw budget.DisplayLimit();
            _text.Append(text);
            _bytes += bytes;
        }
        public void Quote(string text)
        {
            Append("\"");
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                var category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                if (character == '\\') Append("\\\\");
                else if (character == '"') Append("\\\"");
                else if (character == '\n') Append("\\n");
                else if (character == '\r') Append("\\r");
                else if (character == '\t') Append("\\t");
                else if (char.IsControl(character) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    Append("\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    if (char.IsHighSurrogate(character) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        Append("\\u" + ((int)text[++i]).ToString("x4", CultureInfo.InvariantCulture));
                }
                else if (char.IsHighSurrogate(character) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) Append(text.Substring(i++, 2));
                else Append(character.ToString());
            }
            Append("\"");
        }
        public override string ToString() => _text.ToString();
    }
}
