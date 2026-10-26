using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniMcp.Services.Processes;

// Linux runs in a fresh session/process group; Windows puts Bash and its children in a job.
// This cleans up ordinary descendants, and is not a security sandbox for hostile commands.
internal sealed partial class ProcessLifetime : IDisposable
{
    private readonly Process _process;
    private readonly int _pid;
    private readonly bool _ownsResources;
    private SafeFileHandle? _job;
    internal Process Process => _process;
    internal StreamReader StandardOutput { get; private init; } = null!;
    internal StreamReader StandardError { get; private init; } = null!;

    public ProcessLifetime(Process process)
    {
        _process = process;
        _pid = process.Id;
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJob();
        try
        {
            if (!AssignProcessToJobObject(_job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { _job.Dispose(); _job = null; throw; }
    }

    private ProcessLifetime(Process process, SafeFileHandle? job, StreamReader stdout, StreamReader stderr)
    {
        _process = process;
        _pid = process.Id;
        _job = job;
        _ownsResources = true;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    internal static ProcessLifetime Start(ProcessStartInfo start)
    {
        if (OperatingSystem.IsWindows()) return StartWindows(start);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Managed processes require Windows or Linux.");
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("The managed process did not start.");
            process.StandardInput.Close();
            return new(process, null, process.StandardOutput, process.StandardError);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
    }

    // The caller retains this object and the actual task until termination is verified. Cancelling
    // its wait must not release the job, pipes or process while cleanup is still running.
    internal async Task TerminateAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_job is null || !TerminateJobObject(_job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else
        {
            if (KillGroup(-_pid, 9) != 0 && Marshal.GetLastPInvokeError() != 3) // ESRCH
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            // Also covers cancellation before setsid has created the new group.
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Win32Exception) when (_process.HasExited) { }
            catch (InvalidOperationException) when (_process.HasExited) { }
        }
        await _process.WaitForExitAsync().ConfigureAwait(false);
        while (HasLiveMembers()) await Task.Delay(20).ConfigureAwait(false);
    }

    private bool HasLiveMembers()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!QueryInformationJobObject(_job!, 1, out var accounting,
                (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return accounting.ActiveProcesses != 0;
        }
        if (KillGroup(-_pid, 0) != 0)
        {
            if (Marshal.GetLastPInvokeError() == 3) return false;
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        // Zombies cannot execute or retain pipe handles. Their final reaping belongs to their
        // parent/init, which is outside this Runtime (notably in containers without a reaping init).
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out _)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            if (fields.Length < 4) throw new IOException("Could not inspect a process group member.");
            if (int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture) == _pid &&
                fields[0] is not ("Z" or "X")) return true;
        }
        return false;
    }

    private static SafeFileHandle CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        if (OperatingSystem.IsLinux()) KillGroup(-_pid, 9);
        _job?.Dispose();
        _job = null;
        if (_ownsResources)
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
            _process.Dispose();
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillGroup(int pid, int signal);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass,
        out BasicAccountingInformation information, uint length, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}
