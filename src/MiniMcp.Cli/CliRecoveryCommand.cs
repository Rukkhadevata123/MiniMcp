using System.Text;
using MiniMcp.Configuration;
using MiniMcp.Recovery;

namespace MiniMcp.Cli;

internal static class CliRecoveryCommand
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, TextReader? input,
        CancellationToken token, ConfigurationStore store, string workingDirectory)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"] ||
            (args.Length == 2 && args[0] is "list" or "show" or "restore" && args[1] is "--help" or "-h"))
        {
            output.WriteLine("用法：minimcp records list [--config <path>]");
            output.WriteLine("      minimcp records show <change-id> [--config <path>]");
            output.WriteLine("      minimcp records restore <change-id> [--config <path>]");
            output.WriteLine("show 显示记录详情、冲突及完整反向预览。恢复前请停止 run 或桌面服务；restore 在终端展示预览后逐次确认。");
            return CliExitCodes.Success;
        }
        var command = args[0];
        if (command is not ("list" or "show" or "restore")) return Invalid("无法识别记录命令。");
        var index = 1; var id = Guid.Empty; string? config = null;
        if (command != "list" && (args.Length <= index || !Guid.TryParse(args[index++], out id) || id == Guid.Empty))
            return Invalid("请提供有效的修改记录 ID。");
        if (index < args.Length)
        {
            if (args.Length != index + 2 || args[index] != "--config" || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith('-'))
                return Invalid("请使用 --config <path> 指定配置文件。");
            config = args[index + 1];
        }
        if (command == "restore" && input is null && (Console.IsInputRedirected || Console.IsOutputRedirected))
            return Invalid("恢复需要交互式终端。请先停止服务，再直接运行 restore 并查看完整预览。");
        try
        {
            var configuration = await store.LoadRecoveryAsync(config, workingDirectory, token).ConfigureAwait(false);
            await using var session = LocalRecoverySession.Open(configuration);
            output.WriteLine($"项目：{Display(configuration.Settings.ProjectRoot)}");
            output.WriteLine($"模式：{configuration.Policy.Mode} · 恢复会话：{session.RunId:N}");
            if (command == "list")
            {
                var rows = await session.ListAsync(token).ConfigureAwait(false);
                foreach (var row in rows)
                    output.WriteLine($"{Display(row.Name)}  {row.CreatedAtUtc:O}  {row.State}  {row.PathCount} 个路径" +
                        (row.Error is null ? "" : "  " + Display(row.Error)));
                output.WriteLine($"共 {rows.Length} 条记录。");
                return CliExitCodes.Success;
            }
            var details = await session.ReadAsync(id, token).ConfigureAwait(false);
            var preview = details.Preview;
            LocalRecoveryPreparation? preparation = null;
            if (command == "restore" && preview.Blockers.IsEmpty && preview.Diff is not null)
            { preparation = await session.PrepareAsync(id, token).ConfigureAwait(false); preview = preparation.Preview; details = preparation.Record; }
            output.WriteLine(details.ManifestJson);
            output.WriteLine("完整反向预览：");
            output.WriteLine(preview.Diff is null ? "当前无法生成完整预览。" : Display(preview.Diff, keepNewlines: true));
            foreach (var conflict in preview.Conflicts) output.WriteLine($"冲突：{Display(conflict.Path ?? "记录")} · {Display(conflict.Message)}");
            if (!preview.Blockers.IsEmpty) output.WriteLine("恢复限制：" + string.Join(", ", preview.Blockers));
            if (command == "show") return CliExitCodes.Success;
            if (preparation is null) return CliExitCodes.InvalidInput;
            output.WriteLine($"恢复将创建新的备份记录。请确认已停止其他服务，并已查看以上完整预览。");
            var confirmation = "RESTORE " + id.ToString("N");
            output.Write($"输入 {confirmation} 以确认，其他输入取消：");
            await output.FlushAsync(token).ConfigureAwait(false);
            var answer = await (input ?? Console.In).ReadLineAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (answer != confirmation) { output.WriteLine("恢复已取消。"); return CliExitCodes.Cancelled; }
            var result = await session.RestoreAsync(preparation, token).ConfigureAwait(false);
            output.WriteLine($"恢复结果：{result.State} · 新记录：{result.ChangeId:N} · 来源：{result.SourceChangeId:N}");
            foreach (var problem in result.Errors) error.WriteLine(Display(problem));
            return result.State == "Committed" && !result.CleanupIncomplete ? CliExitCodes.Success : CliExitCodes.Failure;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { error.WriteLine("记录操作已取消。"); return CliExitCodes.Cancelled; }
        catch (Exception failure) when (failure is ConfigurationLoadException or ConfigurationValidationException)
        { error.WriteLine("配置错误：" + CliText.Visible(failure.Message)); return CliExitCodes.InvalidInput; }
        catch (Exception failure)
        { error.WriteLine("记录操作失败：" + CliText.Visible(failure.Message)); return CliExitCodes.Failure; }

        int Invalid(string message) { error.WriteLine(message + " 使用 records --help 查看帮助。"); return CliExitCodes.InvalidInput; }
    }

    // Complete local preview, not a diagnostic message: never send it through log redaction/truncation.
    internal static string Display(string value, bool keepNewlines = false)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsControl(character) && !(keepNewlines && character == '\n')) result.Append($"\\u{(int)character:x4}");
            else result.Append(character);
        return result.ToString();
    }
}
