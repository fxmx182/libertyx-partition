using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;
using LibertyX.Partition.Core.Recovery;
using LibertyX.Partition.Core.Tools;

namespace LibertyX.Partition;

/// <summary>
/// Autoteste para Windows: LibertyXPartition.exe --selftest relatorio.txt [--vhd]
/// Sem --vhd: só leitura (lista discos, S.M.A.R.T., MFT do C:, VSS, benchmark).
/// Com --vhd: além disso, opera de verdade num disco virtual "Msft Virtual Disk" já anexado
/// (criado pelo script do CI), e nunca em outro disco.
/// </summary>
public static partial class SelfTest
{
    private static readonly StringBuilder Log = new();
    private static int _fails;

    private static void W(string s) { Console.WriteLine(s); Log.AppendLine(s); }
    private static void Check(bool ok, string what) { W((ok ? "OK    " : "FALHA ") + what); if (!ok) _fails++; }
    private static async Task Step(string name, Func<Task> body)
    {
        W($"\n== {name}");
        var sw = Stopwatch.StartNew();
        try { await body(); }
        catch (Exception ex) { W($"FALHA {name}: {ex}"); _fails++; }
        W($"   ({sw.Elapsed.TotalSeconds:0.0}s)");
    }

    public static int Run(string outPath, bool vhd)
    {
        var t = OperatingSystem.IsLinux() ? RunLinuxAsync(vhd) : RunAsync(vhd);
        t.Wait();
        W(_fails == 0 ? "\nRESULTADO: TUDO OK" : $"\nRESULTADO: {_fails} FALHA(S)");
        File.WriteAllText(outPath, Log.ToString(), Encoding.UTF8);
        return _fails;
    }

    private static async Task RunAsync(bool vhd)
    {
        W($"LibertyX Partition autoteste · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {Environment.OSVersion} · admin={DiskSystem.Native.IsElevated}");
        List<DiskInfo> disks = [];
        await Step("Listar discos e volumes", () =>
        {
            disks = DiskSystem.Enumerate();
            foreach (var d in disks)
            {
                W($"Disco {d.Number}: {d.Model} | {d.Bus} | {d.KindText} | {Fmt(d.Size)} | setor {d.SectorSize}/{d.PhysicalSectorSize} | {d.StyleText} | sistema={d.IsSystem} | erro={d.Error}");
                foreach (var w in d.Table.Warnings) W("   aviso: " + w);
                foreach (var p in d.Partitions)
                    W($"   {(p.IsUnallocated ? "livre" : p.Letter ?? "*"),-6} {FsDetect.Name(p.FileSystem),-10} {Fmt(p.Length),10} usado={Fmt(p.Used)} rótulo={p.Label} tipo={p.TypeText} status={p.StatusText} vol={p.VolumePath}");
            }
            Check(disks.Count > 0, "há discos");
            Check(disks.Any(d => d.IsSystem), "disco do sistema identificado");
            var sys = disks.FirstOrDefault(d => d.IsSystem);
            Check(sys?.Partitions.Any(p => p.IsSystemVolume && p.FileSystem == FsKind.Ntfs) == true, "volume do Windows identificado como NTFS");
            return Task.CompletedTask;
        });

        await Step("S.M.A.R.T.", () =>
        {
            foreach (var d in disks)
            {
                var s = DiskSystem.Native.ReadSmart(d);
                W($"Disco {d.Number}: {s.Health} · {s.Source} · {s.Summary} · temp={s.TemperatureC} horas={s.PowerOnHours} atributos={s.Attributes.Count}");
            }
            return Task.CompletedTask;
        });

        await Step("Ler o MFT do volume do Windows", () =>
        {
            var c = disks.SelectMany(d => d.Partitions).First(p => p.IsSystemVolume);
            using var v = DiskSystem.OpenPartition(c);
            var nv = NtfsVolume.Open(v, out var err);
            Check(nv != null, "NTFS do C: aberto direto dos setores " + err);
            var sw = Stopwatch.StartNew();
            var res = FileSystemScanner.Scan(v, new ScanOptions(), null, CancellationToken.None);
            W($"   {res.Items.Count(i => !i.IsDirectory)} arquivos apagados listados em {sw.Elapsed.TotalSeconds:0.0}s; {string.Join(" | ", res.Log)}");
            Check(res.FileSystem == "NTFS" && res.Allocation != null, "varredura do C: com mapa de clusters");
            var u = UsageCalculator.Compute(v, FsKind.Ntfs);
            W($"   uso pelo $Bitmap: {Fmt(u?.used)} · pelo Windows: {Fmt(c.Used)}");
            Check(u != null && c.Used != null && Math.Abs(u.Value.used - c.Used.Value) < c.Length / 20, "uso calculado pelo $Bitmap bate com o do Windows (±5%)");
            return Task.CompletedTask;
        });

        await Step("Instantâneo VSS", async () =>
        {
            var c = disks.SelectMany(d => d.Partitions).First(p => p.IsSystemVolume);
            var (dev, release) = await DiskOperations.OpenSource(c, true, new OpSink());
            try
            {
                Check(dev.Name.Contains("Shadow", StringComparison.OrdinalIgnoreCase), "origem é o instantâneo: " + dev.Name);
                Check(FsDetect.Detect(dev) == FsKind.Ntfs, "instantâneo legível como NTFS");
                W($"   tamanho do instantâneo: {Fmt(dev.Length)}");
            }
            finally { await release(); }
        });

        await Step("PowerShell / Storage", async () =>
        {
            var c = disks.SelectMany(d => d.Partitions).First(p => p.IsSystemVolume);
            var sup = await WinCommands.SupportedSize(c.Disk.Number, c.Offset);
            W($"   C: pode ir de {Fmt(sup?.min)} a {Fmt(sup?.max)}");
            Check(sup != null, "Get-PartitionSupportedSize respondeu");
            var bl = await WinCommands.BitLockerStatus();
            W($"   BitLocker: {bl.Count} volume(s): {string.Join("; ", bl.Select(b => $"{b.Mount} {b.Status}"))}");
        });

        await Step("Benchmark (64 MB, 1 s)", async () =>
        {
            var r = await DiskBenchmark.RunAsync(Path.GetTempPath(), 64 << 20, TimeSpan.FromSeconds(1), true, null, CancellationToken.None);
            foreach (var x in r) W($"   {x.Test.Label} {(x.Test.Write ? "escrita" : "leitura")}: {x.MBps:0} MB/s · {x.Iops:N0} IOPS");
            Check(r.Count == 8 && r.All(x => x.MBps > 0), "8 medições com valor");
        });

        await Step("Analisador e duplicados", () =>
        {
            string dir = Path.Combine(Environment.SystemDirectory, "drivers");
            var n = SpaceAnalyzer.Analyze(dir, null, CancellationToken.None);
            W($"   {dir}: {n.Files} arquivos, {Fmt(n.Size)}");
            Check(n.Files > 10, "analisador leu a pasta");
            var g = DuplicateFinder.Find([dir], 1, null, null, CancellationToken.None);
            W($"   {g.Count} grupos de duplicados");
            return Task.CompletedTask;
        });

        if (vhd) await VhdTests();
    }

    private static DiskInfo VhdDisk()
    {
        var all = DiskSystem.Enumerate();
        var d = all.FirstOrDefault(x => x.Model.Contains("Virtual Disk", StringComparison.OrdinalIgnoreCase) && !x.IsSystem && x.Size <= 2L << 30 && x.Bus is "VHD" or "Virtual" or "Desconhecido" or "SCSI")
             ?? all.FirstOrDefault(x => x.Bus is "VHD" && !x.IsSystem);
        if (d == null) throw new InvalidOperationException("Disco virtual de teste não encontrado: " + string.Join(", ", all.Select(x => $"{x.Number}:{x.Model}/{x.Bus}")));
        return d;
    }

    private static PartitionInfo Part(DiskInfo d, FsKind? fs = null) => d.Partitions.First(p => !p.IsUnallocated && (fs == null || p.FileSystem == fs));

    private static async Task<DiskInfo> Settle(int number, Func<DiskInfo, bool> ready, int seconds = 30)
    {
        for (int i = 0; i < seconds * 2; i++)
        {
            var d = DiskSystem.Enumerate().First(x => x.Number == number);
            if (ready(d)) return d;
            await Task.Delay(500);
        }
        return DiskSystem.Enumerate().First(x => x.Number == number);
    }

    private static Dictionary<string, string> HashTree(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden })
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static async Task VhdTests()
    {
        var sink = new OpSink();
        sink.LogLine += l => W("   · " + l);
        var d = VhdDisk();
        int n = d.Number;
        W($"\nDISCO VIRTUAL DE TESTE: {n} · {d.Model} · {Fmt(d.Size)} · {d.Bus}");
        string letter = "";
        var files = new Dictionary<string, string>();
        var deleted = new Dictionary<string, string>();

        await Step("Plug and play (anexar e remover um segundo disco virtual)", async () =>
        {
            string sig1 = DiskSystem.Signature();
            string vhd2 = Path.Combine(Path.GetTempPath(), "lx-hotplug.vhdx");
            string dp = Path.Combine(Path.GetTempPath(), "lx-dp.txt");
            await File.WriteAllTextAsync(dp, $"create vdisk file=\"{vhd2}\" maximum=64 type=expandable\r\nattach vdisk\r\n");
            var r = await WinCommands.Run("diskpart.exe", $"/s \"{dp}\"");
            await Task.Delay(2000);
            string sig2 = DiskSystem.Signature();
            Check(r.Ok && sig2 != sig1, "disco novo muda a impressão digital (o app relê sozinho)");
            await File.WriteAllTextAsync(dp, $"select vdisk file=\"{vhd2}\"\r\ndetach vdisk\r\n");
            await WinCommands.Run("diskpart.exe", $"/s \"{dp}\"");
            await Task.Delay(2000);
            Check(DiskSystem.Signature() == sig1, "remover volta à impressão digital original");
            try { File.Delete(vhd2); } catch { }
        });

        await Step("VHD: inicializar GPT e criar NTFS", async () =>
        {
            var t = new PartitionTable { Style = TableStyle.Gpt, SectorSize = d.SectorSize, DiskSectors = d.Size / d.SectorSize };
            DiskOperations.WriteTable(d, t, "autoteste", sink);
            d = await Settle(n, x => x.Table.Style == TableStyle.Gpt);
            Check(d.Table.Style == TableStyle.Gpt && d.Table.Entries.Count == 0, "GPT vazia escrita e relida pelo Windows");
            var r = await WinCommands.CreatePartition(n, 1 << 20, 220L << 20, null, null, "NTFS", "LXTESTE", 0, true, CancellationToken.None);
            Check(r.Ok, "New-Partition + Format-Volume " + r.Message);
            d = await Settle(n, x => x.Partitions.Any(p => p.Letter != null));
            letter = Part(d, FsKind.Ntfs).Letter ?? "";
            Check(letter.Length == 2, "partição montada com letra " + letter);
        });

        await Step("VHD: apagar arquivos e recuperar", async () =>
        {
            var rnd = new Random(7);
            Directory.CreateDirectory($@"{letter}\Fotos");
            Directory.CreateDirectory($@"{letter}\Docs\Antigos");
            for (int i = 0; i < 8; i++)
            {
                var data = new byte[rnd.Next(1, 3) * (i % 3 == 0 ? 600 : 300_000)];
                rnd.NextBytes(data);
                string rel = i < 4 ? $@"Fotos\foto{i}.bin" : $@"Docs\Antigos\doc{i}.bin";
                // write-through: sem isso o arquivo fica só no cache e, apagado logo depois, nunca chega ao disco
                using (var fs = new FileStream($@"{letter}\{rel}", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { fs.Write(data); fs.Flush(true); }
                files[rel] = Convert.ToHexString(SHA256.HashData(data));
            }
            File.WriteAllText($@"{letter}\leia-me.txt", "LibertyX autoteste");
            File.Delete($@"{letter}\Fotos\foto1.bin"); deleted["foto1.bin"] = files[@"Fotos\foto1.bin"];
            File.Delete($@"{letter}\Fotos\foto3.bin"); deleted["foto3.bin"] = files[@"Fotos\foto3.bin"];
            Directory.Delete($@"{letter}\Docs\Antigos", true);
            foreach (var k in files.Keys.Where(k => k.StartsWith(@"Docs\Antigos"))) deleted[Path.GetFileName(k)] = files[k];
            foreach (var k in files.Keys.Where(k => deleted.ContainsKey(Path.GetFileName(k))).ToList()) files.Remove(k);
            await WinCommands.PowerShell($"Write-VolumeCache -DriveLetter {letter[0]}");
            await Task.Delay(1500);
            d = DiskSystem.Enumerate().First(x => x.Number == n);
            var p = Part(d, FsKind.Ntfs);
            using var v = DiskSystem.OpenPartition(p);
            var res = FileSystemScanner.Scan(v, new ScanOptions(), null, CancellationToken.None);
            var found = res.Items.Where(i => !i.IsDirectory).ToList();
            W($"   apagados achados: {string.Join(", ", found.Select(f => f.FullPath + " [" + f.Condition + "]"))}");
            string outDir = Path.Combine(Path.GetTempPath(), "lx-recuperados");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            RecoveryExporter.Export(found, outDir, true, null, CancellationToken.None);
            int ok = 0;
            foreach (var f in Directory.EnumerateFiles(outDir, "*.bin", SearchOption.AllDirectories))
                if (deleted.TryGetValue(Path.GetFileName(f), out var h) && h == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))) ok++;
            Check(ok == deleted.Count, $"{ok}/{deleted.Count} apagados recuperados com hash idêntico (no Windows, com o volume montado)");
            Check(found.Any(f => f.FullPath.Contains("Antigos")), "pasta apagada reconstruída");
        });

        await Step("VHD: reduzir (Resize-Partition)", async () =>
        {
            var p = Part(d, FsKind.Ntfs);
            var r = await WinCommands.Resize(n, p.Offset, 160L << 20, CancellationToken.None);
            Check(r.Ok, "reduzida para 160 MB " + r.Message);
            d = await Settle(n, x => Part(x, FsKind.Ntfs).Length == 160L << 20);
            Check(Part(d, FsKind.Ntfs).Length == 160L << 20, "tamanho novo relido");
        });

        await Step("VHD: mover para a direita (volume montado é trancado e desmontado)", async () =>
        {
            var p = Part(d, FsKind.Ntfs);
            long target = p.StartLba + (24L << 20) / d.SectorSize;
            DiskOperations.MovePartition(p, target, sink);
            d = await Settle(n, x => Part(x, FsKind.Ntfs).StartLba == target && Part(x, FsKind.Ntfs).Letter != null);
            var np = Part(d, FsKind.Ntfs);
            Check(np.StartLba == target, "partição no setor novo");
            Check(ViewModels.MainViewModel.FreshPart(p).StartLba == target, "a fila reencontra a partição movida (pelo GUID)");
            letter = np.Letter ?? letter;
            var now = HashTree(letter + "\\");
            Check(files.All(kv => now.TryGetValue(kv.Key, out var h) && h == kv.Value), "arquivos idênticos depois de mover (Windows remontou)");
        });

        await Step("VHD: alinhar (mover para fora do limite de 1 MB e voltar)", async () =>
        {
            var p = Part(d, FsKind.Ntfs);
            DiskOperations.MovePartition(p, p.StartLba + 7, sink);
            d = await Settle(n, x => !DiskOperations.IsAligned(Part(x, FsKind.Ntfs)));
            p = Part(d, FsKind.Ntfs);
            Check(!DiskOperations.IsAligned(p), "desalinhada de propósito");
            var t = DiskOperations.AlignedTarget(p);
            DiskOperations.MovePartition(p, t!.Value, sink);
            d = await Settle(n, x => DiskOperations.IsAligned(Part(x, FsKind.Ntfs)) && Part(x, FsKind.Ntfs).Letter != null);
            letter = Part(d, FsKind.Ntfs).Letter ?? letter;
            var now = HashTree(letter + "\\");
            Check(DiskOperations.IsAligned(Part(d, FsKind.Ntfs)) && files.All(kv => now.TryGetValue(kv.Key, out var h) && h == kv.Value), "realinhada e íntegra");
        });

        await Step("VHD: estender de volta (Resize-Partition)", async () =>
        {
            var p = Part(d, FsKind.Ntfs);
            var r = await WinCommands.Resize(n, p.Offset, p.Length + (20L << 20), CancellationToken.None);
            Check(r.Ok, "estendida +20 MB " + r.Message);
        });

        await Step("VHD: converter GPT → MBR → GPT", async () =>
        {
            d = DiskSystem.Enumerate().First(x => x.Number == n);
            DiskOperations.ConvertStyle(d, TableStyle.Mbr, sink);
            d = await Settle(n, x => x.Table.Style == TableStyle.Mbr);
            Check(d.Table.Style == TableStyle.Mbr && d.Partitions.Any(p => p.FileSystem == FsKind.Ntfs), "agora MBR, NTFS reconhecido");
            DiskOperations.ConvertStyle(d, TableStyle.Gpt, sink);
            d = await Settle(n, x => x.Table.Style == TableStyle.Gpt && x.Partitions.Any(p => p.Letter != null));
            Check(d.Table.Style == TableStyle.Gpt && d.Table.Warnings.Count == 0, "de volta a GPT sem avisos");
            letter = Part(d, FsKind.Ntfs).Letter ?? letter;
            var now = HashTree(letter + "\\");
            Check(files.All(kv => now.TryGetValue(kv.Key, out var h) && h == kv.Value), "arquivos idênticos depois das conversões");
        });

        await Step("VHD: número de série", async () =>
        {
            var p = Part(d, FsKind.Ntfs);
            DiskOperations.ChangeSerial(p, 0x00000000CAFE1234UL, sink);
            d = await Settle(n, x => Part(x, FsKind.Ntfs).VolumeSerial == 0xCAFE1234);
            Check(Part(d, FsKind.Ntfs).VolumeSerial == 0xCAFE1234, $"Windows enxerga a série nova: {Part(d, FsKind.Ntfs).VolumeSerial:X8}");
        });

        await Step("VHD: FAT32 formatado pelo LibertyX", async () =>
        {
            var free = d.Partitions.Last(p => p.IsUnallocated);
            long start = (free.Offset + (1 << 20) - 1) / (1 << 20) * (1 << 20);
            var t = d.Table.Clone();
            var e = new PartitionEntry { StartLba = start / d.SectorSize, SectorCount = (100L << 20) / d.SectorSize, TypeGuid = PartitionTypes.BasicData, Name = "LXFAT" };
            t.Entries.Add(e);
            using (var dev = DiskSystem.OpenDisk(d, true))
                Fat32Formatter.Format(new Core.IO.SubDevice(dev, start, 100L << 20), "LXFAT", 0, e.StartLba);
            DiskOperations.WriteTable(d, t, "autoteste", sink);
            d = await Settle(n, x => x.Partitions.Any(p => p.FileSystem == FsKind.Fat32));
            var r = await WinCommands.AssignTemporaryLetter(n, start);
            string fl = r.Output.Trim();
            Check(r.Ok && fl.Length == 1, "Windows montou o FAT32 com letra " + fl);
            if (fl.Length == 1)
            {
                var di = new DriveInfo(fl);
                W($"   {fl}: {di.DriveFormat} {di.VolumeLabel} {Fmt(di.TotalSize)}");
                Check(di.DriveFormat == "FAT32", "Windows reconhece o FAT32");
                File.WriteAllText($@"{fl}:\ok.txt", "fat32");
                Check(File.ReadAllText($@"{fl}:\ok.txt") == "fat32", "dá para gravar e ler no FAT32");
                var chk = await WinCommands.Chkdsk($"{fl}:", false, false, CancellationToken.None, l => { });
                W($"   chkdsk {fl}: código {chk.ExitCode}");
                Check(chk.ExitCode == 0, "chkdsk não acha erro no FAT32 do LibertyX");
            }
        });

        await Step("VHD: imagem, excluir tudo, achar e restaurar", async () =>
        {
            d = DiskSystem.Enumerate().First(x => x.Number == n);
            string img = Path.Combine(Path.GetTempPath(), "lx-vhd.img");
            await DiskOperations.CreateImage(d, null, img, false, true, 0, sink);
            Check(new FileInfo(img).Length == d.Size, "imagem do disco inteiro criada");
            DiskOperations.DeleteAllPartitions(d, sink);
            d = await Settle(n, x => x.Table.Entries.Count == 0);
            Check(d.Table.Entries.Count == 0, "todas excluídas");
            List<FoundPartition> found;
            using (var dev = DiskSystem.OpenDisk(d, false)) found = PartitionFinder.Scan(dev, false, null, CancellationToken.None);
            W($"   achadas: {string.Join(", ", found.Select(f => $"{FsDetect.Name(f.FileSystem)} '{f.Label}' @{f.StartLba}{(f.BackupConfirmed ? " cópia-ok" : "")}{(f.Conflicting ? " SOBREPOSTA" : "")}"))}");
            var good = found.Where(f => !f.Conflicting).ToList();
            Check(good.Count == 2 && good.Any(f => f.FileSystem == FsKind.Ntfs) && good.Any(f => f.FileSystem == FsKind.Fat32), "busca rápida acha NTFS e FAT32 (restos de volume movido ficam marcados como sobrepostos)");
            DiskOperations.RestoreFound(d, good, TableStyle.Gpt, sink);
            d = await Settle(n, x => x.Partitions.Any(p => p.FileSystem == FsKind.Ntfs && p.Letter != null));
            letter = Part(d, FsKind.Ntfs).Letter ?? letter;
            var now = HashTree(letter + "\\");
            Check(files.All(kv => now.TryGetValue(kv.Key, out var h) && h == kv.Value), "partição restaurada e arquivos íntegros");
            File.Delete(img); File.Delete(img + ".json");
        });

        // O caminho dos botões do app, num disco MBR como o de um cartão SD: criar em dois passos e
        // formatar com planos B — cada plano forçado de propósito, e o Windows tem de reconhecer.
        await Step("VHD: como o app — MBR, criar e formatar (com planos B)", async () =>
        {
            d = DiskSystem.Enumerate().First(x => x.Number == n);
            DiskOperations.Wipe(d, null, WipeMethod.Zeros, sink);
            d = await Settle(n, x => x.Table.Style == TableStyle.None);
            var t = new PartitionTable { Style = TableStyle.Mbr, SectorSize = d.SectorSize, DiskSectors = d.Size / d.SectorSize };
            DiskOperations.WriteTable(d, t, "autoteste", sink);
            d = await Settle(n, x => x.Table.Style == TableStyle.Mbr);
            long mb = 1 << 20;
            // 4 primárias (o MBR não cabe mais sem lógica): os planos B e C são o caminho dos cartões SD
            var cases = new (string fs, int skip, string how)[]
            {
                ("FAT32", 1, "format clássico (plano B)"), ("exFAT", 1, "format clássico (plano B)"),
                ("FAT32", 2, "formatador do LibertyX (plano C)"), ("NTFS", 0, "Format-Volume"),
            };
            long at = mb;
            foreach (var (fs, skip, how) in cases)
            {
                d = DiskSystem.Enumerate().First(x => x.Number == n);
                var np = await ViewModels.ActionsBuilder.CreateOnWindows(d, at, 80 * mb, fs, "LX" + fs, true, sink, skip);
                at += 81 * mb;
                var now = await Settle(n, x => x.Partitions.Any(p => p.Offset == np!.Offset && p.Letter != null));
                var q = now.Partitions.First(p => p.Offset == np!.Offset);
                string drv = "";
                try { drv = q.Letter != null ? new DriveInfo(q.Letter + "\\").DriveFormat : ""; } catch { }
                Check(string.Equals(drv, fs, StringComparison.OrdinalIgnoreCase), $"criar + formatar {fs} por {how}: Windows monta {q.Letter} como {drv}");
                if (q.Letter != null)
                    try { File.WriteAllText($@"{q.Letter}\ok.txt", fs); Check(File.ReadAllText($@"{q.Letter}\ok.txt") == fs, $"{fs} ({how}) grava e lê"); }
                    catch (Exception ex) { Check(false, $"{fs} ({how}) grava e lê: {ex.Message}"); }
            }
            // reformatar uma partição existente (o botão Formatar), trocando o sistema de arquivos
            d = DiskSystem.Enumerate().First(x => x.Number == n);
            var first = d.Partitions.First(p => p.Offset == mb);
            await ViewModels.ActionsBuilder.FormatRobust(first, "exFAT", "REFORMAT", 0, true, sink);
            var re = await Settle(n, x => x.Partitions.Any(p => p.Offset == mb && p.FileSystem == FsKind.ExFat));
            Check(re.Partitions.First(p => p.Offset == mb).FileSystem == FsKind.ExFat, "Formatar (Format-Volume): FAT32 → exFAT na partição existente");
            Check(re.Table.Entries.Count == 4 && re.Table.Style == TableStyle.Mbr, "4 partições no MBR, como o Windows criou");
        });

        await Step("VHD: lista de discos com limite de tempo", async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var all = DiskSystem.Enumerate();
            Check(all.Count >= 2 && sw.Elapsed < TimeSpan.FromSeconds(20), $"{all.Count} discos lidos em {sw.Elapsed.TotalSeconds:0.0}s");
            int v = Bounded.Run(() => { Thread.Sleep(30000); return 1; }, TimeSpan.FromSeconds(1), -1, out bool slow);
            Check(slow && v == -1, "uma consulta presa é abandonada no limite, sem travar o app");
            await Task.CompletedTask;
        });

        // Gravação direta como o balenaEtcher, no disco virtual: imagem compactada (GZIP) com MBR +
        // FAT32 do próprio LibertyX, gravada com verificação; o Windows tem de montar o FAT32.
        await Step("VHD: gravar imagem compactada com verificação (balenaEtcher)", async () =>
        {
            string raw = Path.Combine(Path.GetTempPath(), "lx-flash.img"), gz = raw + ".gz";
            using (var fsr = new FileStream(raw, FileMode.Create)) fsr.SetLength(64L << 20);
            using (var img = new ImageFileDevice(raw, true))
            {
                var t = new PartitionTable { Style = TableStyle.Mbr, SectorSize = 512, DiskSectors = img.Length / 512 };
                t.Entries.Add(new PartitionEntry { StartLba = 2048, SectorCount = img.Length / 512 - 2048, MbrType = 0x0C });
                Fat32Formatter.Format(new SubDevice(img, 2048 * 512, img.Length - 2048 * 512), "ETCHER", 0, 2048, null);
                PartitionCodec.Write(img, t);
            }
            using (var i = File.OpenRead(raw)) using (var o = new System.IO.Compression.GZipStream(File.Create(gz), System.IO.Compression.CompressionLevel.Fastest)) i.CopyTo(o);
            d = DiskSystem.Enumerate().First(x => x.Number == n);
            var lines = new List<string>();
            sink.LogLine += lines.Add;
            BootMedia.WriteImage(ImageSource.Inspect(gz), d, verify: true, sink);
            sink.LogLine -= lines.Add;
            Check(lines.Any(l => l.Contains("Verificado")), "imagem .gz gravada no disco e verificada (SHA-256 relido igual)");
            d = await Settle(n, x => x.Table.Style == TableStyle.Mbr && x.Partitions.Any(p => p.FileSystem == FsKind.Fat32));
            var fp = d.Partitions.FirstOrDefault(p => p.FileSystem == FsKind.Fat32);
            Check(fp != null, "Windows relê a tabela gravada e acha o FAT32");
            if (fp != null)
            {
                var lr = await WinCommands.AssignTemporaryLetter(n, fp.Offset);
                string fl = lr.Output.Trim();
                string fmt = "";
                try { fmt = new DriveInfo(fl + ":\\").DriveFormat; } catch { }
                Check(fmt == "FAT32", $"Windows monta a imagem gravada como {fmt} ({fl}:)");
            }
            File.Delete(raw); File.Delete(gz);
        });

        await Step("atualização: acha a versão publicada", async () =>
        {
            var u = await Updater.Check();
            // sem resposta: limite de consultas do GitHub no IP compartilhado do CI — ambiente, não defeito
            if (u == null) { W("   (GitHub não respondeu à consulta de versão neste servidor — limite de consultas; não conta como falha)"); return; }
            Check(u.Build > 0 && u.SetupUrl.EndsWith("LibertyXPartition-Setup.exe"),
                $"release latest lido: versão {u?.Version}, instalador de {Fmt(u?.SetupSize)} (este build: 1.0.{Updater.CurrentBuild})");
        });

        await Step("VHD: limpar disco", async () =>
        {
            d = DiskSystem.Enumerate().First(x => x.Number == n);
            DiskOperations.Wipe(d, null, WipeMethod.Zeros, sink);
            d = await Settle(n, x => x.Table.Style == TableStyle.None);
            Check(d.Table.Style == TableStyle.None, "disco zerado aparece sem tabela");
        });
    }

    private static string Fmt(long? b) => b == null ? "—" : DiskOperations.Size(b.Value);
}
