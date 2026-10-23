using MiniMcp.Configuration;

internal static class ExternalProgramLocatorChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"minimcp-programs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        var settings = Path.Combine(temporary, "settings.json");
        var cloudflared = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        string Put(params string[] parts)
        {
            var path = Path.Combine([temporary, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "This is a discovery fixture, not an executable.");
            return path;
        }
        void Assert(bool condition)
        {
            if (!condition) throw new Exception("Program discovery assertion failed.");
        }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception exception) when (exception is ArgumentException or FileNotFoundException) { return; }
            throw new Exception("An invalid explicit program path was accepted.");
        }
        try
        {
            await check("explicit program files are anchored to JSON and take precedence without execution", () =>
            {
                var explicitFile = Put("custom", "chosen-program");
                var fallback = Put("fallback", cloudflared);
                var directories = new[] { Path.GetDirectoryName(fallback)! };
                Assert(ExternalProgramLocator.FindBash("custom/chosen-program", settings) == explicitFile);
                Assert(ExternalProgramLocator.FindCloudflared(explicitFile, settings) == explicitFile);
                Assert(ExternalProgramLocator.FindBash("custom/chosen-program", settings, [], null) == explicitFile);
                Assert(ExternalProgramLocator.FindCloudflared(explicitFile, settings, directories, null) == explicitFile);
                Reject(() => ExternalProgramLocator.FindCloudflared("missing", settings, directories, null));
                Reject(() => ExternalProgramLocator.FindBash("missing", settings, [], Path.GetDirectoryName(explicitFile)));
                Reject(() => ExternalProgramLocator.FindCloudflared("custom", settings, directories, null));
                Reject(() => ExternalProgramLocator.FindBash(" ", settings, [], null));
                return Task.CompletedTask;
            });

            await check("automatic discovery uses known locations before PATH and preserves PATH order", () =>
            {
                var installed = Put("installed", cloudflared);
                var first = Put("first", cloudflared);
                var second = Put("second", cloudflared);
                var searchPath = string.Join(Path.PathSeparator, Path.GetDirectoryName(first), Path.GetDirectoryName(second));
                Assert(ExternalProgramLocator.FindCloudflared("", settings, [Path.GetDirectoryName(installed)!], searchPath) == installed);
                Assert(ExternalProgramLocator.FindCloudflared("", settings, [], searchPath) == first);
                return Task.CompletedTask;
            });

            await check("automatic discovery ignores empty and relative PATH entries and never scans subdirectories", () =>
            {
                var previous = Environment.CurrentDirectory;
                var trap = Path.Combine(temporary, "trap");
                Put("trap", cloudflared);
                Put("trap", "relative", cloudflared);
                Put("trap", "nested", "bin", cloudflared);
                try
                {
                    Environment.CurrentDirectory = trap;
                    Assert(ExternalProgramLocator.FindCloudflared("", settings, [], string.Join(Path.PathSeparator, "", ".", "relative", "")) is null);
                    Assert(ExternalProgramLocator.FindCloudflared("", settings, [], Path.Combine(trap, "nested")) is null);
                    Assert(ExternalProgramLocator.FindBash("", settings, [], null) is null);
                }
                finally { Environment.CurrentDirectory = previous; }
                return Task.CompletedTask;
            });

            await check("programs in PATH directories with spaces are found using literal native paths", () =>
            {
                var file = Put("tools with spaces", cloudflared);
                var directory = Path.GetDirectoryName(file)!;
                Assert(ExternalProgramLocator.FindCloudflared("", settings, [], directory) == file);
                if (OperatingSystem.IsWindows())
                    Assert(ExternalProgramLocator.FindCloudflared("", settings, [], $"\"{directory}\"") == file);
                return Task.CompletedTask;
            });

            await check(OperatingSystem.IsWindows()
                ? "Windows discovers Git Bash from standard layouts and skips standalone or WSL launchers"
                : "Linux discovers bash from known directories and absolute PATH entries", () =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var launcher = Put("standalone", "bash.exe");
                    var gitRoot = Path.Combine(temporary, "portable git");
                    var shell = Put("portable git", "usr", "bin", "bash.exe");
                    var wrapper = Put("portable git", "bin", "bash.exe");
                    Assert(ExternalProgramLocator.FindBash("", settings, [gitRoot], Path.GetDirectoryName(launcher)) is null);
                    Put("portable git", "cmd", "git.exe");
                    Assert(ExternalProgramLocator.FindBash("", settings, [gitRoot], null) == wrapper);
                    foreach (var directory in new[] { "cmd", "bin", Path.Combine("usr", "bin") })
                    {
                        var searchPath = string.Join(Path.PathSeparator, Path.GetDirectoryName(launcher), Path.Combine(gitRoot, directory));
                        Assert(ExternalProgramLocator.FindBash("", settings, [], searchPath) == wrapper);
                    }
                    File.Delete(wrapper);
                    Assert(ExternalProgramLocator.FindBash("", settings, [], Path.Combine(gitRoot, "cmd")) == shell);
                    Assert(ExternalProgramLocator.FindBash(launcher, settings, [], null) == launcher);
                    Put("usr", "cmd", "git.exe");
                    var renamedShell = Put("usr", "usr", "bin", "bash.exe");
                    var renamedBin = Path.Combine(temporary, "usr", "bin");
                    Assert(ExternalProgramLocator.FindBash("", settings, [], renamedBin) == renamedShell);
                }
                else
                {
                    var bash = Put("shell", "bash");
                    var directory = Path.GetDirectoryName(bash)!;
                    Assert(ExternalProgramLocator.FindBash("", settings, [directory], null) == bash);
                    Assert(ExternalProgramLocator.FindBash("", settings, [], directory) == bash);
                    Assert(ExternalProgramLocator.FindBash("", settings, [], ".") is null);
                }
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
