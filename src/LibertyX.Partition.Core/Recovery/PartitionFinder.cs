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
                try { buf = disk.ReadBytes(l * ss, Math.Max(ss, 2048)); } catch (MediaReadException) { continue; }
                int before = found.Count;
                Consider(l, buf, buf.Length >= 1024 + 512 ? buf.AsSpan(1024) : default);
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
                if (++iter % 64 == 0) progress?.Report(new("Procurando setores de boot", Math.Min(0.99, (double)l / sectors), found.Count, l, sectors));
            }
        }
        else
        {
            const int block = 4 << 20;
            var buf = new byte[block + 2048];
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
                    if (!boot && !ext && !Match(s, 3, "NTFS    "u8) && !Match(s, 3, "EXFAT   "u8)) continue;
                    Consider((p + o) / ss, buf.AsSpan(o, Math.Min(ss * 4, got - o)), ext ? buf.AsSpan(o + 1024) : default);
                }
                progress?.Report(new("Lendo o disco inteiro", (double)p / disk.Length, found.Count, p / ss, sectors));
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
        foreach (var f in list) f.BackupConfirmed = HasMatchingBackup(disk, f);
        // sobreposições: a mais confiável fica; as outras continuam na lista, marcadas
        var keep = new List<FoundPartition>();
        foreach (var f in list.OrderByDescending(f => f.Score).ThenBy(f => f.StartLba))
        {
            if (keep.Any(k => f.StartLba <= k.EndLba && k.StartLba <= f.EndLba)) f.Conflicting = true;
            else keep.Add(f);
        }
        // volumes "dentro" de outros e só com estrutura quebrada (ex.: imagem guardada num arquivo) somem
        var result = list.Where(f => !(f.Conflicting && !f.Verified && keep.Any(k => f.StartLba > k.StartLba && f.EndLba <= k.EndLba))).ToList();
        progress?.Report(new("Concluído", 1, result.Count, sectors, sectors));
        return result.OrderBy(f => f.StartLba).ToList();
    }

    private static IEnumerable<FoundPartition> Analyze(IBlockDevice disk, long lba, ReadOnlySpan<byte> sec, ReadOnlySpan<byte> plus1024)
    {
        int ss = disk.SectorSize;
        var res = new List<FoundPartition>();
        if (sec.Length < 512) return res;

        var nb = NtfsVolume.ParseBoot(sec);
        if (nb != null)
        {
            long len = nb.TotalSectors * nb.BytesPerSector / ss + 1;
            var sub = disk.Slice(lba * ss, len * ss);
            bool mftOk = false; string? label = null;
            var primary = new FoundPartition { StartLba = lba, SectorCount = len, FileSystem = FsKind.Ntfs, Evidence = "setor de boot" };
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
                            Evidence = "cópia do setor de boot", Label = v?.ReadLabel(), LooksLikeRecovery = IsRecoveryLabel(v?.ReadLabel()),
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
                string ev = "setor de boot";
                bool prevIsBoot = lba >= 12 && Match(disk.ReadBytes((lba - 12) * ss, 16), 3, "EXFAT   "u8);
                if (prevIsBoot || (lba >= 12 && (long)U64(sec, 64) == lba - 12)) { start = lba - 12; ev = "cópia do setor de boot"; }
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
            string ev = "setor de boot";
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
                Evidence = "cabeçalho BitLocker", Verified = true, LengthGuessed = tot <= 0,
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
                    Evidence = ext.Group == 0 ? "superbloco " + ext.Variant : $"cópia do superbloco (grupo {ext.Group})",
                    Verified = true, Label = ext.Label,
                });
        }
        return res;
    }

    private static bool HasMatchingBackup(IBlockDevice disk, FoundPartition f)
    {
        try
        {
            int ss = disk.SectorSize;
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
                    : p.FileSystem == FsKind.Ext ? PartitionTypes.LinuxFs : PartitionTypes.BasicData;
                if (p.LooksLikeRecovery) e.Attributes = PartitionTypes.RecoveryAttributes;
                if (p.LooksLikeEsp && string.IsNullOrEmpty(e.Name)) e.Name = "EFI system partition";
                e.UniqueGuid = Guid.NewGuid();
            }
            else
            {
                e.MbrType = p.LooksLikeEsp ? (byte)0xEF : p.LooksLikeRecovery ? (byte)0x27 : PartitionTypes.GptToMbr(PartitionTypes.BasicData, p.FileSystem) ?? 0x07;
            }
            t.Entries.Add(e);
        }
        if (style == TableStyle.Mbr && t.Entries.Count > 4)
            throw new InvalidOperationException("MBR comporta só 4 partições primárias — escolha GPT ou menos partições.");
        // o GPT precisa de folga no começo e no fim
        if (style == TableStyle.Gpt)
        {
            long first = PartitionCodec.GptFirstUsable(t), last = PartitionCodec.GptLastUsable(t);
            foreach (var e in t.Entries)
                if (e.StartLba < first || e.EndLba > last)
                    throw new InvalidOperationException($"A partição em {e.StartLba} invade a área reservada do GPT. Use MBR para este disco.");
        }
        return t;
    }
}
