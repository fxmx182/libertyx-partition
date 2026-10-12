using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

/// <summary>Discos de Mac: HFS+/HFSX gravados pelo driver do Linux e APFS gravado numa VM.</summary>
public static class MacTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }

    public static int Run(string dir)
    {
        Hfs(dir);
        Apfs(dir);
        return fails;
    }

    public static int Dump(string img)
    {
        var info = new DiskInfo { Number = 399, IsImage = true, ImagePath = img };
        using var dev = new ImageFileDevice(img);
        info.Size = dev.Length;
        LayoutBuilder.Build(info, dev);
        foreach (var p in info.Partitions.Where(x => !x.IsUnallocated))
        {
            Console.WriteLine($"{p.FileSystem} {p.FsVariant} '{p.Label}' usado={p.Used} livre={p.Free}");
            var res = FileSystemScanner.Scan(dev.Slice(p.Offset, p.Length), new ScanOptions { IncludeLive = true }, null, CancellationToken.None);
            foreach (var l in res.Log) Console.WriteLine("  log: " + l);
            foreach (var i in res.Items.Take(80)) Console.WriteLine($"  {(i.IsDirectory ? "D" : "F")} {i.Origin} {i.Condition} {i.Size} {i.FullPath} {i.Note}");
            Console.WriteLine($"  total {res.Items.Count}");
        }
        return 0;
    }

    /// <summary>Lê todos os arquivos e devolve caminho relativo → (hash, origem, condição, nota).</summary>
    internal static Dictionary<string, (string hash, ItemOrigin origin, Condition cond, string? note)> Collect(ScanResult res)
    {
        var got = new Dictionary<string, (string, ItemOrigin, Condition, string?)>();
        foreach (var f in res.Items.Where(i => !i.IsDirectory))
        {
            string rel = string.Join('/', f.FullPath.Split('\\').Skip(1));
            string h = "";
            if (f.Data != null)
            {
                var b = new byte[f.Size];
                f.Data.Read(0, b);
                h = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));
            }
            got[rel] = (h, f.Origin, f.Condition, f.Note);
        }
        return got;
    }

    static void Hfs(string dir)
    {
        string img = Path.Combine(dir, "hfs.img");
        if (!File.Exists(img)) { Console.WriteLine("\n(sem imagem HFS+ — rode tests/scripts/make-hfs.sh)"); return; }
        Console.WriteLine("\nHFS+ / HFSX");
        var man = File.ReadAllLines(Path.Combine(dir, "hfs-manifest.txt")).Select(l => l.Split('|')).ToList();
        var deletedRel = new[] { "Fotos/praia.jpg", "Documentos/contrato.pdf", "Video/clipe.mp4", "Projetos/codigo/main.py" };
        var info = new DiskInfo { Number = 310, IsImage = true, ImagePath = img };
        using var dev = new ImageFileDevice(img);
        info.Size = dev.Length;
        LayoutBuilder.Build(info, dev);
        var parts = info.Partitions.Where(x => x.FileSystem == FsKind.HfsPlus).ToList();
        Check(parts.Count == 2, "2 volumes HFS+ reconhecidos na tabela");
        Check(parts.Select(p => p.FsVariant).SequenceEqual(["HFS+", "HFSX"]) && parts.All(p => p.Used > 0 && p.Free > 0) && parts[0].Label == "Mac_hfsplus",
            $"painel mostra variante, rótulo e uso ({string.Join(", ", parts.Select(p => $"{p.FsVariant} {p.Label} {p.Used / 1024}K"))})");
        foreach (var p in parts)
        {
            var vol = dev.Slice(p.Offset, p.Length);
            var res = FileSystemScanner.Scan(vol, new ScanOptions { IncludeLive = true }, null, CancellationToken.None);
            string fs = res.FileSystem == "HFSX" ? "hfsx" : "hfsplus";
            var exp = man.Where(m => m[0] == fs).ToDictionary(m => m[1], m => m[2]);
            var got = Collect(res);
            int liveOk = exp.Count(kv => !deletedRel.Contains(kv.Key) && got.TryGetValue(kv.Key, out var g) && g.hash == kv.Value && g.origin == ItemOrigin.Live);
            int liveTotal = exp.Count(kv => !deletedRel.Contains(kv.Key));
            Check(liveOk == liveTotal, $"{res.FileSystem}: {liveOk}/{liveTotal} arquivos existentes lidos com hash idêntico (acentos, japonês, fragmentado)");
            foreach (var kv in exp.Where(kv => !deletedRel.Contains(kv.Key)))
                if (!got.TryGetValue(kv.Key, out var g) || g.hash != kv.Value) Console.WriteLine($"           faltou/diferente: {kv.Key} {(got.ContainsKey(kv.Key) ? got[kv.Key].note : "ausente")}");
            var frag = res.Items.FirstOrDefault(i => i.Name == "grande.bin")?.Data as ExtentSource;
            Check(frag != null && frag.Extents.Count > 8 && got["Fragmentado/grande.bin"].hash == exp["Fragmentado/grande.bin"],
                $"{res.FileSystem}: arquivo em {frag?.Extents.Count} pedaços (além dos 8 do registro, lidos do arquivo de extents) íntegro");
            int delOk = deletedRel.Count(r => got.TryGetValue(r, out var g) && g.origin == ItemOrigin.Deleted && g.hash == exp[r]);
            var lies = got.Where(kv => kv.Value.origin == ItemOrigin.Deleted && kv.Value.cond is Condition.Good or Condition.Excellent && exp.TryGetValue(kv.Key, out var h) && h != kv.Value.hash).Select(kv => kv.Key).ToList();
            Check(delOk >= 1 && lies.Count == 0, $"{res.FileSystem}: apagado da sobra do catálogo recuperado íntegro, e nenhum marcado como bom com conteúdo errado");
            Console.WriteLine($"         {res.FileSystem}: {delOk}/4 apagados recuperados com hash idêntico · " + string.Join(" | ", res.Log));
            foreach (var r in deletedRel) if (got.TryGetValue(r, out var g)) Console.WriteLine($"           {r}: {g.cond} {(g.hash == exp[r] ? "íntegro" : "diferente")} {g.note}");
            foreach (var kv in got.Where(k => k.Value.origin == ItemOrigin.Deleted && !deletedRel.Contains(k.Key))) Console.WriteLine($"           (outro apagado) {kv.Key}: {kv.Value.cond}");
        }
    }

    static void Apfs(string dir)
    {
        string img = Path.Combine(dir, "apfs.img");
        if (!File.Exists(img)) { Console.WriteLine("\n(sem imagem APFS — rode tests/scripts/make-apfs.sh)"); return; }
        Console.WriteLine("\nAPFS");
        var man = File.ReadAllLines(Path.Combine(dir, "apfs-manifest.txt")).Select(l => l.Split('|')).Where(l => l.Length == 3).ToList();
        var deletedRel = new[] { "Fotos/praia.jpg", "Documentos/contrato.pdf", "Video/clipe.mp4", "Projetos/codigo/main.py" };
        var info = new DiskInfo { Number = 311, IsImage = true, ImagePath = img };
        using var dev = new ImageFileDevice(img);
        info.Size = dev.Length;
        LayoutBuilder.Build(info, dev);
        var p = info.Partitions.FirstOrDefault(x => x.FileSystem == FsKind.Apfs);
        Check(p != null && p.FsVariant == "APFS" && p.Label == "LXAPFS" && p.Used > 5_000_000 && p.Free > 300_000_000,
            $"contêiner APFS no painel com nome do volume e uso ({p?.FsVariant} {p?.Label} {p?.Used / 1024}K)");
        if (p == null) return;
        var vol = dev.Slice(p.Offset, p.Length);
        var exp = man.ToDictionary(m => m[1], m => m[2]);
        foreach (var deep in new[] { false, true })
        {
            var res = FileSystemScanner.Scan(vol, new ScanOptions { IncludeLive = true, DeepScan = deep }, null, CancellationToken.None);
            var got = Collect(res);
            string mode = deep ? "profunda" : "rápida";
            if (!deep)
            {
                int liveOk = exp.Count(kv => !deletedRel.Contains(kv.Key) && got.TryGetValue(kv.Key, out var g) && g.hash == kv.Value && g.origin == ItemOrigin.Live);
                int liveTotal = exp.Count(kv => !deletedRel.Contains(kv.Key));
                Check(liveOk == liveTotal, $"{liveOk}/{liveTotal} arquivos existentes lidos com hash idêntico (acentos, japonês, fragmentado, vazio)");
                foreach (var kv in exp.Where(kv => !deletedRel.Contains(kv.Key)))
                    if (!got.TryGetValue(kv.Key, out var g) || g.hash != kv.Value) Console.WriteLine($"           faltou/diferente: {kv.Key} {(got.ContainsKey(kv.Key) ? got[kv.Key].note : "ausente")}");
                var frag = res.Items.FirstOrDefault(i => i.Name == "grande.bin")?.Data as ExtentSource;
                Console.WriteLine($"         grande.bin em {frag?.Extents.Count(e => !e.Sparse)} pedaços");
            }
            int delFound = deletedRel.Count(r => got.TryGetValue(r, out var g) && g.origin == ItemOrigin.Deleted);
            int delOk = deletedRel.Count(r => got.TryGetValue(r, out var g) && g.origin == ItemOrigin.Deleted && g.hash == exp[r]);
            Console.WriteLine($"         {mode}: {delFound}/4 apagados listados, {delOk}/4 com hash idêntico · " + string.Join(" | ", res.Log));
            foreach (var r in deletedRel) if (got.TryGetValue(r, out var g)) Console.WriteLine($"           {r}: {g.cond} {(g.hash == exp[r] ? "íntegro" : "diferente")} {g.note}");
            // o que sobrou no disco tem de voltar íntegro; o que não sobrou não pode ser vendido como bom
            var lies = res.Items.Where(i => !i.IsDirectory && i.Origin == ItemOrigin.Deleted && i.Condition is Condition.Good or Condition.Excellent)
                .Select(i => string.Join('/', i.FullPath.Split('\\').Skip(1))).Where(rel => exp.ContainsKey(rel) && got[rel].hash != exp[rel]).ToList();
            Check(lies.Count == 0, $"varredura {mode}: nenhum apagado marcado como bom com conteúdo errado {string.Join(", ", lies)}");
            if (deep) Check(delOk >= 1, $"varredura profunda: apagado achado em nó antigo solto da árvore, com nome, pasta e hash idêntico ({delOk}/4)");
        }
    }
}
