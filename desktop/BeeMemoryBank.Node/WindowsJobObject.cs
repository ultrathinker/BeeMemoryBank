using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BeeMemoryBank.Node;

/// <summary>
/// A Windows Job Object wrapper to manage process lifecycles.
/// By setting JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, all processes assigned
/// to this job will be killed by Windows when the job object handle is closed/disposed.
/// </summary>
public sealed class WindowsJobObject : IDisposable
{
    private readonly SafeJobObjectHandle _handle;
    private bool _isDisposed;

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobObjectHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobObjectHandle hJob,
        int JobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobObjectHandle hJob, IntPtr hProcess);

    public WindowsJobObject()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Job objects are only supported on Windows.");
        }

        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create job object.");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, size))
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "Failed to set job object limit information.");
        }
    }

    public void AssignProcess(Process process)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(WindowsJobObject));
        }

        if (process == null) throw new ArgumentNullException(nameof(process));

        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                int error = Marshal.GetLastWin32Error();
                // If the process has already exited by the time we assign it,
                // ignore the failure gracefully. Otherwise, throw an exception.
                if (!process.HasExited)
                {
                    throw new Win32Exception(error, $"Failed to assign process {process.Id} to job object.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Handle cases where the process has already exited or hasn't started.
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _handle.Dispose();
            _isDisposed = true;
        }
    }
}

internal sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobObjectHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        return CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
