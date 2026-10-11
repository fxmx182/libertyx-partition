using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

/// <summary>ext2/3/4: achar, confirmar, resgatar pelo superbloco de reserva, restaurar a tabela.</summary>
public static class ExtTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }

    public static int Run(string dir)
    {
        string lost = Path.Combine(dir, "ext-lost.img");
        if (!File.Exists(lost)) { Console.WriteLine("\n(sem imagens ext — rode tests/scripts/make-ext4.sh)"); return 0; }
        Console.WriteLine("\next2/3/4");
        var expected = File.ReadAllLines(Path.Combine(dir, "ext-manifest.txt")).Select(l => l.Split('=')[1].Split('-').Select(long.Parse).ToArray()).ToList();

        foreach (var deep in new[] { false, true })
        {
            using var d = new ImageFileDevice(lost);
            var f = PartitionFinder.Scan(d, deep, null, CancellationToken.None);
            Check(f.Count == 3 && f.Zip(expected).All(z => z.First.StartLba == z.Second[0] && z.First.EndLba == z.Second[1]),
                $"busca {(deep ? "completa" : "rápida")}: 3 volumes com limites exatos, sem cópias de superbloco como fantasmas");
            Check(f.Select(x => x.Variant).SequenceEqual(["ext4", "ext3", "ext2"]) && f.All(x => x.BackupConfirmed), "ext4/ext3/ext2 identificados e confirmados pela cópia do grupo 1");
        }

        // superbloco principal do ext4 destruído: só a cópia de reserva sobra
        var broken = Path.Combine(Path.GetTempPath(), "lx-ext-broken.img");
        File.Copy(lost, broken, true);
        using (var d = new ImageFileDevice(broken, true)) d.Write(expected[0][0] * 512, new byte[4096]);
        using (var d = new ImageFileDevice(broken))
        {
            var quick = PartitionFinder.Scan(d, false, null, CancellationToken.None);
            var deep = PartitionFinder.Scan(d, true, null, CancellationToken.None);
            var r = deep.FirstOrDefault(x => x.Label == "LINUXRAIZ");
            Check(r != null && r.StartLba == expected[0][0] && r.EndLba == expected[0][1] && r.Evidence.Contains("cópia"),
                $"superbloco principal destruído: a busca completa acha o ext4 pela cópia (rápida achou {quick.Count})");
        }

        // restaurar a tabela e ver no painel
        var di = new DiskInfo { Number = 300, IsImage = true, ImagePath = broken, ReadOnly = false };
        File.Copy(lost, broken, true);
        using (var d = new ImageFileDevice(broken)) { di.Size = d.Length; LayoutBuilder.Build(di, d); }
        List<FoundPartition> found;
        using (var d = new ImageFileDevice(broken)) found = PartitionFinder.Scan(d, false, null, CancellationToken.None);
        DiskOperations.RestoreFound(di, found, TableStyle.Gpt, new OpSink());
        var back = new DiskInfo { Number = 300, IsImage = true, ImagePath = broken };
        using (var d = new ImageFileDevice(broken)) { back.Size = d.Length; LayoutBuilder.Build(back, d); }
        var parts = back.Partitions.Where(p => !p.IsUnallocated).ToList();
        Check(parts.Count == 3 && parts.All(p => p.Entry!.TypeGuid == PartitionTypes.LinuxFs && p.FileSystem == FsKind.Ext), "tabela GPT restaurada com o tipo Linux");
        Check(parts.Select(p => p.FsVariant).SequenceEqual(["ext4", "ext3", "ext2"]) && parts.All(p => p.Used > 0 && p.Free > 0) && parts[0].Label == "LINUXRAIZ",
            $"painel mostra variante, rótulo e uso ({string.Join(", ", parts.Select(p => $"{p.FsVariant} {p.Label} {p.Used / 1024}K usados"))})");
        File.Delete(broken);

        using (var d = new ImageFileDevice(Path.Combine(dir, "extmbr-lost.img")))
        {
            var f = PartitionFinder.Scan(d, false, null, CancellationToken.None);
            Check(f.Count == 1 && f[0].Variant == "ext4" && f[0].StartLba == 2048 && f[0].SectorCount == 250L * 2048 && f[0].Label == "MBR64", "ext4 de 64 bits em disco MBR");
        }
        Files(dir);
        return fails;
    }

    static void Files(string dir)
    {
        string img = Path.Combine(dir, "extfiles.img");
        if (!File.Exists(img)) return;
        var man = File.ReadAllLines(Path.Combine(dir, "extfiles-manifest.txt")).Select(l => l.Split('|')).ToList();
        var deletedRel = new[] { "fotos/praia.jpg", "docs/contrato.pdf", "video/clipe.mp4", "projetos/codigo/main.py" };
        var info = new DiskInfo { Number = 301, IsImage = true, ImagePath = img };
        using var dev = new ImageFileDevice(img);
        info.Size = dev.Length;
        LayoutBuilder.Build(info, dev);
        foreach (var p in info.Partitions.Where(x => x.FileSystem == FsKind.Ext))
        {
            var vol = dev.Slice(p.Offset, p.Length);
            var res = FileSystemScanner.Scan(vol, new ScanOptions { IncludeLive = true }, null, CancellationToken.None);
            string fs = res.FileSystem;
            var exp = man.Where(m => m[0] == fs).ToDictionary(m => m[1], m => m[2]);
            var got = new Dictionary<string, (string hash, ItemOrigin origin, Condition cond, string? note)>();
            foreach (var f in res.Items.Where(i => !i.IsDirectory))
            {
                string rel = string.Join('/', f.FullPath.Split('\\').Skip(1));
                string h = "";
                if (f.Data != null) { var b = new byte[f.Size]; f.Data.Read(0, b); h = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b)); }
                got[rel] = (h, f.Origin, f.Condition, f.Note);
            }
            int liveOk = exp.Count(kv => !deletedRel.Contains(kv.Key) && got.TryGetValue(kv.Key, out var g) && g.hash == kv.Value && g.origin == ItemOrigin.Live);
            int liveTotal = exp.Count(kv => !deletedRel.Contains(kv.Key));
            Check(liveOk == liveTotal, $"{fs}: {liveOk}/{liveTotal} arquivos existentes lidos com hash idêntico (pastas e nomes certos)");
            int delFound = deletedRel.Count(r => got.TryGetValue(r, out var g) && g.origin == ItemOrigin.Deleted);
            int delOk = deletedRel.Count(r => got.TryGetValue(r, out var g) && g.origin == ItemOrigin.Deleted && g.hash == exp[r]);
            if (fs != "ext2") Check(delFound == deletedRel.Length, $"{fs}: {delFound}/4 apagados listados com nome e pasta (inclusive dentro de pasta apagada)");
            else Check(res.Log.Any(l => l.Contains("ext2 não tem journal")), "ext2: o app explica que sem journal só a varredura profunda acha apagados");
            Console.WriteLine($"         {fs}: {delOk}/4 apagados recuperados com hash idêntico · " + string.Join(" | ", res.Log));
            foreach (var r in deletedRel) if (got.TryGetValue(r, out var g)) Console.WriteLine($"           {r}: {g.cond} {(g.hash == exp[r] ? "íntegro" : "diferente")} {g.note}");
            if (fs != "ext2") Check(delOk == 4, $"{fs}: apagados recuperados pelo journal");
        }
    }
}
