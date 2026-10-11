using System.Security.Cryptography;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

/// <summary>Varredura do disco inteiro achando e lendo partições perdidas.</summary>
public static class DiskScanTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }

    static Dictionary<string, string> Hashes(ScanResult r) => r.Items.Where(i => !i.IsDirectory && i.Data != null && i.Size < 16 << 20)
        .GroupBy(i => i.Name).ToDictionary(g => g.Key, g => { var f = g.First(); var b = new byte[f.Size]; f.Data!.Read(0, b); return Convert.ToHexStringLower(SHA256.HashData(b)); });

    public static int Run(string dir)
    {
        Console.WriteLine("\nVarredura do disco inteiro");
        var man = File.ReadAllLines(Path.Combine(dir, "manifest.txt")).Where(l => l.Length > 66 && l[64] == ' ').ToDictionary(l => l[66..].Trim(), l => l[..64]);
        using (var d = new ImageFileDevice(Path.Combine(dir, "lost.img")))
        {
            var r = DiskScanner.Scan(d, PartitionCodec.Read(d), 0, d.Length, "lost.img", new ScanOptions(), null, CancellationToken.None);
            var lost = r.Root.Children!.Where(c => c.Name.StartsWith("Partição perdida")).ToList();
            Check(lost.Count == 3, $"tabela destruída: 3 partições perdidas lidas ({string.Join(", ", lost.Select(l => l.Name))})");
            var h = Hashes(r);
            int ok = man.Count(kv => h.TryGetValue(kv.Key, out var x) && x == kv.Value);
            Check(ok == man.Count, $"{ok}/{man.Count} arquivos (vivos e apagados) das partições perdidas com nome e hash idêntico");
        }
        string ext = Path.Combine(dir, "extfiles.img");
        // partição excluída da tabela: a varredura da área livre a acha e lê
        var copy = Path.Combine(Path.GetTempPath(), "lx-dscan.img");
        File.Copy(File.Exists(ext) ? ext : Path.Combine(dir, "disk.img"), copy, true);
        var info = new DiskInfo { Number = 302, IsImage = true, ImagePath = copy, ReadOnly = false };
        using (var d = new ImageFileDevice(copy)) { info.Size = d.Length; LayoutBuilder.Build(info, d); }
        var victim = info.Partitions.First(p => !p.IsUnallocated);
        var t = info.Table.Clone();
        t.Entries.RemoveAll(e => e.StartLba == victim.StartLba);
        DiskOperations.WriteTable(info, t, "teste", new OpSink());
        using (var d = new ImageFileDevice(copy))
        {
            var tt = PartitionCodec.Read(d);
            var free = (tt.FreeRegions(2048).First(f => f.start <= victim.StartLba));
            var r = DiskScanner.Scan(d, tt, free.start * 512, free.count * 512, "livre", new ScanOptions(), null, CancellationToken.None);
            var lost = r.Root.Children?.FirstOrDefault(c => c.Name.StartsWith("Partição perdida"));
            Check(lost != null && r.Items.Count(i => !i.IsDirectory) > 10, $"partição excluída achada e lida a partir do espaço livre: {lost?.Name} ({r.Items.Count(i => !i.IsDirectory)} arquivos)");
        }
        File.Delete(copy);
        return fails;
    }
}
