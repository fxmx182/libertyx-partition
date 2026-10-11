using LibertyX.Partition.Core.IO;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

public enum FsKind { Unknown, Empty, Ntfs, Fat12, Fat16, Fat32, ExFat, ReFS, BitLocker, Ext, HfsPlus, Apfs, LinuxSwap, Iso9660 }

public static class FsDetect
{
    public static string Name(FsKind k) => k switch
    {
        FsKind.Ntfs => "NTFS", FsKind.Fat12 => "FAT12", FsKind.Fat16 => "FAT16", FsKind.Fat32 => "FAT32",
        FsKind.ExFat => "exFAT", FsKind.ReFS => "ReFS", FsKind.BitLocker => "BitLocker", FsKind.Ext => "Ext2/3/4",
        FsKind.HfsPlus => "HFS+", FsKind.Apfs => "APFS", FsKind.LinuxSwap => "Linux swap", FsKind.Iso9660 => "ISO 9660",
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
        return new ExtInfo(ExtVariant(sb), 1024L << (int)logBs, blocks, free, bpg, U32(sb, 20), U16(sb, 90), label, new Guid(sb.Slice(104, 16)));
    }

    public static bool IsFat(FsKind k) => k is FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32;
    public static bool CanRead(FsKind k) => k is FsKind.Ntfs or FsKind.ExFat || IsFat(k);

    /// <summary>Reconhece o sistema de arquivos pelo setor de boot (e pelo superbloco, no caso dos Unix).</summary>
    public static FsKind Detect(IBlockDevice vol)
    {
        if (vol.Length < 4096) return FsKind.Unknown;
        var b = vol.ReadBytes(0, 4096);
        return Detect(b);
    }

    public static FsKind Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length < 512) return FsKind.Unknown;
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
            if (U16(b, 1024 + 56) == 0xEF53) return FsKind.Ext;
            if (Match(b, 1024, "H+"u8) || Match(b, 1024, "HX"u8)) return FsKind.HfsPlus;
            if (Match(b, 32, "NXSB"u8)) return FsKind.Apfs;
        }
        if (b.Length >= 4096 && (Match(b, 4086, "SWAPSPACE2"u8) || Match(b, 4086, "SWAP-SPACE"u8))) return FsKind.LinuxSwap;
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
