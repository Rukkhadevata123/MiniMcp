using MiniMcp.Configuration;
using MiniMcp.Services;

namespace MiniMcp.Recovery;

internal static class RecoveryStorageLimits
{
    public const long MaxStoreBytes = 1024L * 1024 * 1024;
    public const int MaxRecordSlots = 1000;
    public const long MaxRecordBytes = 64L * ProjectWorkspace.MaxFileBytes + RecoveryManifestJson.MaxManifestBytes;
    public const int MaxScanEntries = 100_000;
    public const int MaxDirectoryDepth = 64;
    public const string LockFileName = ".store.lock";
}

// Observed usage, not a reservation. Writers must inspect again under their storage mutation lock.
internal sealed record RecoveryStorageUsage(long Bytes, int RecordSlots)
{
    // A current feasibility check, never a reservation of future capacity.
    public void EnsureCanBeginApplying(long recordBytes, int oldBytes, int applyingBytes, int maximumFinalBytes)
    {
        EnsureCanReplaceManifest(recordBytes, oldBytes, applyingBytes);
        var after = new RecoveryStorageUsage(checked(Bytes - oldBytes + applyingBytes), RecordSlots);
        after.EnsureCanReplaceManifest(checked(recordBytes - oldBytes + applyingBytes), applyingBytes, maximumFinalBytes);
    }

    public void EnsureCanReplaceManifest(long recordBytes, int oldBytes, int newBytes)
    {
        if (oldBytes is <= 0 or > RecoveryManifestJson.MaxManifestBytes || newBytes is <= 0 or > RecoveryManifestJson.MaxManifestBytes ||
            recordBytes < oldBytes || recordBytes > RecoveryStorageLimits.MaxRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(recordBytes), "Use actual bounded record and manifest sizes.");
        if (RecordSlots is < 1 or > RecoveryStorageLimits.MaxRecordSlots || Bytes < recordBytes || Bytes > RecoveryStorageLimits.MaxStoreBytes)
            throw new InvalidDataException("Recovery usage is inconsistent or outside its limits.");
        if (recordBytes - oldBytes + newBytes > RecoveryStorageLimits.MaxRecordBytes)
            throw new IOException("The replacement would exceed the per-record byte limit.");
        // Temporarily retain both manifests in the existing record slot. Never exceed the global cap.
        if (newBytes > RecoveryStorageLimits.MaxStoreBytes - Bytes)
            throw new IOException("Recovery storage lacks space for the temporary manifest; clean records locally before retrying.");
    }

    public void EnsureCanAdd(long recordBytes)
    {
        if (recordBytes is <= 0 or > RecoveryStorageLimits.MaxRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(recordBytes), "Use the actual positive manifest and backup size within the per-record limit.");
        if (Bytes < 0 || RecordSlots < 0) throw new InvalidDataException("Recovery usage cannot be negative.");
        if (RecordSlots >= RecoveryStorageLimits.MaxRecordSlots)
            throw new IOException("Recovery storage has no free record slots; inspect and clean records locally before retrying.");
        if (Bytes > RecoveryStorageLimits.MaxStoreBytes || recordBytes > RecoveryStorageLimits.MaxStoreBytes - Bytes)
            throw new IOException("Recovery storage byte capacity would be exceeded; inspect and clean records locally before retrying.");
    }
}

internal sealed class RecoveryStorageScanBudget
{
    private int _entries;
    public void Visit(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++_entries > RecoveryStorageLimits.MaxScanEntries) throw new IOException("Recovery storage exceeds the inspection entry limit.");
    }
    public static void DirectoryDepth(int depth)
    {
        if (depth > RecoveryStorageLimits.MaxDirectoryDepth) throw new IOException("Recovery storage exceeds the inspection directory-depth limit.");
    }
}

// Read-only preflight. It never creates directories, acquires a write reservation or deletes old data.
internal sealed class RecoveryStorageGuard
{
    private readonly string _projectRoot;
    private readonly string _directory;

    public RecoveryStorageGuard(RuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _projectRoot = Normalized(configuration.Settings.ProjectRoot);
        _directory = Normalized(configuration.Policy.RecoveryDirectory);
        if (Contains(_projectRoot, _directory) || Contains(_directory, _projectRoot))
            throw new ArgumentException("Recovery storage and the workspace must not contain one another.", nameof(configuration));
    }

    public RecoveryStorageUsage Inspect(long recordBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        new RecoveryStorageUsage(0, 0).EnsureCanAdd(recordBytes);
        var usage = Measure(cancellationToken);
        usage.EnsureCanAdd(recordBytes);
        return usage;
    }

    internal void CheckLocation(CancellationToken cancellationToken = default)
    {
        _ = DirectoryPath(_projectRoot, cancellationToken);
        _ = DirectoryPath(_directory, cancellationToken);
    }

    internal RecoveryStorageUsage Measure(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = DirectoryPath(_projectRoot, cancellationToken);
        if (!DirectoryPath(_directory, cancellationToken)) return new(0, 0);

        long bytes = 0;
        var slots = 0;
        var pending = new Stack<(string Path, int Depth)>();
        var budget = new RecoveryStorageScanBudget();
        pending.Push((_directory, 0));
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((Attributes(current.Path) & FileAttributes.Directory) == 0) throw new IOException("A recovery directory changed during inspection.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                budget.Visit(cancellationToken);
                var attributes = Attributes(entry);
                var directory = (attributes & FileAttributes.Directory) != 0;
                var name = Path.GetFileName(entry);
                var marker = current.Depth == 0 && string.Equals(name, RecoveryStorageLimits.LockFileName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (marker && (name != RecoveryStorageLimits.LockFileName || directory))
                    throw new InvalidDataException("The recovery lock marker must be a regular file with its canonical name.");
                if (current.Depth == 0 && !marker && ++slots > RecoveryStorageLimits.MaxRecordSlots)
                    throw new IOException("Recovery storage record-slot capacity is exceeded.");
                if (directory)
                {
                    RecoveryStorageScanBudget.DirectoryDepth(current.Depth + 1);
                    pending.Push((entry, current.Depth + 1));
                }
                else
                {
                    var length = new FileInfo(entry).Length;
                    _ = Attributes(entry);
                    if (marker && length != 0) throw new InvalidDataException("The recovery lock marker must have zero length.");
                    if (length < 0 || length > RecoveryStorageLimits.MaxStoreBytes - bytes)
                        throw new IOException("Recovery storage byte capacity is exceeded.");
                    bytes += length;
                }
            }
        }
        _ = DirectoryPath(_projectRoot, cancellationToken);
        if (!DirectoryPath(_directory, cancellationToken)) throw new IOException("Recovery storage disappeared during inspection.");
        return new(bytes, slots);
    }

    private static bool DirectoryPath(string path, CancellationToken cancellationToken)
    {
        var exists = false;
        var target = true;
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((Attributes(current.FullName) & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("A recovery or workspace directory path is occupied by a file.");
                if (target) exists = true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            target = false;
        }
        return exists;
    }

    private static FileAttributes Attributes(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("Recovery inspection rejects links, junctions and device entries.");
        return attributes;
    }

    private static string Normalized(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != path)
            throw new ArgumentException("Use validated normalized absolute recovery and workspace paths.");
        return path;
    }
    private static bool Contains(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
