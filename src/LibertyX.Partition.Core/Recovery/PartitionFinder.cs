using System.Text;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Partitioning;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.Recovery;

public sealed class FoundPartition
{
    public long StartLba { get; init; }
    public long SectorCount { get; set; }
    public FsKind FileSystem { get; init; }
    public string? Label { get; set; }
    /// <summary>De onde veio: setor de boot principal ou a cópia de reserva.</summary>
    public string Evidence { get; set; } = "";
    /// <summary>A estrutura interna confere (MFT legível, FAT com byte de mídia...).</summary>
    public bool Verified { get; set; }
    public bool InCurrentTable { get; set; }
    public bool LooksLikeEsp { get; set; }
    public bool LooksLikeRecovery { get; set; }
    public bool LengthGuessed { get; set; }
    /// <summary>Detalhe do sistema de arquivos (ext2/ext3/ext4).</summary>
    public string? Variant { get; set; }
    /// <summary>A cópia de reserva do setor de boot (no fim ou nos setores 6/12) confere com este.</summary>
    public bool BackupConfirmed { get; set; }
    /// <summary>Sobrepõe outra candidata mais confiável — costuma ser o resto de um volume que foi movido.</summary>
    public bool Conflicting { get; set; }
    public int Score => (Verified ? 2 : 0) + (BackupConfirmed ? 4 : 0) + (InCurrentTable ? 8 : 0);
    /// <summary>Quão recente é o volume (ext: última gravação do superbloco; Btrfs: geração). Desempata sobreposições: o resto de um volume movido é sempre mais velho.</summary>
    public long Freshness { get; set; }
    public long EndLba => StartLba + SectorCount - 1;
}

/// <summary>
/// Procura partições perdidas pelos setores de boot espalhados no disco — a tabela pode ter
/// sumido (disco "não inicializado", partições excluídas), mas os setores de boot de cada
/// volume continuam lá, e cada um diz o próprio tamanho.
/// </summary>
public static class PartitionFinder
{
    public static List<FoundPartition> Scan(IBlockDevice disk, bool deep, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        int ss = disk.SectorSize;
        long sectors = disk.Length / ss;
        var found = new Dictionary<long, FoundPartition>();
        PartitionTable? table = null;
        try { table = PartitionCodec.Read(disk); } catch { }

        void Consider(long lba, ReadOnlySpan<byte> sec, ReadOnlySpan<byte> plus1024)
        {
            foreach (var f in Analyze(disk, lba, sec, plus1024))
            {
                if (f.StartLba < 0 || f.SectorCount <= 0 || f.StartLba >= sectors) continue;
                if (found.TryGetValue(f.StartLba, out var prev))
                {
                    if (!prev.Verified && f.Verified) found[f.StartLba] = f;
                    else if (prev.Evidence != f.Evidence && !prev.Evidence.Contains('+')) prev.Evidence += " + " + f.Evidence;
                    continue;
                }
                found[f.StartLba] = f;
            }
        }

        if (!deep)
        {
            var candidates = new SortedSet<long> { 63, 2048, 128, 256, 4096 };
            long align = Math.Max(1, (1 << 20) / ss);
            for (long l = align; l < Math.Min(sectors, 256L * 1024 * 1024 / ss); l += align) candidates.Add(l);
            if (table != null) foreach (var e in table.Entries) { candidates.Add(e.StartLba); candidates.Add(e.EndLba); candidates.Add(e.EndLba + 1); }
            var checkedSet = new HashSet<long>();
            int iter = 0;
            while (candidates.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                long l = candidates.Min;
                candidates.Remove(l);
                if (l <= 0 || l >= sectors || !checkedSet.Add(l)) continue;
                byte[] buf;
                try { buf = disk.ReadBytes(l * ss, Math.Max(ss, 4096)); } catch (MediaReadException) { continue; }
                int before = found.Count;
                Consider(l, buf, buf.Length >= 1024 + 512 ? buf.AsSpan(1024) : default);
                // o Btrfs guarda o superbloco a 64 KiB do início
                if (l * ss + 0x10000 + 4096 <= disk.Length)
                    try
                    {
                        var bt = disk.ReadBytes(l * ss + 0x10000, 4096);
                        if (bt.Length >= 512 && Match(bt, 0x40, "_BHRfS_M"u8)) Consider(l + 0x10000 / ss, bt, default);
                    }
                    catch (MediaReadException) { }
                if (found.Count > before || found.Values.Any(f => f.StartLba == l))
                {
                    // segue a corrente: depois do fim de cada volume costuma vir o próximo
                    foreach (var f in found.Values.ToList())
                    {
                        long e = f.EndLba + 1;
                        foreach (var c in new[] { e, (e + align - 1) / align * align, (e + 62) / 63 * 63, e + 63 })
                            if (!checkedSet.Contains(c)) candidates.Add(c);
                        for (long k = 1; k <= 64; k++) { long c = (e + align - 1) / align * align + k * align; if (!checkedSet.Contains(c)) candidates.Add(c); }
                    }
                }
                if (++iter % 64 == 0) progress?.Report(new(L.T("Procurando setores de boot"), Math.Min(0.99, (double)l / sectors), found.Count, l, sectors));
            }
        }
        else
        {
            const int block = 4 << 20;
            var buf = new byte[block + 4096];
            for (long p = 0; p < disk.Length; p += block)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, disk.Length - p);
                int got;
                try { got = disk.Read(p, buf.AsSpan(0, n)); }
                catch (MediaReadException) { continue; }
                for (int o = 0; o < Math.Min(got, block); o += ss)
                {
                    var s = buf.AsSpan(o, ss);
                    bool boot = s[510] == 0x55 && s[511] == 0xAA || (ss > 512 && s[ss - 2] == 0x55);
                    bool ext = o + 1024 + 512 <= got && U16(buf, o + 1024 + 56) == 0xEF53;
                    bool hfs = o + 1024 + 512 <= got && (Match(buf, o + 1024, "H+\0\x04"u8) || Match(buf, o + 1024, "HX\0\x05"u8));
                    bool other = Match(s, 32, "NXSB"u8) || Match(s, 3, "ReFS"u8) || (o + 4096 <= got && Match(buf, o + 4086, "SWAPSPACE2"u8))
                        || Match(s, 0, "XFSB"u8) || Match(s, 0, LuksMagic) || Match(s, 0x40, "_BHRfS_M"u8)
                        || (o + 1024 + 512 <= got && U32(buf, o + 1024) == 0xF2F52010);
                    if (!boot && !ext && !hfs && !other && !Match(s, 3, "NTFS    "u8) && !Match(s, 3, "EXFAT   "u8)) continue;
                    bool f2 = o + 1024 + 512 <= got && U32(buf, o + 1024) == 0xF2F52010;
                    Consider((p + o) / ss, buf.AsSpan(o, Math.Min(4096, got - o)), ext || hfs || f2 ? buf.AsSpan(o + 1024) : default);
                }
                progress?.Report(new(L.T("Lendo o disco inteiro"), (double)p / disk.Length, found.Count, p / ss, sectors));
            }
        }

        var list = found.Values.Where(f => f.EndLba < sectors + 1).OrderBy(f => f.StartLba).ToList();
        // tamanhos desconhecidos (BitLocker): vão até o próximo achado
        for (int i = 0; i < list.Count; i++)
            if (list[i].LengthGuessed)
                list[i].SectorCount = (i + 1 < list.Count ? list[i + 1].StartLba : sectors - 34) - list[i].StartLba;
        if (table != null)
            foreach (var f in list)
                f.InCurrentTable = table.Entries.Any(e => e.StartLba == f.StartLba && Math.Abs(e.SectorCount - f.SectorCount) <= 1);
        // confirma pela cópia do setor de boot: o resto de um volume movido não tem a cópia no lugar
        foreach (var f in list) { f.BackupConfirmed = HasMatchingBackup(disk, f); f.Freshness = FreshnessOf(disk, f); }
        // sobreposições: a mais confiável fica; as outras continuam na lista, marcadas
        var keep = new List<FoundPartition>();
        foreach (var f in list.OrderByDescending(f => f.Score).ThenByDescending(f => f.Freshness).ThenBy(f => f.StartLba))
        {
            if (keep.Any(k => f.StartLba <= k.EndLba && k.StartLba <= f.EndLba)) f.Conflicting = true;
            else keep.Add(f);
        }
        // volumes "dentro" de outros e só com estrutura quebrada (ex.: imagem guardada num arquivo) somem
        var result = list.Where(f => !(f.Conflicting && !f.Verified && keep.Any(k => f.StartLba > k.StartLba && f.EndLba <= k.EndLba))).ToList();
        progress?.Report(new(L.T("Concluído"), 1, result.Count, sectors, sectors));
        return result.OrderBy(f => f.StartLba).ToList();
    }

    private static ReadOnlySpan<byte> LuksMagic => [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE];

    private static IEnumerable<FoundPartition> Analyze(IBlockDevice disk, long lba, ReadOnlySpan<byte> sec, ReadOnlySpan<byte> plus1024)
    {
        int ss = disk.SectorSize;
        var res = new List<FoundPartition>();
        if (sec.Length < 512) return res;

        // ── sistemas do Linux ──
        if (Match(sec, 0, "XFSB"u8) && FindXfs(disk, lba, sec) is { } xfs) { res.Add(xfs); return res; }
        if (Match(sec, 0x40, "_BHRfS_M"u8))
        {
            // cada cópia do superbloco diz onde ela mesma está (bytenr): o início é a posição menos isso
            long bytenr = (long)U64(sec, 0x30), total = (long)U64(sec, 0x70);
            long start = lba * ss - bytenr;
            if (start >= 0 && start % ss == 0 && total > 0 && U64(sec, 0x88) == 1)
                res.Add(new FoundPartition
                {
                    StartLba = start / ss, SectorCount = total / ss, FileSystem = FsKind.Btrfs, Variant = L.T("Btrfs"), Verified = true,
                    Evidence = bytenr == 0x10000 ? L.T("superbloco Btrfs") : L.T($"cópia do superbloco Btrfs ({DiskSizeText(bytenr)})"),
                    Label = Encoding.UTF8.GetString(sec.Slice(0x12B, 256)).TrimEnd('\0'),
                });
            return res;
        }
        if (Match(sec, 0, LuksMagic))
        {
            int ver = U16BE(sec, 6);
            string? label = ver == 2 ? Ascii(sec, 24, 48) : null;
            res.Add(new FoundPartition
            {
                StartLba = lba, SectorCount = 1, FileSystem = FsKind.Luks, Variant = ver == 2 ? "LUKS2" : "LUKS1", Verified = ver is 1 or 2,
                Evidence = L.T("cabeçalho LUKS"), LengthGuessed = true, Label = string.IsNullOrEmpty(label) ? null : label,
            });
            return res;
        }
        if (plus1024.Length >= 512 && U32(plus1024, 0) == 0xF2F52010)
        {
            int logBs = (int)U32(plus1024, 16);
            long blocks = (long)U64(plus1024, 36);
            // a segunda cópia fica no bloco 1: se 4 KiB antes há outra igual, esta é a cópia
            bool isCopy = lba * ss >= 4096 && U32(disk.ReadBytes(lba * ss - 4096 + 1024, 4), 0) == 0xF2F52010;
            if (!isCopy && logBs is >= 9 and <= 16 && blocks > 0)
                res.Add(new FoundPartition
                {
                    StartLba = lba, SectorCount = (blocks << logBs) / ss, FileSystem = FsKind.F2fs, Variant = "F2FS", Verified = true,
                    Evidence = "superbloco F2FS", Label = Utf16(plus1024, 0x7C, 512) is { Length: > 0 } fl ? fl : null,
                });
            return res;
        }

        var nb = NtfsVolume.ParseBoot(sec);
        if (nb != null)
        {
            long len = nb.TotalSectors * nb.BytesPerSector / ss + 1;
            var sub = disk.Slice(lba * ss, len * ss);
            bool mftOk = false; string? label = null;
            var primary = new FoundPartition { StartLba = lba, SectorCount = len, FileSystem = FsKind.Ntfs, Evidence = L.T("setor de boot") };
            try
            {
                var rec = sub.ReadBytes(nb.MftLcn * nb.ClusterSize, 4);
                mftOk = rec.Length == 4 && Match(rec, 0, "FILE"u8);
                if (mftOk) { var v = NtfsVolume.Open(sub, out _); label = v?.ReadLabel(); }
            }
            catch { }
            // é a cópia do fim? então o volume começa "total" setores antes
            long backupStart = lba - nb.TotalSectors * nb.BytesPerSector / ss;
            if (!mftOk && backupStart >= 0)
            {
                var sub2 = disk.Slice(backupStart * ss, len * ss);
                try
                {
                    var rec = sub2.ReadBytes(nb.MftLcn * nb.ClusterSize, 4);
                    if (rec.Length == 4 && Match(rec, 0, "FILE"u8))
                    {
                        var v = NtfsVolume.Open(sub2, out _);
                        res.Add(new FoundPartition
                        {
                            StartLba = backupStart, SectorCount = len, FileSystem = FsKind.Ntfs, Verified = true,
                            Evidence = L.T("cópia do setor de boot"), Label = v?.ReadLabel(), LooksLikeRecovery = IsRecoveryLabel(v?.ReadLabel()),
                        });
                        return res;
                    }
                }
                catch { }
            }
            primary.Verified = mftOk;
            primary.Label = label;
            primary.LooksLikeRecovery = IsRecoveryLabel(label) || (len * ss < (2L << 30) && mftOk && HasWinRe(sub));
            res.Add(primary);
            return res;
        }

        if (Match(sec, 3, "EXFAT   "u8))
        {
            int bpsShift = sec[108];
            if (bpsShift is >= 9 and <= 12)
            {
                long len = (long)U64(sec, 72) * (1L << bpsShift) / ss;
                // o setor 0 do exFAT é o principal; a reserva fica 12 setores depois
                long start = lba;
                string ev = L.T("setor de boot");
                bool prevIsBoot = lba >= 12 && Match(disk.ReadBytes((lba - 12) * ss, 16), 3, "EXFAT   "u8);
                if (prevIsBoot || (lba >= 12 && (long)U64(sec, 64) == lba - 12)) { start = lba - 12; ev = L.T("cópia do setor de boot"); }
                if (len > 0)
                {
                    var f = new FoundPartition { StartLba = start, SectorCount = len, FileSystem = FsKind.ExFat, Evidence = ev, Verified = true };
                    try { var v = ExFatVolume.Open(disk.Slice(start * ss, len * ss), out _); if (v != null) { v.ParseDirectory(v.ReadDirectory(v.RootCluster, 0, false), true); f.Label = v.Label; } } catch { }
                    res.Add(f);
                }
            }
            return res;
        }

        var fat = sec[510] == 0x55 && sec[511] == 0xAA ? FsDetect.FatKindFromBpb(sec) : FsKind.Unknown;
        if (fat != FsKind.Unknown)
        {
            int bps = U16(sec, 11);
            long tot = U16(sec, 19) != 0 ? U16(sec, 19) : U32(sec, 32);
            long len = tot * bps / ss;
            long start = lba;
            string ev = L.T("setor de boot");
            if (fat == FsKind.Fat32 && lba >= 6)
            {
                // a cópia do FAT32 fica no setor 6: se o setor 6 antes também é boot igual, este é a cópia
                var prev = disk.ReadBytes((lba - 6) * ss, 512);
                if (prev.Length == 512 && FsDetect.FatKindFromBpb(prev) == FsKind.Fat32 && U32(prev, 67) == U32(sec, 67)) return res;
                if (U16(sec, 50) == 6 && (prev.Length < 512 || prev[510] != 0x55)) { /* principal intacto */ }
            }
            var sub = disk.Slice(start * ss, len * ss);
            var v = FatVolume.Open(sub, out _);
            bool ok = false; bool esp = false;
            if (v != null)
            {
                try
                {
                    var media = sub.ReadBytes(v.FatOffset, 1);
                    ok = media.Length == 1 && media[0] >= 0xF0;
                    var root = v.ParseDirectory(v.ReadDirectory(v.Kind == FsKind.Fat32 ? v.RootCluster : 0, false));
                    esp = root.Any(e => !e.Deleted && e.IsDir && e.Name.Equals("EFI", StringComparison.OrdinalIgnoreCase));
                }
                catch { }
            }
            res.Add(new FoundPartition { StartLba = start, SectorCount = len, FileSystem = fat, Evidence = ev, Verified = ok, Label = v?.Label, LooksLikeEsp = esp });
            return res;
        }

        if (Match(sec, 3, "-FVE-FS-"u8))
        {
            long tot = (long)U64(sec, 40);
            res.Add(new FoundPartition
            {
                StartLba = lba, SectorCount = tot > 0 ? tot * U16(sec, 11) / ss : 1, FileSystem = FsKind.BitLocker,
                Evidence = L.T("cabeçalho BitLocker"), Verified = true, LengthGuessed = tot <= 0,
            });
            return res;
        }

        if (Match(sec, 3, "ReFS"u8) && sec.Length >= 0x28 && Match(sec, 16, "FSRS"u8))
        {
            long n = (long)U64(sec, 0x18), bps = U32(sec, 0x20);
            if (n > 0 && bps is 512 or 4096)
                res.Add(new FoundPartition { StartLba = lba, SectorCount = n * bps / ss, FileSystem = FsKind.ReFS, Variant = "ReFS", Evidence = L.T("setor de boot ReFS"), Verified = true });
            return res;
        }
        if (sec.Length >= 48 && Match(sec, 32, "NXSB"u8))
        {
            long bs = U32(sec, 36), count = (long)U64(sec, 40);
            if (bs is >= 4096 and <= 65536 && count > 0)
                res.Add(new FoundPartition { StartLba = lba, SectorCount = bs * count / ss, FileSystem = FsKind.Apfs, Variant = "APFS", Evidence = L.T("superbloco do contêiner APFS"), Verified = true });
            return res;
        }
        if (sec.Length >= 4096 && Match(sec, 4086, "SWAPSPACE2"u8) && U32(sec, 1024) == 1)
        {
            long last = U32(sec, 1028);
            if (last > 0)
                res.Add(new FoundPartition
                {
                    StartLba = lba, SectorCount = (last + 1) * 4096 / ss, FileSystem = FsKind.LinuxSwap, Variant = L.T("Linux swap"), Evidence = L.T("cabeçalho de swap"),
                    Verified = true, Label = Encoding.UTF8.GetString(sec.Slice(1024 + 28, 16)).TrimEnd('\0'),
                });
            return res;
        }
        if (plus1024.Length >= 512 && (Match(plus1024, 0, "H+"u8) || Match(plus1024, 0, "HX"u8)) && U16BE(plus1024, 2) is 4 or 5)
        {
            long bs = U32BE(plus1024, 40), total = U32BE(plus1024, 44);
            if (bs is >= 512 and <= 65536 && (bs & (bs - 1)) == 0 && total > 0)
                res.Add(new FoundPartition
                {
                    StartLba = lba, SectorCount = bs * total / ss, FileSystem = FsKind.HfsPlus, Variant = Match(plus1024, 0, "HX"u8) ? "HFSX" : "HFS+",
                    Evidence = L.T("cabeçalho de volume HFS+"), Verified = true,
                });
            return res;
        }
        if (plus1024.Length >= 512 && FsDetect.ParseExt(plus1024) is { } ext)
        {
            // cada grupo de blocos guarda uma cópia do superbloco, dizendo de qual grupo é: uma cópia
            // não vira partição fantasma — aponta para o início verdadeiro do volume
            long sbByte = lba * ss + 1024;
            long primaryByte = ext.Group == 0 ? sbByte - 1024 : sbByte - ((long)ext.Group * ext.BlocksPerGroup + ext.FirstDataBlock) * ext.BlockSize;
            if (primaryByte >= 0 && primaryByte % ss == 0)
                res.Add(new FoundPartition
                {
                    StartLba = primaryByte / ss, SectorCount = ext.Blocks * ext.BlockSize / ss, FileSystem = FsKind.Ext, Variant = ext.Variant,
                    Evidence = ext.Group == 0 ? L.T("superbloco ") + ext.Variant : L.T($"cópia do superbloco (grupo {ext.Group})"),
                    Verified = true, Label = ext.Label,
                });
        }
        return res;
    }

    private static long FreshnessOf(IBlockDevice disk, FoundPartition f)
    {
        try
        {
            int ss = disk.SectorSize;
            switch (f.FileSystem)
            {
                case FsKind.Ext:
                    {
                        var sb = disk.ReadBytes(f.StartLba * ss + 1024, 1024);
                        return sb.Length >= 0x34 && U16(sb, 56) == 0xEF53 ? Math.Max(U32(sb, 0x2C), U32(sb, 0x30)) : 0;
                    }
                case FsKind.Btrfs:
                    {
                        var sb = disk.ReadBytes(f.StartLba * ss + 0x10000, 4096);
                        return Match(sb, 0x40, "_BHRfS_M"u8) ? (long)U64(sb, 0x48) : 0;
                    }
            }
        }
        catch { }
        return 0;
    }

    private static string DiskSizeText(long b) => b >= 1L << 30 ? $"{b >> 30} GiB" : $"{b >> 20} MiB";

    /// <summary>
    /// XFS: cada grupo de alocação começa com uma cópia do superbloco. Volta de grupo em grupo
    /// enquanto houver superbloco com o mesmo UUID — o primeiro é o início do volume.
    /// </summary>
    private static FoundPartition? FindXfs(IBlockDevice disk, long lba, ReadOnlySpan<byte> sec)
    {
        int ss = disk.SectorSize;
        long bs = U32BE(sec, 4), blocks = (long)U64BE(sec, 8), agBlocks = U32BE(sec, 84), agCount = U32BE(sec, 88);
        if (bs is < 512 or > 65536 || blocks <= 0 || agBlocks <= 0 || agCount <= 0) return null;
        var uuid = sec.Slice(32, 16).ToArray();
        long agBytes = agBlocks * bs;
        long start = lba * ss;
        int copies = 0;
        for (long k = 1; k < agCount && start - agBytes >= 0; k++)
        {
            byte[] prev;
            try { prev = disk.ReadBytes(start - agBytes, 512); } catch { break; }
            if (prev.Length < 512 || !Match(prev, 0, "XFSB"u8) || !prev.AsSpan(32, 16).SequenceEqual(uuid)) break;
            start -= agBytes;
            copies++;
        }
        if (start % ss != 0) return null;
        return new FoundPartition
        {
            StartLba = start / ss, SectorCount = blocks * bs / ss, FileSystem = FsKind.Xfs, Variant = (U16BE(sec, 100) & 0xF) >= 5 ? "XFS v5" : "XFS",
            Verified = true, Evidence = copies == 0 ? L.T("superbloco XFS") : L.T($"cópia do superbloco XFS (grupo {copies})"),
            Label = Ascii(sec, 108, 12) is { Length: > 0 } l ? l : null,
        };
    }

    private static bool HasMatchingBackup(IBlockDevice disk, FoundPartition f)
    {
        try
        {
            int ss = disk.SectorSize;
            if (f.FileSystem == FsKind.Xfs)
            {
                var main = disk.ReadBytes(f.StartLba * ss, 512);
                long agBytes = (long)U32BE(main, 84) * U32BE(main, 4);
                var copy = disk.ReadBytes(f.StartLba * ss + agBytes, 512);
                return U32BE(main, 88) < 2 || (Match(copy, 0, "XFSB"u8) && copy.AsSpan(32, 16).SequenceEqual(main.AsSpan(32, 16)));
            }
            if (f.FileSystem == FsKind.Btrfs)
            {
                var main = disk.ReadBytes(f.StartLba * ss + 0x10000, 4096);
                long mirror = f.StartLba * ss + (64L << 20);
                if (f.SectorCount * ss < (64L << 20) + 4096) return Match(main, 0x40, "_BHRfS_M"u8);
                var copy = disk.ReadBytes(mirror, 4096);
                return Match(copy, 0x40, "_BHRfS_M"u8) && copy.AsSpan(0x20, 16).SequenceEqual(main.AsSpan(0x20, 16));
            }
            if (f.FileSystem == FsKind.F2fs)
            {
                var a = disk.ReadBytes(f.StartLba * ss + 1024, 512);
                var b = disk.ReadBytes(f.StartLba * ss + 4096 + 1024, 512);
                return U32(b, 0) == 0xF2F52010 && a.AsSpan(0x6C, 16).SequenceEqual(b.AsSpan(0x6C, 16));
            }
            if (f.FileSystem == FsKind.Luks)
            {
                // LUKS2 tem um segundo cabeçalho binário a 16 KiB ("SKUL")
                var b = disk.ReadBytes(f.StartLba * ss + 0x4000, 512);
                return b.Length >= 6 && b[0] == (byte)'S' && b[1] == (byte)'K' && b[2] == (byte)'U' && b[3] == (byte)'L';
            }
            if (f.FileSystem == FsKind.Ext)
            {
                var main = FsDetect.ParseExt(disk.ReadBytes(f.StartLba * ss + 1024, 1024));
                if (main == null) return false;
                long b1 = f.StartLba * ss + (main.BlocksPerGroup + main.FirstDataBlock) * main.BlockSize;
                var copy = FsDetect.ParseExt(disk.ReadBytes(b1, 1024));
                return copy != null && copy.Uuid == main.Uuid && copy.Group == 1;
            }
            var primary = disk.ReadBytes(f.StartLba * ss, 512);
            long backupLba = f.FileSystem switch
            {
                FsKind.Ntfs => f.EndLba,
                FsKind.Fat32 => f.StartLba + 6,
                FsKind.ExFat => f.StartLba + 12,
                _ => -1,
            };
            if (backupLba < 0 || backupLba * ss >= disk.Length) return false;
            var backup = disk.ReadBytes(backupLba * ss, 512);
            // mesmo volume: mesmo número de série e mesma geometria
            return primary.Length == 512 && backup.Length == 512 && primary.AsSpan(3, 8).SequenceEqual(backup.AsSpan(3, 8))
                && (f.FileSystem == FsKind.Ntfs ? U64(primary, 72) == U64(backup, 72) && U64(primary, 40) == U64(backup, 40)
                  : f.FileSystem == FsKind.Fat32 ? U32(primary, 67) == U32(backup, 67)
                  : U32(primary, 100) == U32(backup, 100));
        }
        catch { return false; }
    }

    private static bool IsRecoveryLabel(string? l) => l != null && (l.Contains("Recovery", StringComparison.OrdinalIgnoreCase) || l.Contains("Recupera", StringComparison.OrdinalIgnoreCase) || l.Contains("WinRE", StringComparison.OrdinalIgnoreCase));

    private static bool HasWinRe(IBlockDevice vol)
    {
        try
        {
            var v = NtfsVolume.Open(vol, out _);
            if (v == null) return false;
            for (long i = 16; i < Math.Min(v.RecordCount, 2000); i++)
            {
                var r = v.ReadRecord(i);
                if (r?.BestName?.Name.Equals("Winre.wim", StringComparison.OrdinalIgnoreCase) == true) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Monta a tabela nova: as partições atuais que não colidem + as escolhidas.</summary>
    public static PartitionTable BuildRestoredTable(PartitionTable current, IEnumerable<FoundPartition> chosen, TableStyle style, long diskSectors, int ss)
    {
        var t = new PartitionTable
        {
            Style = style, SectorSize = ss, DiskSectors = diskSectors,
            DiskGuid = current.Style == TableStyle.Gpt ? current.DiskGuid : Guid.NewGuid(),
            DiskSignature = current.DiskSignature, BootCode = current.BootCode,
        };
        var pick = chosen.OrderBy(c => c.StartLba).ToList();
        if (current.Style == style)
            foreach (var e in current.Entries)
                if (!pick.Any(p => p.StartLba <= e.EndLba && e.StartLba <= p.EndLba)) t.Entries.Add(e.Clone());
        foreach (var p in pick)
        {
            var e = new PartitionEntry { StartLba = p.StartLba, SectorCount = p.SectorCount, Name = p.Label ?? "" };
            if (style == TableStyle.Gpt)
            {
                e.TypeGuid = p.LooksLikeEsp ? PartitionTypes.Esp : p.LooksLikeRecovery ? PartitionTypes.WinRecovery
                    : p.FileSystem switch
                    {
                        FsKind.HfsPlus => PartitionTypes.AppleHfs, FsKind.Apfs => PartitionTypes.AppleApfs,
                        FsKind.Ext or FsKind.Xfs or FsKind.Btrfs or FsKind.F2fs or FsKind.LinuxSwap or FsKind.Luks or FsKind.LvmPv or FsKind.MdRaid
                            or FsKind.ReiserFs or FsKind.Jfs or FsKind.Bcachefs => PartitionTypes.ForFileSystem(p.FileSystem).gpt,
                        _ => PartitionTypes.BasicData,
                    };
                if (p.LooksLikeRecovery) e.Attributes = PartitionTypes.RecoveryAttributes;
                if (p.LooksLikeEsp && string.IsNullOrEmpty(e.Name)) e.Name = "EFI system partition";
                e.UniqueGuid = Guid.NewGuid();
            }
            else
            {
                e.MbrType = p.LooksLikeEsp ? (byte)0xEF : p.LooksLikeRecovery ? (byte)0x27 : p.FileSystem switch
                {
                    FsKind.LinuxSwap => (byte)0x82, FsKind.HfsPlus or FsKind.Apfs => (byte)0xAF,
                    FsKind.Xfs or FsKind.Btrfs or FsKind.F2fs or FsKind.Luks or FsKind.Ext => (byte)0x83,
                    _ => PartitionTypes.GptToMbr(PartitionTypes.BasicData, p.FileSystem) ?? 0x07,
                };
            }
            t.Entries.Add(e);
        }
        if (style == TableStyle.Mbr && t.Entries.Count > 4)
        {
            // mais de 4: as 3 primeiras ficam primárias e as demais viram lógicas numa estendida,
            // que é como esses discos costumavam ser (o codec põe um EBR antes de cada lógica)
            var ord = t.Entries.OrderBy(e => e.StartLba).ToList();
            for (int i = 3; i < ord.Count; i++) { ord[i].IsLogical = true; ord[i].Active = false; }
        }
        // o GPT precisa de folga no começo e no fim
        if (style == TableStyle.Gpt)
        {
            long first = PartitionCodec.GptFirstUsable(t), last = PartitionCodec.GptLastUsable(t);
            foreach (var e in t.Entries)
                if (e.StartLba < first || e.EndLba > last)
                    throw new InvalidOperationException(L.T($"A partição em {e.StartLba} invade a área reservada do GPT. Use MBR para este disco."));
        }
        return t;
    }
}
