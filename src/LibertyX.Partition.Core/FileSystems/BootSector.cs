using LibertyX.Partition.Core.IO;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.FileSystems;

/// <summary>Edições pontuais no setor de boot (e nas cópias): setores ocultos, número de série.</summary>
public static class BootSector
{
    /// <summary>Setores onde cada sistema guarda cópia do boot (relativo ao volume).</summary>
    private static List<long> Copies(IBlockDevice vol, FsKind fs)
    {
        int ss = vol.SectorSize;
        long last = vol.Length / ss - 1;
        return fs switch
        {
            FsKind.Ntfs => [0, last],
            FsKind.Fat32 => [0, 6],
            FsKind.Fat12 or FsKind.Fat16 => [0],
            FsKind.ExFat => [0, 12],
            _ => [],
        };
    }

    /// <summary>Atualiza o campo "setores ocultos" (= início da partição) depois de mover o volume.</summary>
    public static void SetHiddenSectors(IBlockDevice vol, FsKind fs, long startLba)
    {
        foreach (var lba in Copies(vol, fs))
        {
            int ss = vol.SectorSize;
            if (fs == FsKind.ExFat)
            {
                RewriteExFatRegion(vol, lba, s => W64(s, 64, (ulong)startLba));
                continue;
            }
            var b = vol.ReadBytes(lba * ss, ss);
            if (FsDetect.Detect(b) != fs) continue;
            if (startLba <= uint.MaxValue) W32(b, 28, (uint)startLba);
            vol.Write(lba * ss, b);
        }
        vol.Flush();
    }

    public static ulong ReadSerial(IBlockDevice vol, FsKind fs)
    {
        var b = vol.ReadBytes(0, 512);
        return fs switch
        {
            FsKind.Ntfs => U64(b, 72),
            FsKind.Fat32 => U32(b, 67),
            FsKind.Fat12 or FsKind.Fat16 => U32(b, 39),
            FsKind.ExFat => U32(b, 100),
            _ => 0,
        };
    }

    public static void WriteSerial(IBlockDevice vol, FsKind fs, ulong serial)
    {
        int ss = vol.SectorSize;
        foreach (var lba in Copies(vol, fs))
        {
            if (fs == FsKind.ExFat) { RewriteExFatRegion(vol, lba, s => W32(s, 100, (uint)serial)); continue; }
            var b = vol.ReadBytes(lba * ss, ss);
            if (FsDetect.Detect(b) != fs) continue;
            switch (fs)
            {
                case FsKind.Ntfs: W64(b, 72, serial); break;
                case FsKind.Fat32: W32(b, 67, (uint)serial); break;
                default: W32(b, 39, (uint)serial); break;
            }
            vol.Write(lba * ss, b);
        }
        vol.Flush();
    }

    /// <summary>exFAT protege a região de boot com uma soma (setor 11): mudar um campo exige recalcular.</summary>
    private static void RewriteExFatRegion(IBlockDevice vol, long regionLba, Action<Span<byte>> edit)
    {
        int ss = vol.SectorSize;
        var region = vol.ReadBytes(regionLba * ss, 12 * ss);
        if (region.Length < 12 * ss || !Match(region, 3, "EXFAT   "u8)) return;
        edit(region.AsSpan(0, ss));
        uint sum = 0;
        for (int i = 0; i < 11 * ss; i++)
        {
            if (i is 106 or 107 or 112) continue;
            sum = ((sum & 1) != 0 ? 0x80000000 : 0) + (sum >> 1) + region[i];
        }
        for (int i = 11 * ss; i < 12 * ss; i += 4) W32(region, i, sum);
        vol.Write(regionLba * ss, region);
    }

    public static string FormatSerial(ulong s, FsKind fs) =>
        fs == FsKind.Ntfs ? s.ToString("X16") : $"{(s >> 16) & 0xFFFF:X4}-{s & 0xFFFF:X4}";

    public static bool TryParseSerial(string text, FsKind fs, out ulong value)
    {
        string t = text.Replace("-", "").Replace(" ", "").Trim();
        value = 0;
        int max = fs == FsKind.Ntfs ? 16 : 8;
        return t.Length is > 0 && t.Length <= max && ulong.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}

/// <summary>Formata FAT32 em qualquer tamanho — o Windows se recusa acima de 32 GB, mas pendrive de boot UEFI precisa.</summary>
public static class Fat32Formatter
{
    /// <summary>Tabela da Microsoft para FAT32: o cluster cresce com o volume para manter ≥ 65.525 clusters.</summary>
    public static int AutoCluster(long bytes) => bytes switch
    {
        <= 64L << 20 => 512,
        <= 128L << 20 => 1024,
        <= 256L << 20 => 2048,
        <= 8L << 30 => 4096,
        <= 16L << 30 => 8192,
        <= 32L << 30 => 16384,
        _ => 32768,
    };

    public static void Format(IBlockDevice vol, string label, int clusterBytes, long hiddenSectors, IProgress<double>? progress = null)
    {
        int bps = vol.SectorSize;
        if (bps is not (512 or 4096)) throw new InvalidOperationException(L.T("Tamanho de setor não suportado para FAT32."));
        long total = vol.Length / bps;
        if (clusterBytes <= 0) clusterBytes = Math.Max(bps, AutoCluster(vol.Length));
        int spc = Math.Max(1, clusterBytes / bps);
        const int numFats = 2;
        long reserved = 32;
        long tmp2 = (256L * spc + numFats) / 2;
        long fatSz = (total - reserved + tmp2 - 1) / tmp2;
        long align = Math.Max(1024 * 1024 / bps, spc);
        long dataStart = reserved + numFats * fatSz;
        if (dataStart % align != 0) { reserved += align - dataStart % align; dataStart = reserved + numFats * fatSz; }
        long clusters = (total - dataStart) / spc;
        if (clusters < 65525) throw new InvalidOperationException(L.T("Volume pequeno demais para FAT32 com esse cluster — use FAT16 ou exFAT."));
        if (clusters > 0x0FFFFFF5) throw new InvalidOperationException(L.T("Volume grande demais para FAT32 com esse cluster — aumente o cluster."));
        uint serial = (uint)Random.Shared.NextInt64(1, uint.MaxValue);

        var boot = new byte[bps];
        boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;
        "MSWIN4.1"u8.CopyTo(boot.AsSpan(3));
        W16(boot, 11, (ushort)bps);
        boot[13] = (byte)spc;
        W16(boot, 14, (ushort)reserved);
        boot[16] = numFats;
        boot[21] = 0xF8;
        W16(boot, 24, 63);
        W16(boot, 26, 255);
        W32(boot, 28, (uint)Math.Min(hiddenSectors, uint.MaxValue));
        W32(boot, 32, (uint)total);
        W32(boot, 36, (uint)fatSz);
        W32(boot, 44, 2);
        W16(boot, 48, 1);
        W16(boot, 50, 6);
        boot[64] = 0x80;
        boot[66] = 0x29;
        W32(boot, 67, serial);
        var lbl = System.Text.Encoding.ASCII.GetBytes((label.Length == 0 ? "NO NAME" : label.ToUpperInvariant()).PadRight(11)[..11]);
        lbl.CopyTo(boot, 71);
        "FAT32   "u8.CopyTo(boot.AsSpan(82));
        boot[510] = 0x55; boot[511] = 0xAA;
        if (bps > 512) { boot[bps - 2] = 0x55; boot[bps - 1] = 0xAA; }

        var fsinfo = new byte[bps];
        W32(fsinfo, 0, 0x41615252);
        W32(fsinfo, 484, 0x61417272);
        W32(fsinfo, 488, (uint)(clusters - 1));
        W32(fsinfo, 492, 3);
        W32(fsinfo, 508, 0xAA550000);
        var third = new byte[bps];
        third[510] = 0x55; third[511] = 0xAA;

        // zera reservados + FATs + diretório raiz
        long zeroEnd = (dataStart + spc) * bps;
        var zero = new byte[1 << 20];
        for (long p = 0; p < zeroEnd; p += zero.Length)
        {
            vol.Write(p, zero.AsSpan(0, (int)Math.Min(zero.Length, zeroEnd - p)));
            progress?.Report((double)p / zeroEnd);
        }
        foreach (long baseLba in new long[] { 0, 6 })
        {
            vol.Write((baseLba + 0) * bps, boot);
            vol.Write((baseLba + 1) * bps, fsinfo);
            vol.Write((baseLba + 2) * bps, third);
        }
        var fat0 = new byte[bps];
        W32(fat0, 0, 0x0FFFFFF8);
        W32(fat0, 4, 0x0FFFFFFF);
        W32(fat0, 8, 0x0FFFFFFF);
        for (int f = 0; f < numFats; f++) vol.Write((reserved + f * fatSz) * bps, fat0);
        if (label.Length > 0)
        {
            var root = new byte[bps];
            lbl.CopyTo(root, 0);
            root[11] = 0x08;
            vol.Write(dataStart * bps, root);
        }
        vol.Flush();
        progress?.Report(1);
    }
}
