using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

/// <summary>MBR com lógicas e 7 sistemas de arquivos diferentes, com o MBR zerado.</summary>
public static class MixedTests
{
    public static int Run(string dir)
    {
        string img = Path.Combine(dir, "mixed-lost.img");
        if (!File.Exists(img)) return 0;
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }
        Console.WriteLine("\nMBR com lógicas, 7 sistemas de arquivos");
        var exp = File.ReadAllLines(Path.Combine(dir, "mixed-manifest.txt")).Select(l => l.Split('=')).Select(x => (fs: x[0], r: x[1].Split('-').Select(long.Parse).ToArray())).ToList();
        foreach (var deep in new[] { false, true })
        {
            using var d = new ImageFileDevice(img);
            var f = PartitionFinder.Scan(d, deep, null, CancellationToken.None);
            foreach (var x in f) Console.WriteLine($"     {x.StartLba}-{x.EndLba} {x.Variant ?? FsDetect.Name(x.FileSystem)} '{x.Label}' {(x.Conflicting ? "SOBREPOSTA" : "")}");
            var good = f.Where(x => !x.Conflicting).ToList();
            Check(good.Count == 7 && good.Zip(exp).All(z => z.First.StartLba == z.Second.r[0] && z.First.EndLba == z.Second.r[1]),
                $"busca {(deep ? "completa" : "rápida")}: as 7 (NTFS, FAT32, ext4, HFS+, swap, exFAT, ext3) com limites exatos");
        }
        var copy = Path.Combine(Path.GetTempPath(), "lx-mixed.img");
        File.Copy(img, copy, true);
        var di = new DiskInfo { Number = 304, IsImage = true, ImagePath = copy, ReadOnly = false };
        using (var d = new ImageFileDevice(copy)) { di.Size = d.Length; LayoutBuilder.Build(di, d); }
        List<FoundPartition> found;
        using (var d = new ImageFileDevice(copy)) found = PartitionFinder.Scan(d, false, null, CancellationToken.None);
        DiskOperations.RestoreFound(di, found.Where(x => !x.Conflicting), TableStyle.Mbr, new OpSink());
        using (var d = new ImageFileDevice(copy))
        {
            var t = PartitionCodec.Read(d);
            Check(t.Style == TableStyle.Mbr && t.Entries.Count == 7 && t.Entries.Count(e => e.IsLogical) == 4, $"MBR restaurado: 3 primárias + 4 lógicas ({t.Warnings.Count} avisos)");
            Check(t.Entries.Any(e => e.MbrType == 0x82) && t.Entries.Any(e => e.MbrType == 0xAF) && t.Entries.Count(e => e.MbrType == 0x83) == 2, "tipos certos: swap 0x82, HFS+ 0xAF, ext 0x83");
            var di2 = new DiskInfo { Number = 304, IsImage = true, ImagePath = copy, Size = d.Length };
            LayoutBuilder.Build(di2, d);
            Check(di2.Partitions.Count(p => !p.IsUnallocated) == 7 && di2.Partitions.Where(p => !p.IsUnallocated).Select(p => p.FileSystem).Distinct().Count() >= 6, "o disco relido mostra as 7 com o sistema de arquivos certo");
        }
        File.Delete(copy);
        return fails;
    }
}
