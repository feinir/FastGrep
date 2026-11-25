using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FastGrep.Core;

internal readonly record struct FileIdentity(long Device, long Index)
{
    public static FileIdentity? TryRead(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? MacStatInode64(path, out var status)
                : MacStat(path, out status);
            return result == 0 ? new(status.Device, status.Inode) : null;
        }
        if (OperatingSystem.IsLinux())
        {
            const int currentDirectory = -100;
            const uint inodeMask = 0x100;
            return LinuxStat(currentDirectory, path, 0, inodeMask, out var status) == 0 &&
                   (status.Mask & inodeMask) != 0
                ? new((long)status.DeviceMajor << 32 | status.DeviceMinor, status.Inode)
                : null;
        }

        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out var info)
                ? new(info.VolumeSerial, (long)info.IndexHigh << 32 | info.IndexLow)
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // Darwin sys/stat.h uses the same 64-bit inode layout on arm64 and x64.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacFileStatus
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(8)] public long Inode;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "stat")]
    private static extern int MacStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacFileStatus status);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "stat$INODE64")]
    private static extern int MacStatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out MacFileStatus status);

    // Linux's statx ABI is identical across the supported architectures.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public long Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "statx")]
    private static extern int LinuxStat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxFileStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation information);
}
