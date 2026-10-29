using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Partitioning;

namespace LibertyX.Partition.Core.Recovery;

/// <summary>
/// Varredura de um disco inteiro (ou de uma área livre dele), no estilo R-Studio: lê as partições
/// da tabela, procura volumes perdidos (excluídos, de tabela antiga, de antes de uma formatação de
/// disco) e lê cada um com a árvore completa — nomes, pastas, datas — e só então escava por
/// assinatura o que nenhum sistema de arquivos explica.
/// </summary>
public static class DiskScanner
{
    public static ScanResult Scan(IBlockDevice disk, PartitionTable table, long regionStart, long regionLength, string rootName,
                                  ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct, ScanResult? into = null)
    {
        int ss = disk.SectorSize;
        long regionEnd = regionStart + regionLength;
        // "into" permite à tela mostrar o que já foi achado se a varredura for interrompida
        var master = into ?? new ScanResult { Root = new RecoveredItem { Id = 0, Name = rootName, IsDirectory = true, Origin = ItemOrigin.Live } };
        master.Root.Name = rootName;
        var covered = new List<(long start, long length)>();
        var carve = new List<(long, long)>();
        bool whole = regionStart == 0 && regionLength >= disk.Length;

        void AddVolume(long ofs, long len, string name, bool lost, ScanOptions o)
        {
            var vol = disk.Slice(ofs, len);
            var sub = FileSystemScanner.Scan(vol, o, progress, ct);
            sub.Root.Name = name + $" ({sub.FileSystem})";
            if (lost) { sub.Root.Origin = ItemOrigin.Orphan; sub.Root.Note = L.T("partição perdida: não está na tabela"); }
            master.Attach(master.Root, sub.Root);
            master.Items.AddRange(sub.Items.Where(i => i != sub.Root));
            master.Log.AddRange(sub.Log.Select(l => $"{name}: {l}"));
            covered.Add((ofs, len));
            if (sub.Allocation != null && opt.CarveFreeSpaceOnly) carve.AddRange(sub.Allocation.FreeRanges(vol.Length).Select(r => (r.offset + ofs, r.length)));
            else carve.Add((ofs, len));
        }

        // 1. partições da tabela dentro da região
        if (whole)
            foreach (var e in table.Ordered)
            {
                ct.ThrowIfCancellationRequested();
                long ofs = e.StartLba * ss, len = e.SectorCount * ss;
                if (ofs + len > disk.Length) continue;
                var fs = FsDetect.Detect(disk.Slice(ofs, len));
                if (FsDetect.CanRead(fs) || fs == FsKind.Unknown) AddVolume(ofs, len, L.T($"Partição {e.Slot}"), false, opt);
                else { covered.Add((ofs, len)); carve.Add((ofs, len)); }
            }

        // 2. volumes perdidos (fora da tabela)
        progress?.Report(new(L.T("Procurando partições perdidas"), 0, master.Items.Count, 0, 1));
        List<FoundPartition> found;
        using (var view = new SubDevice(disk, 0, disk.Length))
            found = PartitionFinder.Scan(view, opt.DeepScan, progress, ct);
        int lostCount = 0;
        foreach (var f in found.Where(f => !f.InCurrentTable && !f.Conflicting && FsDetect.CanRead(f.FileSystem)))
        {
            long ofs = f.StartLba * ss, len = f.SectorCount * ss;
            if (ofs < regionStart || ofs + len > regionEnd || ofs + len > disk.Length) continue;
            if (covered.Any(c => ofs < c.start + c.length && c.start < ofs + len)) continue;
            lostCount++;
            string label = string.IsNullOrEmpty(f.Label) ? DiskOperationsSize(len) : f.Label!;
            AddVolume(ofs, len, L.T($"Partição perdida · {label}"), true, new ScanOptions
            {
                IncludeLive = true, DeepScan = false, CarveFreeSpaceOnly = opt.CarveFreeSpaceOnly, OrphanMftRecords = opt.OrphanMftRecords,
            });
        }
        if (lostCount > 0) master.Log.Insert(0, L.T($"{lostCount} partição(ões) perdida(s) encontrada(s) e lida(s) com a árvore completa."));

        // 3. o que sobrou da região, sem sistema de arquivos que explique
        long cur = regionStart;
        foreach (var c in covered.OrderBy(c => c.start))
        {
            if (c.start > cur) carve.Add((cur, Math.Min(c.start, regionEnd) - cur));
            cur = Math.Max(cur, c.start + c.length);
        }
        if (cur < regionEnd) carve.Add((cur, regionEnd - cur));
        var ranges = carve.Where(r => r.Item2 > 0)
            .Select(r => (Math.Max(r.Item1, regionStart), Math.Min(r.Item1 + r.Item2, regionEnd) - Math.Max(r.Item1, regionStart)))
            .Where(r => r.Item2 > 0).OrderBy(r => r.Item1).ToList();
        if (opt.DeepScan) Carver.Carve(disk, ranges, master, opt, progress, ct);
        return master;
    }

    private static string DiskOperationsSize(long b) => Operations.DiskOperations.Size(b);
}
