using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;
using LibertyX.Partition.Core.Tools;

namespace LibertyX.Partition.Core.Operations;

/// <summary>
/// Pendrive de boot e gravação de imagens (o que o Rufus e o balenaEtcher fazem). Dois caminhos:
/// - Arquivos: tabela MBR (BIOS + UEFI) ou GPT (só UEFI), FAT32, e os arquivos da ISO copiados.
///   Para o Windows, o install.wim acima de 4 GB é dividido em .swm pelo DISM e um
///   autounattend.xml opcional tira as exigências do Windows 11 (TPM, Secure Boot, conta Microsoft).
/// - Gravação direta (modo DD): ISOs híbridas e imagens de disco (também compactadas), com verificação.
/// </summary>
public static class BootMedia
{
    public static bool IsHybrid(string iso)
    {
        using var f = File.OpenRead(iso);
        var b = new byte[512];
        return f.Read(b) == 512 && b[510] == 0x55 && b[511] == 0xAA;
    }

    /// <summary>
    /// Gravação direta, como o balenaEtcher: a imagem (descompactada enquanto grava) vai setor a
    /// setor para o pendrive, com a impressão digital SHA-256 do que foi escrito; com
    /// <paramref name="verify"/>, o pendrive é relido inteiro e comparado — pega cartão
    /// falsificado (que "aceita" mais do que guarda) e gravação corrompida.
    /// </summary>
    public static void WriteImage(ImageSource src, DiskInfo usb, bool verify, OpSink sink)
    {
        if (usb.IsSystem) throw new InvalidOperationException("Esse é o disco do Windows.");
        if (src.Length is { } need && need > usb.Size)
            throw new InvalidOperationException($"A imagem ({DiskOperations.Size(need)}) não cabe em {usb.Title} ({DiskOperations.Size(usb.Size)}).");
        int ss = Math.Max(512, usb.SectorSize);
        const int Chunk = 4 << 20;
        var buf = GC.AllocateUninitializedArray<byte>(Chunk);
        long written = 0;
        byte[] hash;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var dev = DiskSystem.OpenDisk(usb, true))
        using (DiskSystem.Native.LockVolumes(usb))
        using (var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            // zera o fim do disco antes: um GPT reserva de um uso anterior confunde o firmware
            dev.Write(dev.Length - (1 << 20), new byte[1 << 20]);
            using var st = src.Open(out var raw);
            for (;;)
            {
                sink.Token.ThrowIfCancellationRequested();
                int n = ImageSource.ReadFull(st, buf, Chunk);
                if (n <= 0) break;
                if (written + n > dev.Length) throw new InvalidOperationException($"A imagem descompactada passa do tamanho de {usb.Title} ({DiskOperations.Size(dev.Length)}).");
                sha.AppendData(buf, 0, n);
                int w = (n + ss - 1) / ss * ss;
                if (w > n) Array.Clear(buf, n, w - n);
                dev.Write(written, buf.AsSpan(0, w));
                written += n;
                double f = src.Length is { } L && L > 0 ? (double)written / L : (double)raw.Position / Math.Max(1, src.FileLength);
                double speed = written / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                sink.Progress(verify ? f * 0.6 : f, "Gravando", $"{DiskOperations.Size(written)}{(src.Length is { } t ? " de " + DiskOperations.Size(t) : "")} · {DiskOperations.Size((long)speed)}/s");
            }
            dev.Flush();
            hash = sha.GetHashAndReset();
        }
        sink.Log($"Gravados {DiskOperations.Size(written)} em {sw.Elapsed:hh\\:mm\\:ss} · SHA-256 {Convert.ToHexStringLower(hash)}");
        if (verify)
        {
            sink.Log("Verificando: relendo o que foi gravado…");
            var vw = System.Diagnostics.Stopwatch.StartNew();
            using var dev = DiskSystem.OpenDisk(usb, false);
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (long pos = 0; pos < written;)
            {
                sink.Token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(Chunk, written - pos);
                int r = (n + ss - 1) / ss * ss;
                dev.ReadBytes(pos, buf.AsSpan(0, r));
                sha.AppendData(buf, 0, n);
                pos += n;
                sink.Progress(0.6 + 0.4 * pos / written, "Verificando", $"{DiskOperations.Size(pos)} de {DiskOperations.Size(written)} · {DiskOperations.Size((long)(pos / Math.Max(0.001, vw.Elapsed.TotalSeconds)))}/s");
            }
            var back = sha.GetHashAndReset();
            if (!back.AsSpan().SequenceEqual(hash))
                throw new InvalidOperationException("A verificação falhou: o que está no pendrive é diferente do que foi gravado. O pendrive/cartão pode estar com defeito ou ser falsificado (anuncia mais espaço do que tem). Tente outro.");
            sink.Log("Verificado ✓ — o pendrive tem exatamente a imagem.");
        }
        DiskSystem.Native.Rescan(usb);
        sink.Log(src.Kind == ImageKind.DiskImage
            ? "Pronto. O Windows pode pedir para formatar o cartão (partições de Linux) — não formate."
            : "Pronto. O pendrive pode aparecer \"ilegível\" no Windows — é normal para ISOs de Linux.");
    }

    /// <summary>Compatibilidade: grava uma ISO direto, sem verificação.</summary>
    public static void WriteRaw(string iso, DiskInfo usb, OpSink sink) => WriteImage(ImageSource.Inspect(iso), usb, false, sink);

    public static Task WriteWindows(string iso, DiskInfo usb, string label, OpSink sink) => WriteFiles(iso, usb, label, false, true, null, sink);

    /// <summary>
    /// Pendrive FAT32 com os arquivos da ISO. <paramref name="gpt"/>: tabela GPT (só UEFI); senão MBR
    /// ativa (BIOS e UEFI). <paramref name="windows"/>: confere que é instalação do Windows, divide o
    /// install.wim grande e grava o autounattend.xml das <paramref name="opts"/>.
    /// </summary>
    public static async Task WriteFiles(string iso, DiskInfo usb, string label, bool gpt, bool windows, WindowsSetupOptions? opts, OpSink sink)
    {
        if (usb.IsSystem) throw new InvalidOperationException("Esse é o disco do Windows.");
        sink.Progress(-1, "Montando a ISO");
        string? src = await WinCommands.MountIso(iso, sink.Token);
        if (src == null) throw new InvalidOperationException("O Windows não conseguiu montar a ISO.");
        try
        {
            if (windows && !File.Exists(Path.Combine(src + "\\", "sources", "install.wim")) && !File.Exists(Path.Combine(src + "\\", "sources", "install.esd")))
                throw new InvalidOperationException("Esta ISO não parece ser de instalação do Windows (sem sources\\install.wim/esd). Use a gravação direta.");
            var files = new DirectoryInfo(src + "\\").EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            long need = files.Sum(f => f.Length);
            if (need + (64L << 20) > usb.Size) throw new InvalidOperationException($"Os arquivos precisam de {DiskOperations.Size(need)}; o pendrive tem {DiskOperations.Size(usb.Size)}.");
            if (!windows && files.FirstOrDefault(f => f.Length >= 4L << 30) is { } big)
                throw new InvalidOperationException($"{big.Name} tem {DiskOperations.Size(big.Length)} — o FAT32 não guarda arquivos de 4 GB ou mais. Use a gravação direta.");

            int ss = usb.SectorSize;
            long start = 1024 * 1024 / ss;
            long sectors = usb.Size / ss;
            var t = new PartitionTable { Style = gpt ? TableStyle.Gpt : TableStyle.Mbr, SectorSize = ss, DiskSectors = sectors };
            // GPT: deixa o fim livre para a cópia da tabela (34 setores), alinhado a 1 MB
            long count = gpt ? (sectors - 34 - start) / start * start : Math.Min(sectors - start, uint.MaxValue - start);
            t.Entries.Add(gpt
                ? new PartitionEntry { StartLba = start, SectorCount = count, TypeGuid = PartitionTypes.BasicData, UniqueGuid = Guid.NewGuid(), Name = label }
                : new PartitionEntry { StartLba = start, SectorCount = count, MbrType = 0x0C, Active = true });
            sink.Log($"Criando a tabela {(gpt ? "GPT (UEFI)" : "MBR (BIOS e UEFI)")} e formatando em FAT32…");
            using (var dev = DiskSystem.OpenDisk(usb, true))
            using (DiskSystem.Native.LockVolumes(usb))
            {
                dev.Write(0, new byte[1 << 20]);
                Fat32Formatter.Format(new SubDevice(dev, start * ss, count * ss), label, 0, start, new Progress<double>(x => sink.Progress(x * 0.05, "Formatando FAT32")));
                PartitionCodec.Write(dev, t);
                DiskSystem.Native.Rescan(usb);
            }
            DiskSystem.Native.Rescan(usb);
            await Task.Delay(3000, sink.Token);
            var lr = await WinCommands.AssignTemporaryLetter(usb.Number, start * ss);
            string letter = lr.Output.Trim();
            if (!lr.Ok || letter.Length != 1) throw new InvalidOperationException("O Windows não montou o pendrive novo: " + lr.Message);
            string dst = letter + ":";

            string wim = Path.Combine(src + "\\", "sources", "install.wim");
            bool split = File.Exists(wim) && new FileInfo(wim).Length >= (4L << 30) - 1;
            sink.Log($"Copiando os arquivos da instalação para {dst}…");
            sink.Progress(-1, "Copiando arquivos");
            var r = split
                ? await WinCommands.RobocopyExcluding(src + "\\", dst + "\\", "install.wim", sink.Token, l => { if (l.Contains('%')) return; if (l.Trim().Length > 0) sink.Log(l.Trim()); })
                : await WinCommands.Robocopy(src + "\\", dst + "\\", sink.Token, l => { if (l.Trim().Length > 0) sink.Log(l.Trim()); });
            if (r.ExitCode >= 8) throw new InvalidOperationException($"A cópia falhou (robocopy {r.ExitCode}).");
            if (split)
            {
                sink.Log("install.wim passa de 4 GB (limite do FAT32): dividindo em partes .swm com o DISM…");
                sink.Progress(-1, "Dividindo install.wim");
                var d = await WinCommands.SplitWim(wim, Path.Combine(dst + "\\", "sources", "install.swm"), sink.Token, l => { if (l.Trim().Length > 0 && !l.Contains('%')) sink.Log(l.Trim()); });
                if (!d.Ok) throw new InvalidOperationException("O DISM não conseguiu dividir o install.wim: " + d.Message);
            }
            if (windows && opts is { Any: true })
            {
                File.WriteAllText(Path.Combine(dst + "\\", "autounattend.xml"), Unattend.Build(opts), new System.Text.UTF8Encoding(false));
                sink.Log("autounattend.xml gravado: " + opts.Summary);
            }
            sink.Log(gpt ? "Pendrive pronto: inicia em computadores UEFI." : "Pendrive pronto: inicia em computadores UEFI e BIOS.");
        }
        finally { await WinCommands.DismountIso(iso); }
    }
}
