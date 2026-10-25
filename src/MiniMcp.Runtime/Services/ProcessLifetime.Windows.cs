using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MiniMcp.Services;

internal sealed partial class ProcessLifetime
{
    // JOB_LIST assigns the child atomically at creation. SUSPENDED lets us acquire the managed
    // Process handle before even an immediately exiting program runs. Only its three std handles
    // are inherited; the kill-on-close job itself remains exclusively owned by this process.
    private static ProcessLifetime StartWindows(ProcessStartInfo start)
    {
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        AnonymousPipeServerStream? output = null, error = null;
        SafeFileHandle? job = null;
        IntPtr attributes = IntPtr.Zero, jobList = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero;
        var initialized = false;
        var created = new ProcessInformation();
        Process? process = null;
        try
        {
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            job = CreateJob();
            nuint length = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref length);
            attributes = Marshal.AllocHGlobal(checked((int)length));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var inherited = new[] { input.ClientSafePipeHandle.DangerousGetHandle(),
                output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
            handles = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length);
            for (var index = 0; index < inherited.Length; index++) Marshal.WriteIntPtr(handles, index * IntPtr.Size, inherited[index]);
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handles,
                (nuint)(IntPtr.Size * inherited.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInformationEx
            {
                Information = new StartupInformation
                {
                    Size = Marshal.SizeOf<StartupInformationEx>(),
                    Flags = 0x100 | (!start.CreateNoWindow ? 0x1u : 0u), // Standard handles; hidden new console for controlled checks.
                    ShowWindow = 0,
                    StandardInput = inherited[0], StandardOutput = inherited[1], StandardError = inherited[2]
                },
                Attributes = attributes
            };
            environment = Marshal.StringToHGlobalUni(string.Join('\0', start.Environment
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0");
            var arguments = new StringBuilder(QuoteArgument(start.FileName));
            foreach (var argument in start.ArgumentList) arguments.Append(' ').Append(QuoteArgument(argument));
            if (arguments.Length >= 32767) throw new IOException("The managed process command line is too long.");
            if (!CreateProcess(start.FileName, arguments, IntPtr.Zero, IntPtr.Zero, true,
                0x80000 | 0x4 | (start.CreateNoWindow ? 0x8000000u : 0x10u) | 0x400,
                environment, start.WorkingDirectory, ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            process = Process.GetProcessById(created.ProcessId);
            _ = process.Handle; // Keep its process identity alive independently of the PID.
            if (ResumeThread(created.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            return new(process, job, new StreamReader(output, new UTF8Encoding(false)),
                new StreamReader(error, new UTF8Encoding(false)));
        }
        catch
        {
            // The job also covers a suspended process if any post-creation initialization fails.
            job?.Dispose();
            process?.Dispose();
            output?.Dispose();
            error?.Dispose();
            throw;
        }
        finally
        {
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    // Windows argv quoting, not shell escaping. Token paths remain one exact argument.
    private static string QuoteArgument(string argument)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var value in argument)
        {
            if (value == '\\') { slashes++; continue; }
            result.Append('\\', value == '"' ? slashes * 2 + 1 : slashes);
            result.Append(value);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx
    {
        public StartupInformation Information;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder arguments, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInformationEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute,
        IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
