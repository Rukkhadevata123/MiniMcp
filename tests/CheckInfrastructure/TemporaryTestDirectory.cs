// Fixture directories live directly under the system temp directory as minimcp-<tag>-<guid>.
internal static class TemporaryTestDirectory
{
    /// <summary>Returns a fresh fixture path; the caller creates whatever structure it needs.</summary>
    public static string Reserve(string tag) =>
        Path.Combine(Path.GetTempPath(), $"minimcp-{tag}-{Guid.NewGuid():N}");

    /// <summary>
    /// Deletes a path produced by <see cref="Reserve"/> and refuses anything else. Windows sharing
    /// violations get the bounded retry from <see cref="CheckRunner.DeleteTemporaryDirectory"/>, and a
    /// failure inside a check is reported as a cleanup failure instead of replacing the check's own.
    /// </summary>
    public static void Delete(string directory)
    {
        var target = Path.GetFullPath(directory);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(target), temp, comparison) ||
            !Path.GetFileName(target).StartsWith("minimcp-", StringComparison.Ordinal))
            throw new IOException($"Refusing to delete a path that is not a MiniMcp test fixture: {target}");

        CheckRunner.Cleanup(target, () => CheckRunner.DeleteTemporaryDirectory(target));
    }
}
