using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>Chamadas diretas à libc: E/S em dispositivos de bloco, ioctls do kernel, desmontar.</summary>
[SupportedOSPlatform("linux")]
internal static unsafe class LinuxNative
{
    private const string Libc = "libc";

    public const int O_RDONLY = 0, O_RDWR = 2, O_CLOEXEC = 0x80000, O_DSYNC = 0x1000, O_NONBLOCK = 0x800;
    /// <summary>O_DIRECT muda de valor conforme a arquitetura.</summary>
    public static int O_DIRECT => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x10000 : 0x4000;

    public const int EINTR = 4, EIO = 5, EACCES = 13, EBUSY = 16, EINVAL = 22, ENOTTY = 25, EROFS = 30, ENOENT = 2, EPERM = 1, ENODEV = 19, ENXIO = 6, EMEDIUMTYPE = 124, ENOMEDIUM = 123;

    // ioctls de bloco (linux/fs.h)
    public const ulong BLKRRPART = 0x125F, BLKGETSIZE64 = 0x80081272, BLKSSZGET = 0x1268, BLKPBSZGET = 0x127B,
        BLKFLSBUF = 0x1261, BLKPG = 0x1269, BLKDISCARD = 0x1277, BLKSECDISCARD = 0x127D, BLKZEROOUT = 0x127F, BLKROGET = 0x125E;
    public const ulong SG_IO = 0x2285;
    public const ulong NVME_IOCTL_ADMIN_CMD = 0xC0484E41;
    public const ulong FIFREEZE = 0xC0045877, FITHAW = 0xC0045878;

    public const int BLKPG_ADD_PARTITION = 1, BLKPG_DEL_PARTITION = 2, BLKPG_RESIZE_PARTITION = 3;
    public const int MNT_DETACH = 2;

    [DllImport(Libc, SetLastError = true, EntryPoint = "open")] public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode = 0);
    [DllImport(Libc, SetLastError = true, EntryPoint = "close")] public static extern int Close(int fd);
    [DllImport(Libc, SetLastError = true, EntryPoint = "pread64")] public static extern nint PRead(int fd, void* buf, nuint count, long offset);
    [DllImport(Libc, SetLastError = true, EntryPoint = "pwrite64")] public static extern nint PWrite(int fd, void* buf, nuint count, long offset);
    [DllImport(Libc, SetLastError = true, EntryPoint = "fsync")] public static extern int FSync(int fd);
    [DllImport(Libc, SetLastError = true, EntryPoint = "ioctl")] public static extern int Ioctl(int fd, ulong request, void* arg);
    [DllImport(Libc, SetLastError = true, EntryPoint = "ioctl")] public static extern int IoctlInt(int fd, ulong request, nint arg);
    [DllImport(Libc, SetLastError = true, EntryPoint = "umount2")] public static extern int Umount2([MarshalAs(UnmanagedType.LPUTF8Str)] string target, int flags);
    [DllImport(Libc, SetLastError = true, EntryPoint = "swapoff")] public static extern int SwapOff([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Libc, SetLastError = true, EntryPoint = "chown")] public static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int uid, int gid);
    [DllImport(Libc, EntryPoint = "geteuid")] public static extern uint GetEuid();
    [DllImport(Libc, EntryPoint = "strerror")] private static extern nint StrError(int errnum);

    public static int Errno => Marshal.GetLastPInvokeError();
    public static string ErrText(int errno) { try { return Marshal.PtrToStringUTF8(StrError(errno)) ?? $"errno {errno}"; } catch { return $"errno {errno}"; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlkpgPartition
    {
        public long Start, Length;   // em bytes
        public int Pno;
        public fixed byte DevName[64];
        public fixed byte VolName[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlkpgIoctlArg
    {
        public int Op, Flags, DataLen;
        public void* Data;
    }

    public static bool Blkpg(int fd, int op, int pno, long start, long length, out int errno)
    {
        var part = new BlkpgPartition { Start = start, Length = length, Pno = pno };
        var arg = new BlkpgIoctlArg { Op = op, DataLen = sizeof(BlkpgPartition), Data = &part };
        int r = Ioctl(fd, BLKPG, &arg);
        errno = r < 0 ? Errno : 0;
        return r == 0;
    }

    /// <summary>sg_io_hdr_t (scsi/sg.h), para comandos ATA PASS-THROUGH pelo SG_IO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SgIoHdr
    {
        public int InterfaceId;      // 'S'
        public int DxferDirection;   // SG_DXFER_FROM_DEV = -3
        public byte CmdLen;
        public byte MxSbLen;
        public ushort IovecCount;
        public uint DxferLen;
        public void* Dxferp;
        public byte* Cmdp;
        public byte* Sbp;
        public uint Timeout;         // ms
        public uint Flags;
        public int PackId;
        public void* UsrPtr;
        public byte Status, MaskedStatus, MsgStatus, SbLenWr;
        public ushort HostStatus, DriverStatus;
        public int Resid;
        public uint Duration, Info;
    }

    /// <summary>nvme_admin_cmd (linux/nvme_ioctl.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NvmeAdminCmd
    {
        public byte Opcode, Flags;
        public ushort Rsvd1;
        public uint Nsid, Cdw2, Cdw3;
        public ulong Metadata, Addr;
        public uint MetadataLen, DataLen;
        public uint Cdw10, Cdw11, Cdw12, Cdw13, Cdw14, Cdw15;
        public uint TimeoutMs, Result;
    }
}
