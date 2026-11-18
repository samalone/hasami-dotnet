using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hasami.Cli;

/// <summary>Moves files and directories to the Windows Recycle Bin.</summary>
[SupportedOSPlatform("windows")]
internal static class RecycleBin
{
    /// <summary>
    /// Returns null if items under <paramref name="directory"/> can be recycled, otherwise
    /// a reason they cannot. Windows only provides a Recycle Bin on fixed local drives;
    /// on network shares and most removable media "recycling" silently deletes permanently,
    /// so we refuse rather than destroy data the user expected to be recoverable.
    /// </summary>
    public static string? UnavailableReason(string directory)
    {
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return $"'{full}' is on a network share, which has no Recycle Bin";
        var type = new DriveInfo(root).DriveType;
        return type == DriveType.Fixed ? null : $"drive {root} ({type}) has no Recycle Bin";
    }

    /// <summary>Moves a file or directory (recursively) to the Recycle Bin.</summary>
    /// <exception cref="IOException">The shell reported a failure or the operation was aborted.</exception>
    public static void Recycle(string path)
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Recycle Bin support requires a 64-bit process");
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            // pFrom is a list of NUL-terminated paths ending with an extra NUL; the
            // marshaller supplies the final terminator.
            pFrom = Path.GetFullPath(path) + "\0",
            // FOF_WANTNUKEWARNING: if Windows can't recycle an item (Recycle Bin disabled
            // for the volume, or item larger than its capacity), ask before deleting it
            // permanently instead of doing so silently.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        int result = SHFileOperationW(ref op);
        if (result != 0)
            throw new IOException($"the shell reported error 0x{result:X}");
        if (op.fAnyOperationsAborted)
            throw new IOException("the operation was cancelled");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    // Natural packing is correct for 64-bit Windows (x64/arm64); shellapi.h only
    // packs this struct to 1 byte on 32-bit.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
