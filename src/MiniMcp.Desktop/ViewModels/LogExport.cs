using System.Text;

namespace MiniMcp.Desktop.ViewModels;

public static class LogExport
{
    // The UI supplies a snapshot of already sanitized/escaped visible rows and an explicit picker path.
    public static async Task WriteAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择绝对文件路径。");
        if (text.Length > RuntimeMonitor.MaxCharacters + 4096) throw new InvalidDataException("日志导出超过显示缓冲上限。");
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("导出路径必须指向文件。");
        var temporary = Path.Combine(parent, ".minimcp-log-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(text);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
