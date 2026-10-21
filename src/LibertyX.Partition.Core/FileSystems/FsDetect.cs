using LibertyX.Partition.Core.IO;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

public enum FsKind { Unknown, Empty, Ntfs, Fat12, Fat16, Fat32, ExFat, ReFS, BitLocker, Ext, HfsPlus, Apfs, LinuxSwap, Iso9660,
    Xfs, Btrfs, F2fs, Luks, LvmPv, MdRaid, Zfs, Bcachefs, Squashfs, Jfs, ReiserFs }

public static class FsDetect
{
    public static string Name(FsKind k) => k switch
    {
        FsKind.Ntfs => "NTFS", FsKind.Fat12 => "FAT12", FsKind.Fat16 => "FAT16", FsKind.Fat32 => "FAT32",
        FsKind.ExFat => "exFAT", FsKind.ReFS => "ReFS", FsKind.BitLocker => "BitLocker", FsKind.Ext => "Ext2/3/4",
        FsKind.HfsPlus => "HFS+", FsKind.Apfs => "APFS", FsKind.LinuxSwap => "Linux swap", FsKind.Iso9660 => "ISO 9660",
        FsKind.Xfs => "XFS", FsKind.Btrfs => "Btrfs", FsKind.F2fs => "F2FS", FsKind.Luks => "LUKS", FsKind.LvmPv => "LVM2",
        FsKind.MdRaid => "RAID (md)", FsKind.Zfs => "ZFS", FsKind.Bcachefs => "bcachefs", FsKind.Squashfs => "SquashFS",
        FsKind.Jfs => "JFS", FsKind.ReiserFs => "ReiserFS",
        FsKind.Empty => "Não formatada", _ => "Desconhecido",
    };

    /// <summary>ext2, ext3 ou ext4, pelas marcas de recurso do superbloco (que fica 1024 bytes após o início).</summary>
    public static string ExtVariant(ReadOnlySpan<byte> sb)
    {
        uint compat = U32(sb, 92), incompat = U32(sb, 96);
        if ((incompat & (0x40 | 0x80 | 0x200)) != 0) return "ext4";      // extents, 64 bits, flex_bg
        if ((compat & 0x4) != 0) return "ext3";                           // journal
        return "ext2";
    }

    public sealed record ExtInfo(string Variant, long BlockSize, long Blocks, long FreeBlocks, long BlocksPerGroup, long FirstDataBlock, int Group, string Label, Guid Uuid);

    /// <summary>Lê um superbloco ext (principal ou cópia de reserva). Nulo se não for um superbloco plausível.</summary>
    public static ExtInfo? ParseExt(ReadOnlySpan<byte> sb)
    {
        if (sb.Length < 512 || U16(sb, 56) != 0xEF53) return null;
        uint logBs = U32(sb, 24), rev = U32(sb, 76), inodes = U32(sb, 0), bpg = U32(sb, 32);
        if (logBs > 6 || rev > 1 || inodes == 0 || bpg == 0 || bpg > 65536 * 8) return null;
        uint incompat = U32(sb, 96);
        long blocks = U32(sb, 4) | ((incompat & 0x80) != 0 && sb.Length >= 0x154 ? (long)U32(sb, 0x150) << 32 : 0);
        long free = U32(sb, 12) | ((incompat & 0x80) != 0 && sb.Length >= 0x15C ? (long)U32(sb, 0x158) << 32 : 0);
        if (blocks == 0 || free > blocks) return null;
        string label = System.Text.Encoding.UTF8.GetString(sb.Slice(120, 16)).TrimEnd('\0');
        return new ExtInfo(ExtVariant(sb), 1024L << (int)logBs, blocks, free, bpg, U32(sb, 20), U16(sb, 90), label, new Guid(sb.Slice(104, 16), true));
    }

    private static ReadOnlySpan<byte> LuksMagic => [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE];
    private static ReadOnlySpan<byte> BcachefsMagic => [0xc6, 0x85, 0x73, 0xf6, 0x66, 0xce, 0x90, 0xa9, 0xd9, 0x6a, 0x60, 0xcf, 0x80, 0x3d, 0xf7, 0xef];
    private static ReadOnlySpan<byte> BcachefsMagicOld => [0xc6, 0x85, 0x73, 0xf6, 0x4e, 0x1a, 0x45, 0xca, 0x82, 0x65, 0xf5, 0x7f, 0x48, 0xba, 0x6d, 0x81];

    /// <summary>Sistemas nativos do Linux (o kernel monta; o LibertyX formata e repara pelas ferramentas do sistema).</summary>
    public static bool IsLinuxNative(FsKind k) => k is FsKind.Ext or FsKind.Xfs or FsKind.Btrfs or FsKind.F2fs or FsKind.ReiserFs or FsKind.Jfs or FsKind.Bcachefs;
    /// <summary>Não é sistema de arquivos montável, é um recipiente (o conteúdo está dentro, depois de aberto).</summary>
    public static bool IsContainer(FsKind k) => k is FsKind.Luks or FsKind.LvmPv or FsKind.MdRaid or FsKind.Zfs or FsKind.BitLocker;

    public static bool IsFat(FsKind k) => k is FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32;
    public static bool CanRead(FsKind k) => k is FsKind.Ntfs or FsKind.ExFat or FsKind.Ext or FsKind.HfsPlus or FsKind.Apfs || IsFat(k);

    /// <summary>Reconhece o sistema de arquivos pelo setor de boot (e pelo superbloco, no caso dos Unix).</summary>
    public static FsKind Detect(IBlockDevice vol)
    {
        if (vol.Length < 4096) return FsKind.Unknown;
        var b = vol.ReadBytes(0, (int)Math.Min(vol.Length, 8192));
        var k = Detect(b);
        if (k is not (FsKind.Unknown or FsKind.Empty)) return k;
        // os que guardam a assinatura mais adiante: Btrfs e ReiserFS (64 KiB), JFS (32 KiB), ZFS (128 KiB)
        try
        {
            if (vol.Length >= 0x10000 + 4096)
            {
                var b2 = vol.ReadBytes(0x10000, 4096);
                if (Match(b2, 0x40, "_BHRfS_M"u8)) return FsKind.Btrfs;
                if (Match(b2, 52, "ReIsEr"u8) || Match(b2, 52, "ReIsEr2Fs"u8)) return FsKind.ReiserFs;
            }
            if (vol.Length >= 0x8000 + 4096 && Match(vol.ReadBytes(0x8000, 512), 0, "JFS1"u8)) return FsKind.Jfs;
            if (vol.Length >= 0x40000)
            {
                // rótulo ZFS: o vetor de uberblocks começa em 128 KiB
                var ub = vol.ReadBytes(0x20000, 1024);
                if (U64(ub, 0) == 0x00bab10cUL || U64BE(ub, 0) == 0x00bab10cUL) return FsKind.Zfs;
            }
        }
        catch { }
        return k;
    }

    /// <summary>Rótulo, UUID, tamanho total e livre dos sistemas Linux sem leitor de árvore, direto do superbloco.</summary>
    public sealed record SuperInfo(string? Label, string? Uuid, long? Total, long? Free, string? Variant);

    public static SuperInfo? ReadSuper(IBlockDevice vol, FsKind k)
    {
        try
        {
            switch (k)
            {
                case FsKind.Xfs:
                    {
                        var b = vol.ReadBytes(0, 512);
                        long bs = U32BE(b, 4), blocks = (long)U64BE(b, 8), free = (long)U64BE(b, 144);
                        int ver = U16BE(b, 100) & 0xF;
                        return new SuperInfo(Ascii(b, 108, 12), new Guid(b.AsSpan(32, 16), true).ToString(), blocks * bs, free * bs, ver >= 5 ? "XFS v5" : "XFS v4");
                    }
                case FsKind.Btrfs:
                    {
                        var b = vol.ReadBytes(0x10000, 4096);
                        long total = (long)U64(b, 0x70), used = (long)U64(b, 0x78);
                        string label = System.Text.Encoding.UTF8.GetString(b.AsSpan(0x12B, 256)).TrimEnd('\0');
                        return new SuperInfo(label, UuidBe(b.AsSpan(0x20, 16)), total, Math.Max(0, total - used), U64(b, 0x88) > 1 ? $"Btrfs ({U64(b, 0x88)} dispositivos)" : "Btrfs");
                    }
                case FsKind.F2fs:
                    {
                        var b = vol.ReadBytes(1024, 3072);
                        int logBs = (int)U32(b, 16);
                        long blocks = (long)U64(b, 36);
                        string label = Utf16(b, 0x7C, 512);
                        return new SuperInfo(label.Length > 0 ? label : null, UuidBe(b.AsSpan(0x6C, 16)), logBs is >= 9 and <= 16 ? blocks << logBs : null, null, "F2FS");
                    }
                case FsKind.Luks:
                    {
                        var b = vol.ReadBytes(0, 512);
                        int ver = U16BE(b, 6);
                        string uuid = Ascii(b, 168, 40);
                        string? label = ver == 2 ? Ascii(b, 24, 48) : null;
                        return new SuperInfo(string.IsNullOrEmpty(label) ? null : label, uuid, null, null, ver == 2 ? "LUKS2" : "LUKS1");
                    }
                case FsKind.LinuxSwap:
                    {
                        var b = vol.ReadBytes(0, 4096);
                        long pages = U32(b, 1024 + 4);
                        return new SuperInfo(Ascii(b, 1024 + 28, 16) is { Length: > 0 } l ? l : null, UuidBe(b.AsSpan(1024 + 12, 16)), (pages + 1) * 4096, null, "swap");
                    }
                case FsKind.LvmPv:
                    {
                        var b = vol.ReadBytes(0, 2048);
                        for (int s = 0; s < 4; s++)
                            if (Match(b, s * 512, "LABELONE"u8))
                            {
                                int o = s * 512 + (int)U32(b, s * 512 + 20);
                                return new SuperInfo(null, o + 32 <= b.Length ? Ascii(b, o, 32) : null, null, null, "LVM2 PV");
                            }
                        return null;
                    }
            }
        }
        catch { }
        return null;
    }

    private static string UuidBe(ReadOnlySpan<byte> u) => new Guid(u, true).ToString();

    public static FsKind Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length < 512) return FsKind.Unknown;
        if (Match(b, 0, LuksMagic)) return FsKind.Luks;
        if (Match(b, 0, "XFSB"u8)) return FsKind.Xfs;
        if (Match(b, 0, "hsqs"u8)) return FsKind.Squashfs;
        for (int s = 0; s < 4 && s * 512 + 32 <= b.Length; s++)
            if (Match(b, s * 512, "LABELONE"u8) && Match(b, s * 512 + 24, "LVM2 001"u8)) return FsKind.LvmPv;
        if (Match(b, 3, "NTFS    "u8)) return FsKind.Ntfs;
        if (Match(b, 3, "EXFAT   "u8)) return FsKind.ExFat;
        if (Match(b, 3, "-FVE-FS-"u8)) return FsKind.BitLocker;
        if (Match(b, 3, "ReFS"u8)) return FsKind.ReFS;
        if (b[510] == 0x55 && b[511] == 0xAA)
        {
            var fat = FatKindFromBpb(b);
            if (fat != FsKind.Unknown) return fat;
        }
        if (b.Length >= 2048)
        {
            if (U32(b, 1024) == 0xF2F52010) return FsKind.F2fs;
            if (U16(b, 1024 + 56) == 0xEF53) return FsKind.Ext;
            if (Match(b, 1024, "H+"u8) || Match(b, 1024, "HX"u8)) return FsKind.HfsPlus;
            if (Match(b, 32, "NXSB"u8)) return FsKind.Apfs;
        }
        if (b.Length >= 4096 && (Match(b, 4086, "SWAPSPACE2"u8) || Match(b, 4086, "SWAP-SPACE"u8))) return FsKind.LinuxSwap;
        // RAID md 1.1 (no início) e 1.2 (a 4 KiB); bcachefs a 4 KiB
        if (U32(b, 0) == 0xa92b4efc || (b.Length >= 4100 && U32(b, 4096) == 0xa92b4efc)) return FsKind.MdRaid;
        if (b.Length >= 4096 + 40 && (Match(b, 4096 + 24, BcachefsMagic) || Match(b, 4096 + 24, BcachefsMagicOld))) return FsKind.Bcachefs;
        if (IsZero(b)) return FsKind.Empty;
        return FsKind.Unknown;
    }

    /// <summary>Tipo de FAT pela contagem de clusters (a regra oficial da Microsoft), e não pelo texto "FAT32", que é só enfeite.</summary>
    public static FsKind FatKindFromBpb(ReadOnlySpan<byte> b)
    {
        int bps = U16(b, 11), spc = b[13], rsv = U16(b, 14), nfats = b[16], rootEnt = U16(b, 17);
        if (bps is not (512 or 1024 or 2048 or 4096)) return FsKind.Unknown;
        if (spc == 0 || (spc & (spc - 1)) != 0 || rsv == 0 || nfats is 0 or > 4) return FsKind.Unknown;
        if (b[0] != 0xEB && b[0] != 0xE9) return FsKind.Unknown;
        long tot = U16(b, 19) != 0 ? U16(b, 19) : U32(b, 32);
        long fatSz = U16(b, 22) != 0 ? U16(b, 22) : U32(b, 36);
        if (tot == 0 || fatSz == 0) return FsKind.Unknown;
        long rootSecs = (rootEnt * 32L + bps - 1) / bps;
        long data = tot - (rsv + nfats * fatSz + rootSecs);
        if (data <= 0) return FsKind.Unknown;
        long clusters = data / spc;
        return clusters < 4085 ? FsKind.Fat12 : clusters < 65525 ? FsKind.Fat16 : FsKind.Fat32;
    }
}
