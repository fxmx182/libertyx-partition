using System.Security.Cryptography;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Recovery;

/// <summary>Leitores de XFS e Btrfs contra imagens gravadas pelo kernel (tests/scripts/make-xfs.sh, make-btrfs.sh).</summary>
public static class LinuxFsTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }

    static Dictionary<string, string> Manifest(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 66).ToDictionary(l => l[66..].Trim().TrimStart('.', '/'), l => l[..64].ToUpperInvariant());

    static string Hash(RecoveredItem i)
    {
        using var sha = SHA256.Create();
        var buf = new byte[1 << 20];
        long done = 0;
        while (done < i.Size)
        {
            int n = i.Data!.Read(done, buf.AsSpan(0, (int)Math.Min(buf.Length, i.Size - done)));
            if (n <= 0) break;
            sha.TransformBlock(buf, 0, n, null, 0);
            done += n;
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    static string Rel(RecoveredItem i)
    {
        var parts = new List<string>();
        for (var x = i; x?.Parent != null; x = x.Parent) parts.Insert(0, x.Name);
        return string.Join('/', parts);
    }

    public static int Run(string dir)
    {
        foreach (var (img, kind) in new[] { ("xfs.img", FsKind.Xfs), ("btrfs.img", FsKind.Btrfs) })
        {
            string path = Path.Combine(dir, img);
            if (!File.Exists(path)) { Console.WriteLine($"({img} não existe — pulado)"); continue; }
            Console.WriteLine($"== {img}");
            using var dev = new ImageFileDevice(path);
            Check(FsDetect.Detect(dev) == kind, $"reconhecido como {FsDetect.Name(kind)}");
            var live = Manifest(Path.Combine(dir, img.Replace(".img", "-manifest.txt")));
            var gone = Manifest(Path.Combine(dir, img.Replace(".img", "-apagados.txt")));
            foreach (var k in gone.Keys) live.Remove(k);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = FileSystemScanner.Scan(dev, new ScanOptions { IncludeLive = true }, null, CancellationToken.None);
            Console.WriteLine($"   {res.FileSystem} '{res.Label}' · {res.Items.Count} itens em {sw.Elapsed.TotalSeconds:0.0}s · {string.Join(" | ", res.Log)}");
            var files = res.Items.Where(i => !i.IsDirectory).ToList();
            int ok = 0, bad = 0;
            foreach (var (rel, h) in live)
            {
                var f = files.FirstOrDefault(x => x.Origin == ItemOrigin.Live && Rel(x) == rel);
                if (f?.Data != null && Hash(f) == h) ok++; else { bad++; if (bad <= 5) Console.WriteLine($"     vivo errado: {rel} ({(f == null ? "não achado" : "hash difere")})"); }
            }
            Check(bad == 0, $"{ok}/{live.Count} arquivos vivos com hash idêntico");
            Check(res.Allocation != null, "mapa de alocação lido (para a varredura profunda só no espaço livre)");
            var del = files.Where(x => x.Origin == ItemOrigin.Deleted).ToList();
            int named = gone.Keys.Count(k => del.Any(d => Rel(d) == k || d.Name == Path.GetFileName(k)));
            int intact = 0, wrong = 0;
            foreach (var d in del.Where(d => d.Data != null && d.Condition is Condition.Good or Condition.Excellent))
            {
                string? want = gone.FirstOrDefault(g => g.Key.EndsWith("/" + d.Name) || g.Key == d.Name).Value;
                if (want == null) continue;
                if (Hash(d) == want) intact++; else wrong++;
            }
            Console.WriteLine($"   apagados: {named}/{gone.Count} achados pelo nome; {intact} com conteúdo íntegro; {wrong} marcados bons com conteúdo errado");
            Check(named >= gone.Count * 8 / 10, "pelo menos 80% dos apagados aparecem com o nome");
            Check(wrong == 0, "nenhum apagado marcado como bom com conteúdo errado");
            if (kind == FsKind.Btrfs) Check(intact >= 1, "Btrfs: apagados voltam íntegros pelas árvores antigas (cópia na escrita)");
        }
        Console.WriteLine(fails == 0 ? "TUDO OK" : $"{fails} FALHA(S)");
        return fails;
    }
}
