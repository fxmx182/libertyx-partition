using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Tools;

/// <summary>Detector de LUKS: o que sobra depois de formatar por cima, e o apagamento que não estraga o que é de hoje.</summary>
public static class LuksTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK   " : "  FALHA ") + what); if (!ok) fails++; }
    const long M = 1 << 20;

    static string Hash(IBlockDevice dev, PartitionInfo p, List<(long off, long len)> used)
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var (o, l) in used) h.AppendData(dev.ReadBytes(p.Offset + o, (int)l));
        return Convert.ToHexString(h.GetHashAndReset());
    }

    public static int Run(string dir)
    {
        string src = Path.Combine(dir, "luks", "luks.img");
        if (!File.Exists(src)) { Console.WriteLine("\n(sem imagem LUKS — rode tests/scripts/make-luks.sh)"); return 0; }
        Console.WriteLine("\nDetector de LUKS");
        string img = Path.Combine(Path.GetTempPath(), $"lx-luks-{Guid.NewGuid():N}.img");
        File.Copy(src, img);
        try
        {
            List<LuksFinding> quick, full;
            DiskInfo d;
            using (var dev = new ImageFileDevice(img))
            {
                d = new DiskInfo { Number = 901, IsImage = true, ImagePath = img, DevicePath = img, Size = dev.Length };
                LayoutBuilder.Build(d, dev);
                var layout = LuksFinder.Layout(d, dev.Length);
                quick = LuksFinder.Find(dev, layout, 0, dev.Length, LuksScanMode.Quick, null, default);
                full = LuksFinder.Find(dev, layout, 0, dev.Length, LuksScanMode.Full, null, default, a => LuksFinder.UsedRanges(dev.Slice(a.Offset, a.Length)));
            }
            foreach (var f in full) Console.WriteLine($"    {f.Start / M,4} MiB {f.Title} [{f.StateText}] {f.Where}: {f.Explain()}  cópias={string.Join(",", f.Headers.Select(h => $"{h.Key - f.Start}:{(h.Value ? "ok" : "ruim")}"))} chaves={string.Join(",", f.Slots.Select(s => $"{s.Index}:{s.Damaged}/{s.Sectors}"))}");
            LuksFinding? At(List<LuksFinding> l, long mib) => l.FirstOrDefault(f => f.Start == mib * M);
            var cofre = full.FirstOrDefault(f => f.Label == "LXARQUIVO");
            Check(full.Count == 6, $"completa: os 6 LUKS do disco ({full.Count})");
            Check(At(full, 1) is { State: LuksState.Active, Version: 2, Label: "LXVIVO", IntactSlots: 1, PrimaryOk: true, SecondaryOk: true }, "p1: LUKS2 em uso, as duas cópias e a chave inteiras");
            Check(!full.Any(f => f.Label == "LXNTFS"), "p2: o mkfs.ntfs destruiu o LUKS de antes (o cryptsetup também não abre)");
            Check(At(full, 101) is { State: LuksState.Openable, Label: "LXSOLTO" } fs && fs.Where.Contains("NTFS"), "cabeçalho + chave soltos no espaço livre do NTFS: ainda abre");
            Check(cofre is { State: LuksState.InFile } && cofre.Where.Contains("Ext"), "arquivo-contêiner dentro do ext4: achado e reconhecido como arquivo de hoje");
            Check(At(full, 190) is { Version: 1, State: LuksState.Openable, IntactSlots: 1 } f4 && !f4.Area!.IsPartition, "LUKS1 no espaço não alocado, ainda abre com a senha");
            Check(At(full, 215) is { Version: 2, State: LuksState.Dead, IntactSlots: 0, PrimaryOk: true }, "LUKS2 com a chave zerada: não abre mais");
            Check(At(full, 240) is { Version: 2, State: LuksState.Openable, PrimaryOk: false, SecondaryOk: true }, "LUKS2 sem o cabeçalho principal: a reserva basta (o cryptsetup abre)");
            Check(quick.Count == 1 && quick[0].Start == M, $"rápida: só olha o começo das partições e dos espaços livres ({quick.Count})");

            // apagar: nada que é de hoje pode mudar
            using (var dev = new ImageFileDevice(img, true))
            {
                var layout = LuksFinder.Layout(d, dev.Length);
                var parts = d.Partitions.Where(p => p.FileSystem is FsKind.Ntfs or FsKind.Ext).ToList();
                var usedBefore = parts.ToDictionary(p => p.Offset, p => Hash(dev, p, LuksFinder.UsedRanges(dev.Slice(p.Offset, p.Length))!));
                LuksEraseResult Er(LuksFinding f) => LuksFinder.Erase(dev, layout, f, a => LuksFinder.UsedRanges(dev.Slice(a.Offset, a.Length)), _ => { }, default);
                var r1 = Er(At(full, 101)!);
                Check(r1.Written > 0 && r1.After == null, $"solto no NTFS: apagado ({r1.Written / 1024} KiB), sem sobra");
                var r2 = Er(cofre!);
                Check(r2.Written == 0 && r2.Skipped.Count > 0 && r2.After is { State: LuksState.InFile }, "arquivo-contêiner do ext4: não foi tocado (é um arquivo de hoje)");
                foreach (var mib in new long[] { 190, 215, 240 })
                {
                    var r = Er(At(full, mib)!);
                    Check(r.After == null && r.Skipped.Count == 0, $"espaço livre a {mib} MiB: apagado sem sobra");
                }
                var r6 = Er(At(full, 1)!);
                Check(r6.After == null, "contêiner em uso: destruído");
                Check(parts.All(p => Hash(dev, p, LuksFinder.UsedRanges(dev.Slice(p.Offset, p.Length))!) == usedBefore[p.Offset]), "NTFS e ext4: nenhum byte em uso mudou");
                var rest = LuksFinder.Find(dev, layout, 0, dev.Length, LuksScanMode.Full, null, default, a => LuksFinder.UsedRanges(dev.Slice(a.Offset, a.Length)));
                Check(rest.Count == 1 && rest[0].Label == "LXARQUIVO", $"varredura depois: só sobrou o arquivo-contêiner ({rest.Count})");
            }
            if (Environment.GetEnvironmentVariable("LX_LUKS_KEEP") is { Length: > 0 } keep) File.Copy(img, keep, true);
        }
        finally { File.Delete(img); }
        Console.WriteLine(fails == 0 ? "LUKS OK" : $"LUKS: {fails} FALHAS");
        return fails;
    }
}
