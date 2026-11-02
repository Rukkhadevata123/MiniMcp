using MiniMcp.Services.Workspace;
using System.Text;

namespace MiniMcp.Services.Patching;

internal sealed partial class PatchService
{
    private sealed record Chunk(string? Anchor, string[] Lines, bool EndOfFile);
    private sealed record Operation(string Action, string Path, string? MoveTo, string? Content, List<Chunk> Chunks);
    private static List<Operation> Parse(string patch)
    {
        if (string.IsNullOrEmpty(patch)) throw new WorkspaceException("patch is required.");
        try
        {
            if (ProjectWorkspace.Utf8.GetByteCount(patch) > 1024 * 1024)
                throw new WorkspaceException("Patch exceeds the 1 MiB limit.");
        }
        catch (EncoderFallbackException) { throw new WorkspaceException("Patch is not valid UTF-8 text."); }
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var length = lines.Length;
        if (lines[^1] == "") length--;
        if (length < 3 || lines[0] != "*** Begin Patch" || lines[length - 1] != "*** End Patch")
            throw new WorkspaceException("Expected *** Begin Patch and *** End Patch around file operations.");
        var result = new List<Operation>();
        var i = 1;
        while (i < length - 1)
        {
            result.Add(ParseOperation(lines, length, ref i));
            if (result.Count > 64) throw new WorkspaceException("A patch may contain at most 64 file operations.");
        }
        return result;
    }

    private static Operation ParseOperation(string[] lines, int length, ref int cursor)
    {
        var header = lines[cursor++];
        var action = ParseAction(header);
        var path = header[(header.IndexOf(": ", StringComparison.Ordinal) + 2)..];
        string? move = null, content = null;
        var chunks = new List<Chunk>();
        if (action == "add")
        {
            var added = new StringBuilder();
            while (cursor < length - 1 && lines[cursor].StartsWith('+'))
                added.Append(lines[cursor++][1..]).Append('\n');
            content = added.ToString();
        }
        if (action == "update")
        {
            if (lines[cursor].StartsWith("*** Move to: ", StringComparison.Ordinal))
                move = lines[cursor++][13..];
            while (cursor < length - 1 && (lines[cursor] == "@@" || lines[cursor].StartsWith("@@ ", StringComparison.Ordinal)))
            {
                var anchor = lines[cursor] == "@@" ? null : lines[cursor][3..];
                cursor++;
                var edits = new List<string>();
                while (cursor < length - 1 && lines[cursor].Length > 0 && lines[cursor][0] is ' ' or '+' or '-')
                    edits.Add(lines[cursor++]);
                if (edits.Count == 0 || edits.All(line => line[0] == ' '))
                    throw new WorkspaceException("Each @@ chunk must contain a change; blank context lines need a space prefix.");
                var eof = lines[cursor] == "*** End of File";
                if (eof) cursor++;
                chunks.Add(new Chunk(anchor, edits.ToArray(), eof));
                if (eof) break;
            }
            if (chunks.Count == 0 && move is null) throw new WorkspaceException("Update File requires a chunk or Move to header.");
        }
        return new Operation(action, path, move, content, chunks);
    }

    private static string ParseAction(string header)
    {
        if (header.StartsWith("*** Add File: ", StringComparison.Ordinal)) return "add";
        if (header.StartsWith("*** Delete File: ", StringComparison.Ordinal)) return "delete";
        if (header.StartsWith("*** Update File: ", StringComparison.Ordinal)) return "update";
        throw new WorkspaceException("Expected an Add File, Delete File or Update File header.");
    }

    private static string Update(string text, List<Chunk> chunks)
    {
        var original = ProjectWorkspace.SplitLines(text);
        var ending = original.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\n";
        var finalNewline = original.Count == 0 || original[^1].Ending.Length > 0;
        var output = new List<TextLine>();
        var cursor = 0;
        foreach (var chunk in chunks)
        {
            var start = cursor;
            if (chunk.Anchor is not null)
                start = FindUnique(original, [chunk.Anchor], cursor, false) + 1;
            var old = chunk.Lines.Where(line => line[0] != '+').Select(line => line[1..]).ToArray();
            var index = old.Length == 0 ? original.Count : FindUnique(original, old, start, chunk.EndOfFile);
            output.AddRange(original.GetRange(cursor, index - cursor));
            var consumed = index;
            foreach (var edit in chunk.Lines)
            {
                if (edit[0] == ' ') output.Add(original[consumed++]);
                else if (edit[0] == '-') consumed++;
                else output.Add(new TextLine(edit[1..], ending));
            }
            cursor = consumed;
        }
        output.AddRange(original.Skip(cursor));
        var result = new StringBuilder();
        for (var i = 0; i < output.Count; i++)
        {
            var line = output[i];
            result.Append(line.Text);
            if (i == output.Count - 1 && !finalNewline) continue;
            result.Append(line.Ending.Length == 0 ? ending : line.Ending);
        }
        return result.ToString();
    }

    private static int FindUnique(List<TextLine> lines, string[] expected, int start, bool endOfFile)
    {
        if (endOfFile)
        {
            var index = lines.Count - expected.Length;
            if (index >= start && lines.Skip(index).Select(line => line.Text).SequenceEqual(expected)) return index;
            throw new WorkspaceException("Patch context did not match exactly at the end of the file.");
        }
        // Prefix matching keeps repeated lines linear in the file/context size.
        var prefix = new int[expected.Length];
        for (int i = 1, length = 0; i < expected.Length; i++)
        {
            while (length > 0 && expected[i] != expected[length]) length = prefix[length - 1];
            if (expected[i] == expected[length]) length++;
            prefix[i] = length;
        }
        var match = -1;
        for (int i = start, length = 0; i < lines.Count; i++)
        {
            while (length > 0 && lines[i].Text != expected[length]) length = prefix[length - 1];
            if (lines[i].Text == expected[length]) length++;
            if (length != expected.Length) continue;
            if (match >= 0) throw new WorkspaceException("Patch context is ambiguous; include more context or an exact @@ anchor.");
            match = i - expected.Length + 1;
            length = prefix[length - 1];
        }
        if (match < 0) throw new WorkspaceException("Patch context did not match exactly; read the file again.");
        return match;
    }

}
