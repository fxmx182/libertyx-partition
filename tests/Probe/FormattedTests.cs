using System.Security.Cryptography;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

/// <summary>NTFS formatado rapidamente por cima: árvore antiga reconstruída a partir dos registros MFT soltos.</summary>
public static class FormattedTests
{
    public static int Run(string dir)
    {
        string img = Path.Combine(dir, "formatted.img");
        if (!File.Exists(img)) return 0;
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }
        Console.WriteLine("\nNTFS formatado por cima");
        var man = File.ReadAllLines(Path.Combine(dir, "formatted-manifest.txt")).Select(l => l.Split('|')).ToDictionary(x => x[0], x => x[1]);
        var info = new DiskInfo { Number = 303, IsImage = true, ImagePath = img };
        using var dev = new ImageFileDevice(img);
        info.Size = dev.Length;
        LayoutBuilder.Build(info, dev);
        var p = info.Partitions.First(x => !x.IsUnallocated);
        var vol = dev.Slice(p.Offset, p.Length);
        var opt = new ScanOptions { DeepScan = true };
        var res = FileSystemScanner.Scan(vol, opt, null, CancellationToken.None);
        Console.WriteLine($"   rápida: {res.Items.Count(i => !i.IsDirectory)} apagados (o MFT novo não sabe do antigo)");
        Carver.Carve(vol, res.Allocation!.FreeRanges(vol.Length).ToList(), res, opt, null, CancellationToken.None, res.ClusterSize);
        foreach (var l in res.Log) Console.WriteLine("   " + l);
        var top = res.Root.Children!.FirstOrDefault(c => c.Name.StartsWith("MFT antigo"));
        Check(top != null, "pasta \"MFT antigo\" criada");
        int pathOk = 0, hashOk = 0, goodCond = 0, badReported = 0;
        foreach (var f in res.Items.Where(i => !i.IsDirectory && i.Origin == ItemOrigin.Orphan))
        {
            var parts = new List<string>();
            for (var x = f; x != null && x != top; x = x.Parent) parts.Insert(0, x.Name);
            string rel = string.Join('/', parts);
            if (!man.TryGetValue(rel, out var h)) continue;
            pathOk++;
            var b = new byte[f.Size]; f.Data!.Read(0, b);
            bool same = Convert.ToHexStringLower(SHA256.HashData(b)) == h;
            if (same) hashOk++;
            if (f.Condition == Condition.Good) goodCond++;
            if (!same && f.Condition is Condition.Partial or Condition.Overwritten) badReported++;
        }
        Check(pathOk >= man.Count * 0.9, $"{pathOk}/{man.Count} arquivos de antes da formatação com caminho completo original");
        Check(hashOk >= pathOk - 2 && hashOk > 0, $"{hashOk}/{pathOk} com conteúdo idêntico (cluster antigo deduzido)");
        Check(hashOk + badReported == pathOk, $"os {pathOk - hashOk} que a formatação sobrescreveu aparecem como Parcial/Sobrescrito");
        return fails;
    }
}
