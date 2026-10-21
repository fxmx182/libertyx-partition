using System.Diagnostics;
using System.Security.Cryptography;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Linux;
using LibertyX.Partition.Core.Recovery;
using LibertyX.Partition.Core.Tools;
using LibertyX.Partition.ViewModels;

namespace LibertyX.Partition;

/// <summary>
/// Autoteste no Linux: LibertyXPartition --selftest relatorio.txt [--loop]
/// Sem --loop: só leitura (discos, S.M.A.R.T., sistema de arquivos da raiz, benchmark).
/// Com --loop: cria um arquivo, liga num /dev/loopN com partições (losetup -P) e opera de
/// verdade SÓ nele — conferido pelo arquivo por trás do loop antes de cada escrita.
/// </summary>
public static partial class SelfTest
{
    private static string? _loopDev, _loopFile;

    private static async Task RunLinuxAsync(bool loop)
    {
        W($"LibertyX Partition autoteste (Linux) · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {LinuxTools.Current.Name} · kernel {Environment.OSVersion.Version} · root={DiskSystem.Native.IsElevated}");
        List<DiskInfo> disks = [];
        await Step("Listar discos e volumes", () =>
        {
            disks = DiskSystem.Enumerate();
            foreach (var d in disks)
            {
                W($"{d.Title}: {d.Model} | {d.Bus} | {d.KindText} | {Fmt(d.Size)} | setor {d.SectorSize}/{d.PhysicalSectorSize} | {d.StyleText} | sistema={d.IsSystem} | ro={d.ReadOnly} | erro={d.Error}");
                foreach (var w in d.Table.Warnings) W("   aviso: " + w);
                foreach (var p in d.Partitions)
                    W($"   {(p.IsUnallocated ? "livre" : p.KernelName ?? "*"),-12} {FsDetect.Name(p.FileSystem),-10} {Fmt(p.Length),10} usado={Fmt(p.Used)} rótulo={p.Label} uuid={p.FsUuid} montada=[{string.Join(",", p.MountPoints)}] {p.StatusText}");
            }
            Check(disks.Count > 0, "há discos");
            bool rootOnDisk = LinuxDiskSystem.ReadMounts().LastOrDefault(m => m.MountPoint == "/")?.Source.StartsWith("/dev/") == true;
            if (rootOnDisk)
            {
                Check(disks.Any(d => d.IsSystem), "disco do sistema identificado");
                Check(disks.SelectMany(d => d.Partitions).Any(p => p.IsSystemVolume && p.IsSystemCritical), "partição do / marcada como em uso (travada)");
            }
            else W("   (raiz num sistema de arquivos virtual — contêiner: sem disco do sistema para identificar)");
            return Task.CompletedTask;
        });

        await Step("S.M.A.R.T.", () =>
        {
            foreach (var d in disks.Where(x => !x.IsVirtual))
            {
                var s = DiskSystem.Native.ReadSmart(d);
                W($"{d.Title}: {s.Health} · {s.Source} · {s.Summary} · temp={s.TemperatureC} horas={s.PowerOnHours} atributos={s.Attributes.Count}");
            }
            return Task.CompletedTask;
        });

        await Step("Ler o sistema de arquivos da raiz direto dos setores", () =>
        {
            var root = disks.SelectMany(d => d.Partitions).FirstOrDefault(p => p.IsSystemVolume);
            if (root == null) { W("   (sem raiz num disco)"); return Task.CompletedTask; }
            using var v = DiskSystem.OpenPartition(root);
            W($"   {root.DeviceNode}: {FsDetect.Name(FsDetect.Detect(v))} {Fmt(v.Length)}");
            if (root.FileSystem == FsKind.Ext)
            {
                var u = UsageCalculator.Compute(v, FsKind.Ext);
                W($"   uso pelo superbloco: {Fmt(u?.used)} · pelo kernel (statvfs): {Fmt(root.Used)}");
                Check(u != null && root.Used != null && Math.Abs(u.Value.used - root.Used.Value) < root.Length / 10, "uso pelo superbloco bate com o do kernel (±10%, o ext reserva blocos)");
            }
            return Task.CompletedTask;
        });

        await Step("Ferramentas do sistema", () =>
        {
            foreach (var g in LinuxTools.Catalog.GroupBy(t => t.Group))
                W($"   {g.Key}: {string.Join(", ", g.Select(t => t.Exe + (LinuxTools.Has(t.Exe) ? "" : " (falta)")))}");
            W("   instalar o que falta: " + LinuxTools.InstallHint(LinuxTools.Catalog.Where(t => !LinuxTools.Has(t.Exe)).Select(t => t.Exe)));
            return Task.CompletedTask;
        });

        await Step("Benchmark (64 MB, 1 s)", async () =>
        {
            var r = await DiskBenchmark.RunAsync(Path.GetTempPath(), 64 << 20, TimeSpan.FromSeconds(1), true, null, CancellationToken.None);
            foreach (var x in r) W($"   {x.Test.Label} {(x.Test.Write ? "escrita" : "leitura")}: {x.MBps:0} MB/s · {x.Iops:N0} IOPS");
            Check(r.Count == 8 && r.All(x => x.MBps > 0), "8 medições com valor");
        });

        await Step("Analisador e duplicados", () =>
        {
            string dir = Directory.Exists("/usr/share/doc") ? "/usr/share/doc" : "/usr/lib";
            var n = SpaceAnalyzer.Analyze(dir, null, CancellationToken.None);
            W($"   {dir}: {n.Files} arquivos, {Fmt(n.Size)}");
            Check(n.Files > 10, "analisador leu a pasta");
            var g = DuplicateFinder.Find([dir], 1, null, null, CancellationToken.None);
            W($"   {g.Count} grupos de duplicados");
            return Task.CompletedTask;
        });

        if (loop)
        {
            try { await LoopTests(); }
            finally { DetachLoop(); }
        }
    }

    // ───────── discos extras (clone) ─────────

    private static readonly List<(string dev, string file)> _extra = [];

    private static (string dev, string file) TempLoop(long bytes)
    {
        string file = _loopFile + $".{_extra.Count + 2}";
        using (var f = new FileStream(file, FileMode.Create)) f.SetLength(bytes);
        string dev = Loop("-f", "--show", "-P", file);
        _extra.Add((dev, file));
        return (dev, file);
    }

    /// <summary>Um disco extra de teste — conferido como o principal: é loop e aponta para o nosso arquivo.</summary>
    private static DiskInfo TempDisk(string dev, string file)
    {
        var d = DiskSystem.Enumerate().FirstOrDefault(x => x.DevicePath == dev) ?? throw new InvalidOperationException($"{dev} sumiu da lista de discos.");
        string back = File.ReadAllText($"/sys/block/{Path.GetFileName(dev)}/loop/backing_file").Trim();
        if (!d.KernelName.StartsWith("loop") || Path.GetFullPath(back) != Path.GetFullPath(file))
            throw new InvalidOperationException($"Recusado: {d.DevicePath} não é o disco de teste ({back}).");
        return d;
    }

    private static async Task<DiskInfo> SettleTemp(string dev, string file, Func<DiskInfo, bool> ready)
    {
        for (int i = 0; i < 80; i++)
        {
            var d = TempDisk(dev, file);
            if (ready(d)) return d;
            await Task.Delay(250);
        }
        return TempDisk(dev, file);
    }

    private static void DropLoop(string dev, string file)
    {
        foreach (var m in LinuxDiskSystem.ReadMounts().Where(m => m.Source.StartsWith(dev)).OrderByDescending(m => m.MountPoint.Length))
            Proc.RunSync("umount", ["-l", m.MountPoint], TimeSpan.FromSeconds(20));
        Proc.RunSync("losetup", ["-d", dev], TimeSpan.FromSeconds(20));
        try { File.Delete(file); } catch { }
        _extra.Remove((dev, file));
    }

    /// <summary>O disco principal de teste com GPT nova e um ext4 cheio de arquivos no começo.</summary>
    private static async Task<DiskInfo> FreshExtDisk(long len, string label, Dictionary<string, string> files, OpSink sink)
    {
        const long MB = 1 << 20;
        var d = LoopDisk();
        DiskOperations.WriteTable(d, new PartitionTable { Style = TableStyle.Gpt, SectorSize = d.SectorSize, DiskSectors = d.Size / d.SectorSize }, "autoteste", sink);
        d = await SettleLoop(x => x.Table.Style == TableStyle.Gpt && x.Table.Entries.Count == 0);
        await ActionsBuilder.CreateLinux(d, MB, len, "ext4", label, sink);
        d = await SettleLoop(x => x.Partitions.Any(p => p.Offset == MB && p.FileSystem == FsKind.Ext && p.DeviceNode != null));
        var p = PartAt(d, MB);
        string mp = await MountAt(p, sink);
        WriteTestFiles(mp, 30, 11, files, "clone");
        await LinuxFs.Unmount(MainViewModel.FreshPart(p), sink);
        return await SettleLoop(x => x.Partitions.Any(q => q.Offset == MB && !q.IsMounted));
    }

    // ───────── o disco de teste ─────────

    private static string Loop(params string[] a) { var r = Proc.RunSync("losetup", a, TimeSpan.FromSeconds(30)); return r.Ok ? r.Output.Trim() : throw new InvalidOperationException("losetup: " + r.Message); }

    private static void DetachLoop()
    {
        try
        {
            if (_loopDev != null)
            {
                foreach (var m in LinuxDiskSystem.ReadMounts().Where(m => m.Source.StartsWith(_loopDev)).OrderByDescending(m => m.MountPoint.Length))
                    Proc.RunSync("umount", ["-l", m.MountPoint], TimeSpan.FromSeconds(20));
                foreach (var f in Directory.GetFiles("/dev/mapper").Where(f => Path.GetFileName(f).StartsWith("luks-") || Path.GetFileName(f).StartsWith("libertyx-")))
                    if (LinuxDiskSystem.SlavesOf(LinuxDiskSystem.KernelNameOf(f) ?? "").Any(x => x.StartsWith(Path.GetFileName(_loopDev))))
                        Proc.RunSync("cryptsetup", ["close", Path.GetFileName(f)], TimeSpan.FromSeconds(20));
                Proc.RunSync("losetup", ["-d", _loopDev], TimeSpan.FromSeconds(20));
            }
        }
        catch { }
        foreach (var (dev, file) in _extra.ToList()) try { DropLoop(dev, file); } catch { }
        try { if (_loopFile != null) File.Delete(_loopFile); } catch { }
    }

    /// <summary>O disco de teste — sempre conferido: é loop e aponta para o nosso arquivo.</summary>
    private static DiskInfo LoopDisk()
    {
        var d = DiskSystem.Enumerate().FirstOrDefault(x => x.DevicePath == _loopDev)
            ?? throw new InvalidOperationException($"{_loopDev} sumiu da lista de discos.");
        string back = File.ReadAllText($"/sys/block/{Path.GetFileName(_loopDev)}/loop/backing_file").Trim();
        if (!d.KernelName.StartsWith("loop") || Path.GetFullPath(back) != Path.GetFullPath(_loopFile!))
            throw new InvalidOperationException($"Recusado: {d.DevicePath} não é o disco de teste ({back}).");
        return d;
    }

    private static async Task<DiskInfo> SettleLoop(Func<DiskInfo, bool> ready, int seconds = 20)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            var d = LoopDisk();
            if (ready(d)) return d;
            await Task.Delay(250);
        }
        return LoopDisk();
    }

    private static PartitionInfo PartAt(DiskInfo d, long offset) => d.Partitions.First(p => !p.IsUnallocated && p.Offset == offset);

    private static string WriteTestFiles(string dir, int count, int seed, Dictionary<string, string> into, string prefix)
    {
        var rnd = new Random(seed);
        Directory.CreateDirectory(Path.Combine(dir, prefix));
        for (int i = 0; i < count; i++)
        {
            var data = new byte[rnd.Next(1, 4) * (i % 3 == 0 ? 700 : 200_000)];
            rnd.NextBytes(data);
            string rel = Path.Combine(prefix, $"arq{i}.bin");
            using (var fs = new FileStream(Path.Combine(dir, rel), FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { fs.Write(data); fs.Flush(true); }
            into[rel] = Convert.ToHexString(SHA256.HashData(data));
        }
        Proc.RunSync("sync", [], TimeSpan.FromSeconds(30));
        return dir;
    }

    private static bool SameFiles(string root, Dictionary<string, string> files) =>
        files.All(kv => File.Exists(Path.Combine(root, kv.Key)) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, kv.Key)))) == kv.Value);

    private static async Task<string> MountAt(PartitionInfo p, OpSink s, bool ro = false)
    {
        string mp = Path.Combine("/run/libertyx-teste", p.KernelName!);
        Directory.CreateDirectory(mp);
        return await LinuxFs.Mount(p, mp, ro, s);
    }

    private static async Task LoopTests()
    {
        if (!DiskSystem.Native.IsElevated) { W("FALHA --loop precisa de root"); _fails++; return; }
        Environment.SetEnvironmentVariable("LIBERTYX_LOOPS", "1");
        var sink = new OpSink();
        sink.LogLine += l => W("   · " + l);
        const long MB = 1 << 20;

        _loopFile = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? "/var/tmp", $"lx-teste-{Environment.ProcessId}.img");
        using (var f = new FileStream(_loopFile, FileMode.Create)) f.SetLength(1536 * MB);
        _loopDev = Loop("-f", "--show", "-P", _loopFile);
        W($"\nDISCO DE TESTE: {_loopDev} → {_loopFile} (1,5 GB)");
        var d = LoopDisk();
        var files = new Dictionary<string, string>();
        long extStart = MB, extLen = 400 * MB;

        await Step("Plug and play (um segundo loop entra e sai)", async () =>
        {
            string sig1 = DiskSystem.Signature();
            string f2 = _loopFile + ".2";
            using (var f = new FileStream(f2, FileMode.Create)) f.SetLength(64 * MB);
            string dev2 = Loop("-f", "--show", f2);
            await Task.Delay(500);
            Check(DiskSystem.Signature() != sig1, "disco novo muda a impressão digital (o app relê sozinho)");
            Loop("-d", dev2);
            File.Delete(f2);
            await Task.Delay(500);
            Check(DiskSystem.Signature() == sig1, "remover volta à impressão digital original");
        });

        await Step("Inicializar GPT e criar ext4 (o caminho do botão Criar partição)", async () =>
        {
            DiskOperations.WriteTable(d, new PartitionTable { Style = TableStyle.Gpt, SectorSize = d.SectorSize, DiskSectors = d.Size / d.SectorSize }, "autoteste", sink);
            d = await SettleLoop(x => x.Table.Style == TableStyle.Gpt);
            Check(d.Table.Style == TableStyle.Gpt && d.Table.Entries.Count == 0, "GPT vazia escrita e relida");
            string? dev = await ActionsBuilder.CreateLinux(d, extStart, extLen, "ext4", "LXTESTE", sink);
            d = await SettleLoop(x => x.Partitions.Any(p => p.FileSystem == FsKind.Ext && p.DeviceNode != null));
            var p = PartAt(d, extStart);
            Check(dev != null && p.DeviceNode == dev && p.FileSystem == FsKind.Ext && p.FsVariant == "ext4", $"partição {dev} criada, kernel avisado, ext4 reconhecido pelo motor");
            Check(p.Label == "LXTESTE", "rótulo lido do superbloco: " + p.Label);
            Check(p.Entry?.TypeGuid == PartitionTypes.LinuxFs, "tipo GPT Linux");
        });

        await Step("ext4: gravar, apagar e recuperar pelo journal", async () =>
        {
            var p = PartAt(d, extStart);
            string mp = await MountAt(p, sink);
            WriteTestFiles(mp, 6, 11, files, "Fotos");
            var gone = new Dictionary<string, string>();
            WriteTestFiles(mp, 4, 12, gone, "Apagar");
            Proc.RunSync("sync", [], TimeSpan.FromSeconds(30));
            // o journal só guarda o antigo se o bloco foi reescrito depois de ir para o disco: força um commit no meio
            await Task.Delay(6000);
            foreach (var k in gone.Keys) File.Delete(Path.Combine(mp, k));
            Directory.Delete(Path.Combine(mp, "Apagar"));
            Proc.RunSync("sync", [], TimeSpan.FromSeconds(30));
            await LinuxFs.Unmount(MainViewModel.FreshPart(p), sink);
            using var v = DiskSystem.OpenPartition(MainViewModel.FreshPart(p));
            var res = FileSystemScanner.Scan(v, new ScanOptions(), null, CancellationToken.None);
            var found = res.Items.Where(i => !i.IsDirectory).ToList();
            W($"   apagados achados: {string.Join(", ", found.Select(f => f.FullPath + " [" + f.Condition + "]"))}");
            string outDir = Path.Combine(Path.GetTempPath(), "lx-recuperados");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            RecoveryExporter.Export(found, outDir, true, null, CancellationToken.None);
            var byHash = gone.Values.ToHashSet();
            int ok = Directory.Exists(outDir) ? Directory.EnumerateFiles(outDir, "*.bin", SearchOption.AllDirectories).Count(f => byHash.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))) : 0;
            W($"   {ok}/{gone.Count} recuperados com hash idêntico");
            Check(found.Count > 0, "o journal do ext4 devolveu entradas apagadas");
            Check(ok >= 1, "pelo menos um arquivo apagado volta íntegro (o kernel ≥5.14 zera o mapa de blocos; só o journal guarda)");
        });

        await Step("ext4: reduzir (e2fsck + resize2fs + tabela)", async () =>
        {
            var p = PartAt(d, extStart);
            long min = await LinuxFs.MinSize(p) ?? 0;
            W($"   mínimo estimado pelo resize2fs: {Fmt(min)}");
            Check(min > 0 && min < p.Length, "tamanho mínimo calculado");
            await ActionsBuilder.ApplyGeometryLinux(p, p.Offset, 300 * MB, sink);
            d = await SettleLoop(x => PartAt(x, extStart).Length == 300 * MB);
            p = PartAt(d, extStart);
            FsDetect.ExtInfo? sb;
            using (var pv = DiskSystem.OpenPartition(p)) sb = FsDetect.ParseExt(pv.ReadBytes(1024, 1024));
            Check(p.Length == 300 * MB && sb != null && sb.Blocks * sb.BlockSize <= 300 * MB, $"partição e ext4 com 300 MB ({Fmt(sb?.Blocks * sb?.BlockSize)})");
            string mp = await MountAt(p, sink, ro: true);
            Check(SameFiles(mp, files), "arquivos íntegros depois de reduzir");
            await LinuxFs.Unmount(MainViewModel.FreshPart(p), sink);
        });

        await Step("ext4: mover para a direita (motor próprio)", async () =>
        {
            var p = PartAt(d, extStart);
            long target = p.StartLba + 32 * MB / d.SectorSize;
            DiskOperations.MovePartition(p, target, sink);
            extStart = target * d.SectorSize;
            d = await SettleLoop(x => x.Partitions.Any(q => q.Offset == extStart && q.DeviceNode != null));
            var np = PartAt(d, extStart);
            Check(np.StartLba == target, "partição no setor novo e o kernel a vê");
            Check(MainViewModel.FreshPart(p).StartLba == target, "a fila reencontra a partição movida (pelo GUID)");
            string mp = await MountAt(np, sink, ro: true);
            Check(SameFiles(mp, files), "arquivos idênticos depois de mover");
            await LinuxFs.Unmount(MainViewModel.FreshPart(np), sink);
        });

        await Step("ext4: crescer COM ELA MONTADA (o kernel ajusta sem desmontar)", async () =>
        {
            var p = PartAt(d, extStart);
            string mp = await MountAt(p, sink);
            p = MainViewModel.FreshPart(p);
            long before = new DriveInfo(mp).TotalSize;
            await ActionsBuilder.ApplyGeometryLinux(p, p.Offset, 360 * MB, sink);
            d = await SettleLoop(x => PartAt(x, extStart).Length == 360 * MB);
            long after = new DriveInfo(mp).TotalSize;
            W($"   {mp}: {Fmt(before)} → {Fmt(after)}");
            Check(PartAt(d, extStart).IsMounted && after > before + 40 * MB, "cresceu online e continua montada no mesmo lugar");
            Check(SameFiles(mp, files), "arquivos íntegros");
            await LinuxFs.Unmount(MainViewModel.FreshPart(PartAt(d, extStart)), sink);
        });

        await Step("ext4: rótulo, UUID e verificação", async () =>
        {
            var p = PartAt(d, extStart);
            await LinuxFs.SetLabel(p, "NOVO", sink);
            string old = p.FsUuid ?? "";
            await LinuxFs.SetUuid(MainViewModel.FreshPart(p), null, sink);
            d = await SettleLoop(x => PartAt(x, extStart).Label == "NOVO");
            p = PartAt(d, extStart);
            Check(p.Label == "NOVO", "rótulo novo lido");
            Check(p.FsUuid != null && p.FsUuid != old, $"UUID trocado: {old} → {p.FsUuid}");
            string msg = await LinuxFs.Check(p, LinuxFs.CheckMode.ReadOnly, sink);
            Check(msg.StartsWith("Nenhum problema"), "e2fsck -n: " + msg);
        });

        // um de cada: cria pela mesma rotina do botão, o motor reconhece, monta, grava, lê e o fsck aprova
        long at = 440 * MB;
        var made = new List<(string key, long ofs)>();
        foreach (var (key, size) in new[] { ("xfs", 320L), ("btrfs", 160L), ("ntfs", 48L), ("exfat", 48L), ("fat32", 64L), ("f2fs", 80L), ("swap", 32L) })
        {
            var spec = LinuxFs.Formats.First(f => f.Key == key);
            if (!LinuxTools.Has(spec.Mkfs) && key != "fat32") { W($"\n== {spec.Name}: {spec.Mkfs} não instalado — pulado"); continue; }
            long ofs = at;
            at += (size + 1) * MB;
            await Step($"{spec.Name}: criar, reconhecer, montar, gravar e verificar", async () =>
            {
                d = LoopDisk();
                await ActionsBuilder.CreateLinux(d, ofs, size * MB, key, "LX" + key.ToUpperInvariant(), sink);
                d = await SettleLoop(x => x.Partitions.Any(p => p.Offset == ofs && p.DeviceNode != null && p.FileSystem == spec.Kind));
                var p = PartAt(d, ofs);
                Check(p.FileSystem == spec.Kind, $"motor reconhece {FsDetect.Name(p.FileSystem)} (rótulo {p.Label}, uuid {p.FsUuid})");
                made.Add((key, ofs));
                if (key == "swap") return;
                string mp;
                try { mp = await MountAt(p, sink); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("não tem suporte"))
                {
                    W($"   (o kernel deste computador não monta {spec.Name}: {ex.Message.Split('—')[0].Trim()} — ambiente, não falha do app)");
                    return;
                }
                var mine = new Dictionary<string, string>();
                WriteTestFiles(mp, 3, ofs.GetHashCode(), mine, "teste");
                Check(SameFiles(mp, mine), $"{spec.Name} grava e lê");
                long total = new DriveInfo(mp).TotalSize;
                await LinuxFs.Unmount(MainViewModel.FreshPart(p), sink);
                p = MainViewModel.FreshPart(p);
                string msg = await LinuxFs.Check(p, LinuxFs.CheckMode.ReadOnly, sink);
                Check(msg.StartsWith("Nenhum") || msg.StartsWith("Estrutura básica") || msg.StartsWith("Verificação concluída"), $"verificação de {spec.Name}: {msg}");
                if (spec.Kind is FsKind.Xfs or FsKind.Btrfs)
                {
                    FsDetect.SuperInfo? u;
                    using (var pv = DiskSystem.OpenPartition(p)) u = FsDetect.ReadSuper(pv, spec.Kind);
                    Check(u?.Total is > 0 && Math.Abs(u.Total.Value - total) < total / 5 + 16 * MB, $"tamanho pelo superbloco ({Fmt(u?.Total)}) bate com o do kernel ({Fmt(total)})");
                }
            });
        }

        await Step("LUKS2: criar, abrir, montar, gravar, trancar, cópia do cabeçalho", async () =>
        {
            if (!LinuxTools.Has("cryptsetup")) { W("   (cryptsetup não instalado — pulado)"); return; }
            long ofs = at; at += 81 * MB;
            d = LoopDisk();
            await ActionsBuilder.CreateLinux(d, ofs, 80 * MB, "luks", "COFRE", sink, "senha-de-teste-123");
            d = await SettleLoop(x => x.Partitions.Any(p => p.Offset == ofs && p.FileSystem == FsKind.Luks));
            var v = LinuxCrypto.List([d]).FirstOrDefault(x => x.Part.Offset == ofs);
            Check(v is { IsOpen: false, Version: "LUKS2" }, "volume LUKS2 listado e trancado");
            bool wrong = false;
            try { await LinuxCrypto.Open(v!, "senha-errada", null, false, sink); } catch (InvalidOperationException ex) { wrong = ex.Message.Contains("incorreta"); }
            Check(wrong, "senha errada é recusada com mensagem clara");
            string dev = await LinuxCrypto.Open(v!, "senha-de-teste-123", null, false, sink);
            d = await SettleLoop(x => x.Partitions.Any(p => p.Offset == ofs && p.HeldBy != null));
            Check(PartAt(d, ofs).HeldBy?.Contains("LUKS aberto") == true, "partição aparece como LUKS aberto");
            var inner = DiskSystem.Enumerate().FirstOrDefault(x => x.IsVirtual && x.VirtualKind == "LUKS" && x.DevicePath == "/dev/" + LinuxDiskSystem.KernelNameOf(dev));
            Check(inner?.Partitions.FirstOrDefault()?.FileSystem == FsKind.Ext, "conteúdo decifrado aparece como volume ext4 na lista de discos");
            string folder = Path.Combine(Path.GetTempPath(), "lx-cabecalho");
            Directory.CreateDirectory(folder);
            string hdr = await LinuxCrypto.BackupHeader(LinuxCrypto.List([LoopDisk()]).First(x => x.Part.Offset == ofs), folder, sink);
            Check(new FileInfo(hdr).Length >= 1 << 20, "cópia do cabeçalho salva");
            await LinuxCrypto.Close(LinuxCrypto.List([LoopDisk()]).First(x => x.Part.Offset == ofs), sink);
            Check(LinuxCrypto.List([LoopDisk()]).First(x => x.Part.Offset == ofs).IsOpen == false, "trancado de novo");
            Directory.Delete(folder, true);
        });

        await Step("Sinalizadores, nome GPT e tipo (só a tabela, sem desmontar)", async () =>
        {
            d = LoopDisk();
            var p = PartAt(d, extStart);
            var t = d.Table.Clone();
            var e = t.Entries.First(x => x.StartLba == p.Entry!.StartLba);
            e.Name = "raiz-teste";
            e.TypeGuid = PartitionTypes.LinuxRootX64;
            e.Attributes |= PartitionTypes.AttrLegacyBoot;
            var before = d.Partitions.Where(x => x.Entry != null).ToDictionary(x => x.Offset, x => x.Entry!.Slot);
            DiskOperations.WriteTable(d, t, "autoteste", sink);
            d = await SettleLoop(x => PartAt(x, extStart).Entry?.Name == "raiz-teste" && x.Partitions.Where(q => q.Entry != null).All(q => q.DeviceNode != null));
            var ne = PartAt(d, extStart).Entry!;
            Check(ne.Name == "raiz-teste" && ne.TypeGuid == PartitionTypes.LinuxRootX64 && (ne.Attributes & PartitionTypes.AttrLegacyBoot) != 0, "nome, tipo e legacy_boot gravados");
            var lost = d.Partitions.Where(x => x.Entry != null && x.DeviceNode == null).ToList();
            foreach (var x in lost) W($"   sem /dev: slot {x.Entry!.Slot} em {Fmt(x.Offset)} ({x.HeldBy})");
            Check(lost.Count == 0, "todas as partições continuam com /dev");
            Check(d.Partitions.Where(x => x.Entry != null).All(x => before.TryGetValue(x.Offset, out var sl) && sl == x.Entry!.Slot), "numeração preservada (cada partição no mesmo número)");
        });

        await Step("Imagem, excluir tudo, achar e restaurar", async () =>
        {
            d = LoopDisk();
            int before = d.Table.Entries.Count;
            var kinds = d.Partitions.Where(p => p.Entry != null).Select(p => p.FileSystem).ToList();
            string img = Path.Combine(Path.GetTempPath(), "lx-loop.img");
            await DiskOperations.CreateImage(d, null, img, false, true, 0, sink);
            Check(new FileInfo(img).Length == d.Size, "imagem do disco inteiro criada (com SHA-256)");
            DiskOperations.DeleteAllPartitions(d, sink);
            d = await SettleLoop(x => x.Table.Entries.Count == 0);
            Check(d.Table.Entries.Count == 0 && !d.Partitions.Any(p => p.DeviceNode != null && !p.IsUnallocated), "todas excluídas e o kernel removeu os /dev");
            List<FoundPartition> found;
            using (var dev = DiskSystem.OpenDisk(d, false)) found = PartitionFinder.Scan(dev, false, null, CancellationToken.None);
            W($"   achadas: {string.Join(", ", found.Select(f => $"{FsDetect.Name(f.FileSystem)} '{f.Label}' @{f.StartLba}{(f.Conflicting ? " SOBREPOSTA" : "")}"))}");
            var good = found.Where(f => !f.Conflicting).ToList();
            foreach (var k in kinds.Distinct().Where(k => k is FsKind.Ext or FsKind.Xfs or FsKind.Btrfs or FsKind.Ntfs or FsKind.ExFat or FsKind.Fat32 or FsKind.F2fs or FsKind.LinuxSwap or FsKind.Luks))
                Check(good.Any(f => f.FileSystem == k), $"busca rápida acha {FsDetect.Name(k)}");
            DiskOperations.RestoreFound(d, good, TableStyle.Gpt, sink);
            d = await SettleLoop(x => x.Partitions.Any(p => p.FileSystem == FsKind.Ext && p.DeviceNode != null));
            var ext = d.Partitions.First(p => p.FileSystem == FsKind.Ext && p.Entry != null);
            string mp = await MountAt(ext, sink, ro: true);
            Check(SameFiles(mp, files), "partição ext4 restaurada e arquivos íntegros");
            await LinuxFs.Unmount(MainViewModel.FreshPart(ext), sink);
            File.Delete(img); File.Delete(img + ".json");
        });

        await Step("Converter GPT → MBR → GPT (só a tabela)", async () =>
        {
            d = LoopDisk();
            // MBR comporta 4: deixa só ext4 + 3
            var keep = d.Partitions.Where(p => p.Entry != null).OrderBy(p => p.Offset).ToList();
            if (keep.Count > 4)
            {
                var t = d.Table.Clone();
                t.Entries.RemoveAll(e => !keep.Take(4).Any(k => k.Entry!.StartLba == e.StartLba));
                DiskOperations.WriteTable(d, t, "autoteste", sink);
                d = await SettleLoop(x => x.Table.Entries.Count == 4);
            }
            DiskOperations.ConvertStyle(d, TableStyle.Mbr, sink);
            d = await SettleLoop(x => x.Table.Style == TableStyle.Mbr && x.Partitions.Any(p => p.FileSystem == FsKind.Ext && p.DeviceNode != null));
            Check(d.Table.Style == TableStyle.Mbr && d.Partitions.Any(p => p.FileSystem == FsKind.Ext && p.DeviceNode != null), "agora MBR, ext4 com /dev");
            DiskOperations.ConvertStyle(d, TableStyle.Gpt, sink);
            d = await SettleLoop(x => x.Table.Style == TableStyle.Gpt && x.Partitions.Any(p => p.FileSystem == FsKind.Ext && p.DeviceNode != null));
            Check(d.Table.Style == TableStyle.Gpt && d.Table.Warnings.Count == 0, "de volta a GPT sem avisos");
            var ext = d.Partitions.First(p => p.FileSystem == FsKind.Ext);
            string mp = await MountAt(ext, sink, ro: true);
            Check(SameFiles(mp, files), "arquivos idênticos depois das conversões");
            await LinuxFs.Unmount(MainViewModel.FreshPart(ext), sink);
        });

        await Step("Formatar por cima: ext4 → exFAT → ext4 (tipo da tabela acompanha)", async () =>
        {
            d = LoopDisk();
            var p = d.Partitions.First(x => x.FileSystem == FsKind.Ext && x.Entry != null);
            await ActionsBuilder.FormatLinuxCore(p, "exfat", "TROCA", 0, true, null, sink);
            d = await SettleLoop(x => x.Partitions.Any(q => q.Offset == p.Offset && q.FileSystem == FsKind.ExFat));
            var q = PartAt(d, p.Offset);
            Check(q.FileSystem == FsKind.ExFat && q.Entry?.TypeGuid == PartitionTypes.BasicData, "exFAT e tipo GPT Dados básicos");
            await ActionsBuilder.FormatLinuxCore(q, "ext4", "VOLTA", 0, true, null, sink);
            d = await SettleLoop(x => x.Partitions.Any(r => r.Offset == p.Offset && r.FileSystem == FsKind.Ext));
            Check(PartAt(d, p.Offset).Entry?.TypeGuid == PartitionTypes.LinuxFs, "ext4 e tipo GPT Linux de novo");
        });

        await Step("Lista de discos com limite de tempo", async () =>
        {
            var sw = Stopwatch.StartNew();
            var all = DiskSystem.Enumerate();
            Check(all.Count >= 1 && sw.Elapsed < TimeSpan.FromSeconds(20), $"{all.Count} discos lidos em {sw.Elapsed.TotalSeconds:0.0}s");
            int v = Bounded.Run(() => { Thread.Sleep(30000); return 1; }, TimeSpan.FromSeconds(1), -1, out bool slow);
            Check(slow && v == -1, "uma consulta presa é abandonada no limite, sem travar o app");
            await Task.CompletedTask;
        });

        await Step("Pendrive de instalação do Windows (arquivos e autounattend, como o Rufus)", async () =>
        {
            string? mk = LinuxTools.Find("genisoimage") ?? LinuxTools.Find("xorriso");
            if (mk == null) { W("   (sem genisoimage/xorriso para montar a ISO de teste — pulado)"); return; }
            string dir = Path.Combine(Path.GetTempPath(), "lx-win"), iso = Path.Combine(Path.GetTempPath(), "lx-win.iso");
            Directory.CreateDirectory(Path.Combine(dir, "sources"));
            Directory.CreateDirectory(Path.Combine(dir, "efi", "boot"));
            File.WriteAllText(Path.Combine(dir, "bootmgr"), "bootmgr");
            File.WriteAllBytes(Path.Combine(dir, "efi", "boot", "bootx64.efi"), new byte[64 * 1024]);
            var wim = new byte[2_000_000]; "MSWIM\0\0\0"u8.CopyTo(wim);
            File.WriteAllBytes(Path.Combine(dir, "sources", "install.wim"), wim);
            var r = mk.EndsWith("xorriso") ? await Proc.Run(mk, ["-as", "mkisofs", "-quiet", "-J", "-R", "-V", "WIN11_PTBR", "-o", iso, dir])
                                           : await Proc.Run(mk, ["-quiet", "-J", "-R", "-V", "WIN11_PTBR", "-o", iso, dir]);
            Check(r.Ok, "ISO de teste montada " + r.Message);
            d = LoopDisk();
            await BootMedia.WriteFiles(iso, d, "WIN11", false, true, new WindowsSetupOptions(true, true, "Teste", true, true), sink);
            d = await SettleLoop(x => x.Table.Style == TableStyle.Mbr && x.Partitions.Any(p => p.FileSystem == FsKind.Fat32 && p.DeviceNode != null));
            var fp = d.Partitions.FirstOrDefault(p => p.FileSystem == FsKind.Fat32 && p.DeviceNode != null);
            Check(fp != null && fp.Entry?.Active == true, "MBR com a partição FAT32 ativa (BIOS e UEFI)");
            if (fp != null)
            {
                string mp = await MountAt(fp, sink, ro: true);
                Check(File.Exists(Path.Combine(mp, "bootmgr")) && File.Exists(Path.Combine(mp, "sources", "install.wim")) && File.Exists(Path.Combine(mp, "efi", "boot", "bootx64.efi")), "arquivos da instalação copiados");
                string un = File.Exists(Path.Combine(mp, "autounattend.xml")) ? File.ReadAllText(Path.Combine(mp, "autounattend.xml")) : "";
                Check(un.Contains("BypassTPMCheck") && un.Contains("Teste"), "autounattend.xml do Windows 11 gravado (sem TPM, conta local)");
                await LinuxFs.Unmount(MainViewModel.FreshPart(fp), sink);
            }
            Directory.Delete(dir, true); File.Delete(iso);
        });

        await Step("Gravar imagem compactada com verificação (balenaEtcher)", async () =>
        {
            string raw = Path.Combine(Path.GetTempPath(), "lx-flash.img"), gz = raw + ".gz";
            using (var fsr = new FileStream(raw, FileMode.Create)) fsr.SetLength(64 * MB);
            using (var img = new ImageFileDevice(raw, true))
            {
                var t = new PartitionTable { Style = TableStyle.Mbr, SectorSize = 512, DiskSectors = img.Length / 512 };
                t.Entries.Add(new PartitionEntry { StartLba = 2048, SectorCount = img.Length / 512 - 2048, MbrType = 0x0C });
                Fat32Formatter.Format(new SubDevice(img, 2048 * 512, img.Length - 2048 * 512), "ETCHER", 0, 2048, null);
                PartitionCodec.Write(img, t);
            }
            using (var i = File.OpenRead(raw)) using (var o = new System.IO.Compression.GZipStream(File.Create(gz), System.IO.Compression.CompressionLevel.Fastest)) i.CopyTo(o);
            d = LoopDisk();
            var lines = new List<string>();
            sink.LogLine += lines.Add;
            BootMedia.WriteImage(ImageSource.Inspect(gz), d, verify: true, sink);
            sink.LogLine -= lines.Add;
            Check(lines.Any(l => l.Contains("Verificado")), "imagem .gz gravada e verificada (SHA-256 relido igual)");
            d = await SettleLoop(x => x.Table.Style == TableStyle.Mbr && x.Partitions.Any(p => p.FileSystem == FsKind.Fat32 && p.DeviceNode != null));
            var fp = d.Partitions.FirstOrDefault(p => p.FileSystem == FsKind.Fat32);
            Check(fp?.DeviceNode != null, "kernel relê a tabela gravada e cria o /dev do FAT32");
            if (fp?.DeviceNode != null)
            {
                string mp = await MountAt(fp, sink);
                File.WriteAllText(Path.Combine(mp, "ok.txt"), "etcher");
                Check(File.ReadAllText(Path.Combine(mp, "ok.txt")) == "etcher", "o FAT32 gravado monta e aceita escrita");
                await LinuxFs.Unmount(MainViewModel.FreshPart(fp), sink);
                if (LinuxTools.Has("fsck.fat"))
                {
                    var r = await Proc.Run("fsck.fat", ["-n", fp.DeviceNode]);
                    Check(r.ExitCode == 0, "fsck.fat não acha erro no FAT32 do LibertyX");
                }
            }
            File.Delete(raw); File.Delete(gz);
        });

        await Step("Copiar disco Linux para um maior: ids mantidos e a partição cresce (como o Windows faz)", async () =>
        {
            d = await FreshExtDisk(700 * MB, "CLONE", files = new(), sink);
            var src = PartAt(d, MB);
            var (dev2, file2) = TempLoop(1024 * MB);
            try
            {
                var t = TempDisk(dev2, file2);
                var plan = DiskOperations.PlanClone(d, t, false, fitToTarget: true);
                Check(plan.Deficit == 0 && plan.Grow?.Offset == MB, $"plano: cabe, e o ext4 cresce {Fmt(plan.GrowBy)}");
                await DiskOperations.Clone(plan, usedOnly: true, useVss: true, makeBootable: false, sink);
                await ActionsBuilder.GrowAfterClone(plan, sink);
                t = await SettleTemp(dev2, file2, x => x.Partitions.Any(q => q.FileSystem == FsKind.Ext && q.DeviceNode != null));
                var q = PartAt(t, MB);
                Check(t.Table.DiskGuid == d.Table.DiskGuid && q.Entry?.UniqueGuid == src.Entry?.UniqueGuid, "mesmo GUID do disco e PARTUUID (root=PARTUUID= e fstab continuam valendo no clone)");
                Check(q.FsUuid == src.FsUuid, "mesmo UUID do ext4");
                Check(q.Length > 900 * MB, $"partição cresceu até o fim do disco novo ({Fmt(q.Length)})");
                using (var v = DiskSystem.OpenPartition(q))
                {
                    var sb = FsDetect.ParseExt(v.ReadBytes(1024, 1024));
                    Check(sb != null && sb.Blocks * sb.BlockSize > q.Length - 16 * MB, $"o ext4 ocupa a partição inteira ({Fmt(sb?.Blocks * sb?.BlockSize)})");
                }
                var fsck = await Proc.Run("e2fsck", ["-fn", q.DeviceNode!]);
                Check(fsck.ExitCode == 0, "e2fsck -fn limpo no clone");
                string mp = await MountAt(q, sink, ro: true);
                Check(SameFiles(mp, files), "arquivos idênticos no clone");
                await LinuxFs.Unmount(MainViewModel.FreshPart(q), sink);
            }
            finally { DropLoop(dev2, file2); }
        });

        await Step("Copiar disco para um menor: reduz a origem sem perder dados e copia", async () =>
        {
            d = await FreshExtDisk(900 * MB, "MENOR", files = new(), sink);
            var (dev2, file2) = TempLoop(600 * MB);
            try
            {
                var t = TempDisk(dev2, file2);
                var plan = DiskOperations.PlanClone(d, t, false, fitToTarget: true);
                Check(plan.Deficit > 0 && plan.ShrinkCandidate?.Offset == MB, $"plano: não cabe (faltam {Fmt(plan.Deficit)}) e oferece reduzir o ext4");
                var p = PartAt(d, MB);
                long newSize = (p.Length - plan.Deficit - 32 * MB) / MB * MB;
                await ActionsBuilder.ApplyGeometryLinux(p, p.Offset, newSize, sink);
                d = await SettleLoop(x => x.Partitions.Any(q => q.Offset == MB && q.Length == newSize && q.DeviceNode != null));
                plan = DiskOperations.PlanClone(d, TempDisk(dev2, file2), false, fitToTarget: true);
                Check(plan.Deficit == 0, $"depois de reduzir para {Fmt(newSize)}, cabe");
                await DiskOperations.Clone(plan, usedOnly: true, useVss: false, makeBootable: false, sink);
                if (plan.Grow != null) await ActionsBuilder.GrowAfterClone(plan, sink);
                t = await SettleTemp(dev2, file2, x => x.Partitions.Any(q => q.FileSystem == FsKind.Ext && q.DeviceNode != null));
                var q = PartAt(t, MB);
                Check((await Proc.Run("e2fsck", ["-fn", q.DeviceNode!])).ExitCode == 0, "e2fsck -fn limpo no clone menor");
                string mp = await MountAt(q, sink, ro: true);
                Check(SameFiles(mp, files), "arquivos idênticos no disco menor");
                await LinuxFs.Unmount(MainViewModel.FreshPart(q), sink);
            }
            finally { DropLoop(dev2, file2); }
        });

        await Step("Cópia consistente de partição montada (só leitura durante a cópia)", async () =>
        {
            d = LoopDisk();
            var p = PartAt(d, MB);
            string mp = await MountAt(p, sink);
            p = MainViewModel.FreshPart(p);
            var (dev, release) = await DiskOperations.OpenSource(p, true, sink);
            bool ro = LinuxDiskSystem.ReadMounts().First(m => m.MountPoint == mp).Options.Split(',').Contains("ro");
            bool blocked; try { File.WriteAllText(Path.Combine(mp, "x.txt"), "x"); blocked = false; } catch { blocked = true; }
            dev.Dispose();
            await release();
            bool rw = LinuxDiskSystem.ReadMounts().First(m => m.MountPoint == mp).Options.Split(',').Contains("rw");
            Check(ro && blocked, "durante a cópia a partição fica só leitura (nada muda no meio)");
            Check(rw, "no fim volta a aceitar gravação");
            await LinuxFs.Unmount(MainViewModel.FreshPart(p), sink);
        });

        await Step("Cópia consistente de volume LVM em uso (instantâneo do LVM, o VSS do Linux)", async () =>
        {
            if (!LinuxTools.Has("lvm")) { W("   (sem lvm2 — pulado)"); return; }
            var (dev2, file2) = TempLoop(512 * MB);
            string vg = $"lxvg{Guid.NewGuid().ToString("N")[..6]}";
            string? mp = null;
            try
            {
                TempDisk(dev2, file2);
                Check((await Proc.Run("pvcreate", ["-ff", "-y", dev2])).Ok && (await Proc.Run("vgcreate", [vg, dev2])).Ok
                      && (await Proc.Run("lvcreate", ["-y", "-L", "200M", "-n", "dados", vg])).Ok, "grupo e volume LVM criados");
                KernelSync.Settle();
                Check((await Proc.Run("mkfs.ext4", ["-q", "-L", "LVMTESTE", $"/dev/{vg}/dados"])).Ok, "ext4 no volume");
                mp = Path.Combine("/run/libertyx-teste", vg);
                Directory.CreateDirectory(mp);
                Check((await Proc.Run("mount", [$"/dev/{vg}/dados", mp])).Ok, "volume montado (em uso)");
                var lvFiles = new Dictionary<string, string>();
                WriteTestFiles(mp, 12, 21, lvFiles, "lv");
                var lvDisk = DiskSystem.Enumerate().First(x => x.VirtualKind == "LVM" && x.Partitions.Any(q => q.MountPoints.Contains(mp)));
                var part = lvDisk.Partitions.First(q => q.MountPoints.Contains(mp));
                var log = new List<string>();
                sink.LogLine += log.Add;
                var (sdev, release) = await DiskOperations.OpenSource(part, true, sink);
                sink.LogLine -= log.Add;
                bool hasSnap = (await Proc.Run("lvs", ["--noheadings", "-o", "lv_name", vg])).Output.Contains("lx-snap-");
                // grava no volume de verdade durante a cópia: o instantâneo não pode ver
                File.WriteAllBytes(Path.Combine(mp, "depois.bin"), new byte[4 << 20]);
                Proc.RunSync("sync", [], TimeSpan.FromSeconds(20));
                string img = Path.Combine(Path.GetTempPath(), $"lx-lv-{Environment.ProcessId}.img");
                using (var o = ImageFileDevice.Create(img, sdev.Length, 512)) SectorCopier.Copy(sdev, o, new CopyOptions { Ranges = [(0, sdev.Length)] }, null, CancellationToken.None, null);
                sdev.Dispose();
                await release();
                bool gone = !(await Proc.Run("lvs", ["--noheadings", "-o", "lv_name", vg])).Output.Contains("lx-snap-");
                Check(hasSnap && log.Any(l => l.Contains("Instantâneo pronto")), "instantâneo LVM criado para a cópia");
                Check(gone, "instantâneo apagado no fim");
                Check((await Proc.Run("e2fsck", ["-fn", img])).ExitCode == 0, "a cópia do volume em uso passa no e2fsck -fn (consistente)");
                string imp = mp + "-img";
                Directory.CreateDirectory(imp);
                await Proc.Run("mount", ["-o", "ro,loop,noload", img, imp]);
                Check(SameFiles(imp, lvFiles) && !File.Exists(Path.Combine(imp, "depois.bin")), "arquivos de antes íntegros; o gravado depois do instantâneo não entrou");
                await Proc.Run("umount", [imp]);
                File.Delete(img);
            }
            finally
            {
                if (mp != null && !(await Proc.Run("umount", [mp])).Ok) await Proc.Run("umount", ["-l", mp]);
                await Proc.Run("vgchange", ["-an", vg]);
                await Proc.Run("vgremove", ["-f", vg]);
                await Proc.Run("pvremove", ["-ff", "-y", dev2]);
                DropLoop(dev2, file2);
            }
        });

        await Step("atualização: acha a versão publicada", async () =>
        {
            var u = await Updater.Check();
            if (u == null) { W("   (sem resposta do GitHub — limite de consultas ou sem release Linux ainda; não conta como falha)"); return; }
            W($"   publicado: {u.Version} · {u.SetupUrl} (este build: 1.0.{Updater.CurrentBuild})");
            Check(u.Build > 0, "release lido");
        });

        await Step("Limpar disco", async () =>
        {
            d = LoopDisk();
            DiskOperations.Wipe(d, null, WipeMethod.Zeros, sink);
            d = await SettleLoop(x => x.Table.Style == TableStyle.None);
            Check(d.Table.Style == TableStyle.None, "disco zerado aparece sem tabela");
        });
    }

    /// <summary>
    /// Teste da migração (numa VM descartável): migra o sistema em uso para <paramref name="targetDev"/>
    /// e confere o disco novo — fstab e GRUB com os UUIDs novos. Quem liga a VM pelo disco novo é o script.
    /// </summary>
    public static int RunMigrate(string outPath, string targetDev)
    {
        RunMigrateAsync(targetDev).Wait();
        W(_fails == 0 ? "\nRESULTADO: TUDO OK" : $"\nRESULTADO: {_fails} FALHA(S)");
        File.WriteAllText(outPath, Log.ToString(), System.Text.Encoding.UTF8);
        return _fails;
    }

    private static async Task RunMigrateAsync(string targetDev)
    {
        W($"LibertyX Partition · teste de migração · {LinuxTools.Current.Name} · kernel {Environment.OSVersion.Version} · {(Directory.Exists("/sys/firmware/efi") ? "UEFI" : "BIOS")}");
        var sink = new OpSink();
        sink.LogLine += l => W("   · " + l);
        await Step("Planejar e migrar", async () =>
        {
            var target = DiskSystem.Enumerate().First(d => d.DevicePath == targetDev);
            var plan = LinuxMigrate.Analyze(target);
            foreach (var p in plan.Pieces) W($"   {p.Title,-10} {p.FsType,-6} {p.SourceDev} usado {Fmt(p.Used)} de {Fmt(p.Size)} → {Fmt(p.TargetSize)} {(p.Include ? "" : "(fica)")} subvols=[{string.Join(",", p.Subvols.Select(s => s.subvol + "@" + s.mountPoint))}]");
            foreach (var n in plan.Notes) W("   nota: " + n);
            Check(plan.Problem == null, "plano sem problemas " + plan.Problem);
            if (plan.Problem != null) return;
            await LinuxMigrate.Run(plan, sink);
            foreach (var p in plan.Pieces.Where(p => p.Include)) W($"LXMAP|{p.Title}|{p.Uuid}|{p.NewUuid}|{p.NewDev}");
            Check(plan.Pieces.Where(p => p.Include).All(p => p.NewUuid != null && p.NewUuid != p.Uuid), "cada sistema de arquivos tem UUID novo");
        });
        await Step("Conferir o disco novo", async () =>
        {
            var target = DiskSystem.Enumerate().First(d => d.DevicePath == targetDev);
            var root = target.Partitions.FirstOrDefault(p => p.Entry?.TypeGuid == PartitionTypes.LinuxRootX64 || p.Entry?.TypeGuid == PartitionTypes.LinuxRootArm64);
            Check(root?.DeviceNode != null, "raiz no disco novo: " + root?.DeviceNode);
            if (root?.DeviceNode == null) return;
            string mp = "/run/libertyx-confere";
            Directory.CreateDirectory(mp);
            await Proc.Run("mount", ["-o", "ro", root.DeviceNode, mp]);
            try
            {
                string fstab = File.ReadAllText(Path.Combine(mp, "etc/fstab"));
                W("   fstab novo:\n" + string.Join("\n", fstab.Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).Select(l => "      " + l)));
                Check(root.FsUuid != null && fstab.Contains(root.FsUuid), "fstab monta a raiz pelo UUID novo");
                Check(File.Exists(Path.Combine(mp, "usr/bin/env")) && File.Exists(Path.Combine(mp, "etc/hostname")), "o sistema foi copiado");
            }
            finally { await Proc.Run("umount", [mp]); }
        });
    }

    /// <summary>Teste do reparo do boot (numa VM): <paramref name="partDev"/> é a raiz de um Linux instalado.</summary>
    public static int RunRepair(string outPath, string partDev)
    {
        Task.Run(async () =>
        {
            W($"LibertyX Partition · teste de reparo do boot · {(Directory.Exists("/sys/firmware/efi") ? "UEFI" : "BIOS")}");
            var sink = new OpSink();
            sink.LogLine += l => W("   · " + l);
            await Step("Reparar a inicialização", async () =>
            {
                var all = DiskSystem.Enumerate();
                var p = all.SelectMany(d => d.Partitions).FirstOrDefault(x => x.DeviceNode == partDev);
                if (p == null)
                {
                    foreach (var d in all) { W($"   {d.DevicePath} {d.StyleText} erro={d.Error}"); foreach (var q in d.Partitions) W($"      {q.DeviceNode} {FsDetect.Name(q.FileSystem)} @{q.Offset} {q.HeldBy}"); }
                    Check(false, $"{partDev} não está na lista de discos");
                    return;
                }
                var info = await LinuxMigrate.Inspect(p);
                Check(info != null, $"Linux instalado reconhecido: {info?.Name} · {info?.Loader} · subvol={info?.BtrfsSubvol}");
                if (info == null) return;
                await LinuxMigrate.RepairBoot(p, info, sink);
                Check(true, "reparo concluído");
            });
        }).Wait();
        W(_fails == 0 ? "\nRESULTADO: TUDO OK" : $"\nRESULTADO: {_fails} FALHA(S)");
        File.WriteAllText(outPath, Log.ToString(), System.Text.Encoding.UTF8);
        return _fails;
    }
}
