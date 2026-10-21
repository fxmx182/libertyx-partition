using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Sistemas de arquivos no Linux: formatar, redimensionar, verificar, rótulo, UUID, montar.
/// O LibertyX cuida da tabela e da movimentação de dados com o próprio motor; o que é interno
/// de cada sistema de arquivos fica com a ferramenta oficial dele (e2fsprogs, xfsprogs,
/// btrfs-progs, ntfs-3g, dosfstools, exfatprogs, f2fs-tools) — as mesmas que o GParted usa.
/// </summary>
[SupportedOSPlatform("linux")]
public static partial class LinuxFs
{
    public sealed record Spec(string Key, string Name, FsKind Kind, string Mkfs, int LabelMax, bool Shrink, bool Grow, bool GrowMounted, bool ShrinkMounted, string Note);

    /// <summary>O que dá para criar, na ordem em que aparece na tela.</summary>
    public static readonly List<Spec> Formats =
    [
        new("ext4", "ext4", FsKind.Ext, "mkfs.ext4", 16, true, true, true, false, "Padrão do Linux: journal, rápido e robusto."),
        new("xfs", "XFS", FsKind.Xfs, "mkfs.xfs", 12, false, true, true, false, "Para volumes grandes e arquivos grandes. Cresce, mas não diminui."),
        new("btrfs", "Btrfs", FsKind.Btrfs, "mkfs.btrfs", 255, true, true, true, true, "Instantâneos, compressão e soma de verificação de tudo."),
        new("ntfs", "NTFS", FsKind.Ntfs, "mkntfs", 32, true, true, false, false, "Para trocar arquivos com o Windows (discos internos e externos)."),
        new("exfat", "exFAT", FsKind.ExFat, "mkfs.exfat", 11, false, false, false, false, "Pendrives e cartões: Windows, Mac, Linux, câmeras e TVs; sem limite de 4 GB."),
        new("fat32", "FAT32", FsKind.Fat32, "mkfs.fat", 11, false, false, false, false, "Compatibilidade máxima (até 4 GB por arquivo); partição EFI."),
        new("f2fs", "F2FS", FsKind.F2fs, "mkfs.f2fs", 512, false, true, false, false, "Feito para memória flash (cartões, pendrives, eMMC)."),
        new("ext3", "ext3", FsKind.Ext, "mkfs.ext3", 16, true, true, true, false, "Compatível com sistemas antigos."),
        new("ext2", "ext2", FsKind.Ext, "mkfs.ext2", 16, true, true, false, false, "Sem journal (pendrives antigos, /boot de sistemas velhos)."),
        new("fat16", "FAT16", FsKind.Fat16, "mkfs.fat", 11, false, false, false, false, "Mídias pequenas e aparelhos antigos (até 4 GB)."),
        new("swap", "Linux swap", FsKind.LinuxSwap, "mkswap", 16, true, true, false, false, "Área de troca da memória."),
    ];

    public static Spec? SpecOf(PartitionInfo p) => p.FileSystem switch
    {
        FsKind.Ext => Formats.First(f => f.Key == (p.FsVariant is "ext2" or "ext3" ? p.FsVariant : "ext4")),
        FsKind.Fat12 or FsKind.Fat16 => Formats.First(f => f.Key == "fat16"),
        _ => Formats.FirstOrDefault(f => f.Kind == p.FileSystem),
    };

    private static void Need(params string[] tools)
    {
        if (LinuxTools.Missing(tools) is { } m) throw new InvalidOperationException(m);
    }

    private static Action<string> Lines(OpSink s) => l => { if (l.Trim().Length > 0) s.Log(l.TrimEnd()); };

    private static void Ensure(CommandResult r, string what)
    {
        if (!r.Ok) throw new InvalidOperationException($"{what} falhou (código {r.ExitCode}): {Last(r.Message)}");
    }

    private static string Last(string msg)
    {
        var lines = msg.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" · ", lines.TakeLast(4));
    }

    // ───────── formatar ─────────

    /// <summary>
    /// Formata <paramref name="dev"/>. Antes apaga as assinaturas antigas (wipefs): um resto de
    /// Btrfs a 64 KiB dentro de um ext4 novo deixa o blkid em dúvida e o sistema não monta.
    /// </summary>
    public static async Task Mkfs(string dev, string key, string label, int cluster, bool quick, OpSink s)
    {
        var spec = Formats.First(f => f.Key == key);
        if (label.Length > spec.LabelMax) label = label[..spec.LabelMax];
        if (key is "fat32" or "fat16" && !LinuxTools.Has("mkfs.fat") && key == "fat32")
        {
            OwnFat32(dev, label, cluster, s);
            return;
        }
        Need(spec.Mkfs);
        if (LinuxTools.Has("wipefs")) await Proc.Run("wipefs", ["-a", "-q", dev], s.Token);
        var args = new List<string>();
        switch (key)
        {
            case "ext4" or "ext3" or "ext2":
                args.AddRange(["-F", "-q"]);
                if (label.Length > 0) args.AddRange(["-L", label]);
                if (cluster > 0) args.AddRange(["-b", cluster.ToString()]);
                if (!quick) args.Add("-c");   // badblocks antes de formatar
                break;
            case "xfs":
                args.Add("-f");
                if (label.Length > 0) args.AddRange(["-L", label]);
                if (cluster > 0) args.AddRange(["-b", $"size={cluster}"]);
                break;
            case "btrfs":
                args.Add("-f");
                if (label.Length > 0) args.AddRange(["-L", label]);
                break;
            case "f2fs":
                args.Add("-f");
                if (label.Length > 0) args.AddRange(["-l", label]);
                break;
            case "ntfs":
                args.AddRange(["-F"]);
                if (quick) args.Add("-Q");
                if (label.Length > 0) args.AddRange(["-L", label]);
                if (cluster > 0) args.AddRange(["-c", cluster.ToString()]);
                break;
            case "exfat":
                if (label.Length > 0) args.AddRange(["-L", label]);
                if (cluster > 0) args.AddRange(["-c", (cluster / 1024) + "K"]);
                break;
            case "fat32" or "fat16":
                args.AddRange(["-F", key == "fat32" ? "32" : "16", "-I"]);
                if (label.Length > 0) args.AddRange(["-n", label.ToUpperInvariant()]);
                if (cluster > 0) args.AddRange(["-s", (cluster / 512).ToString()]);
                if (!quick) args.Add("-c");
                break;
            case "swap":
                args.Add("-f");
                if (label.Length > 0) args.AddRange(["-L", label]);
                break;
        }
        args.Add(dev);
        s.Log($"{spec.Mkfs} {string.Join(' ', args)}");
        var r = await Proc.Run(LinuxTools.Find(spec.Mkfs)!, args, s.Token, Lines(s));
        if (!r.Ok && key == "fat32")
        {
            s.Log("O mkfs.fat recusou; formatando FAT32 pelo próprio LibertyX…");
            OwnFat32(dev, label, cluster, s);
            return;
        }
        Ensure(r, $"Formatar em {spec.Name}");
        KernelSync.Settle();
    }

    private static void OwnFat32(string dev, string label, int cluster, OpSink s)
    {
        using var d = new LinuxDevice(dev, true);
        long hidden = 0;
        try { hidden = long.Parse(File.ReadAllText($"/sys/class/block/{Path.GetFileName(dev)}/start").Trim()) * 512 / d.SectorSize; } catch { }
        Fat32Formatter.Format(d, label, cluster, hidden, new Progress<double>(x => s.Progress(x, "Formatando FAT32")));
        d.Flush();
    }

    /// <summary>Partição criptografada: LUKS2 com a senha, e o sistema de arquivos dentro.</summary>
    public static async Task MkfsEncrypted(string dev, string innerKey, string label, string password, OpSink s)
    {
        Need("cryptsetup");
        if (LinuxTools.Has("wipefs")) await Proc.Run("wipefs", ["-a", "-q", dev], s.Token);
        s.Log("Criando o contêiner LUKS2 (AES-XTS 512 bits, Argon2id)…");
        s.Progress(-1, "Criptografando");
        var lbl = new List<string> { "luksFormat", "--type", "luks2", "-q", "--key-file=-" };
        if (label.Length > 0) lbl.AddRange(["--label", label]);
        lbl.Add(dev);
        Ensure(await Proc.Run("cryptsetup", lbl, s.Token, Lines(s), stdin: password), "Criar LUKS");
        string name = "libertyx-" + Guid.NewGuid().ToString("N")[..8];
        Ensure(await Proc.Run("cryptsetup", ["open", "--key-file=-", dev, name], s.Token, Lines(s), stdin: password), "Abrir LUKS");
        try { await Mkfs("/dev/mapper/" + name, innerKey, label, 0, true, s); }
        finally { await Proc.Run("cryptsetup", ["close", name]); }
        s.Log("Partição criptografada pronta. Para usar, desbloqueie em Criptografia (ou pelo gerenciador de arquivos).");
    }

    // ───────── redimensionar ─────────

    /// <summary>Menor tamanho possível (bytes) para o sistema de arquivos encolher, com folga. Nulo = não dá para saber.</summary>
    public static async Task<long?> MinSize(PartitionInfo p, CancellationToken ct = default)
    {
        string? dev = p.DeviceNode;
        if (dev == null) return null;
        try
        {
            switch (p.FileSystem)
            {
                case FsKind.Ext when LinuxTools.Has("resize2fs"):
                    {
                        var r = await Proc.Run("resize2fs", ["-P", dev], ct);
                        var m = Regex.Match(r.Output + r.Error, @"minimum size of the filesystem:\s*(\d+)");
                        if (!m.Success) return null;
                        long blocks = long.Parse(m.Groups[1].Value);
                        using var v = DiskSystem.OpenPartition(p);
                        var x = FsDetect.ParseExt(v.ReadBytes(1024, 1024));
                        long bs = x?.BlockSize ?? 4096;
                        return (long)(blocks * bs * 1.05) + (64L << 20);
                    }
                case FsKind.Ntfs when LinuxTools.Has("ntfsresize"):
                    {
                        var r = await Proc.Run("ntfsresize", ["--info", "--force", "--no-progress-bar", dev], ct);
                        var m = Regex.Match(r.Output, @"You might resize at (\d+) bytes");
                        return m.Success ? (long)(long.Parse(m.Groups[1].Value) * 1.03) + (64L << 20) : null;
                    }
                case FsKind.Btrfs or FsKind.LinuxSwap:
                    return p.Used is { } u ? u + Math.Max(1L << 30, u / 10) : null;
            }
        }
        catch { }
        return null;
    }

    public static bool CanResize(PartitionInfo p, bool shrink, out string? why)
    {
        why = null;
        var spec = SpecOf(p);
        if (spec == null) { why = $"{FsDetect.Name(p.FileSystem)} não é redimensionado aqui; dá para mover."; return false; }
        if (shrink && !spec.Shrink) { why = $"{spec.Name} não diminui (só cresce). Para encolher: copie os arquivos, recrie menor e devolva."; return false; }
        if (!shrink && !spec.Grow) { why = $"{spec.Name} não é redimensionado por nenhuma ferramenta confiável; dá para mover a partição."; return false; }
        if (p.IsMounted && !(shrink ? spec.ShrinkMounted : spec.GrowMounted)) { why = $"Desmonte {p.KernelName} antes."; }
        return true;
    }

    /// <summary>
    /// Encolhe o sistema de arquivos para caber em <paramref name="newBytes"/> (antes de encolher a
    /// partição) ou o faz ocupar a partição inteira (depois de aumentá-la).
    /// </summary>
    public static async Task ResizeFs(PartitionInfo p, long newBytes, bool grow, OpSink s)
    {
        string dev = p.DeviceNode ?? throw new InvalidOperationException("A partição ainda não tem /dev — clique em Atualizar.");
        long kib = newBytes / 1024;
        switch (p.FileSystem)
        {
            case FsKind.Ext:
                Need("e2fsck", "resize2fs");
                if (!p.IsMounted)
                {
                    s.Log("Conferindo o sistema de arquivos antes (e2fsck -f)…");
                    s.Progress(-1, "Verificando");
                    var c = await Proc.Run("e2fsck", ["-f", "-y", "-C", "0", dev], s.Token, Lines(s));
                    if (c.ExitCode >= 4) throw new InvalidOperationException($"O e2fsck achou erros que não corrigiu (código {c.ExitCode}). Nada foi redimensionado.");
                }
                s.Progress(-1, grow ? "Expandindo ext" : "Encolhendo ext");
                Ensure(await Proc.Run("resize2fs", grow ? ["-p", dev] : ["-p", dev, kib + "K"], s.Token, Lines(s)), "resize2fs");
                break;
            case FsKind.Ntfs:
                Need("ntfsresize");
                s.Log("Simulando antes (ntfsresize --no-action)…");
                var sim = await Proc.Run("ntfsresize", grow ? ["--no-action", "--force", "--no-progress-bar", dev] : ["--no-action", "--force", "--no-progress-bar", "--size", newBytes.ToString(), dev], s.Token, Lines(s));
                Ensure(sim, "Simulação do ntfsresize");
                s.Progress(-1, grow ? "Expandindo NTFS" : "Encolhendo NTFS");
                Ensure(await Proc.Run("ntfsresize", grow ? ["--force", "--no-progress-bar", dev] : ["--force", "--no-progress-bar", "--size", newBytes.ToString(), dev], s.Token, Lines(s), stdin: "y\n"), "ntfsresize");
                s.Log("O Windows vai rodar uma verificação rápida (chkdsk) na próxima vez que abrir este volume — é normal depois de redimensionar.");
                break;
            case FsKind.Btrfs:
                Need("btrfs");
                await WithMount(p, async mp =>
                {
                    s.Progress(-1, grow ? "Expandindo Btrfs" : "Encolhendo Btrfs");
                    Ensure(await Proc.Run("btrfs", ["filesystem", "resize", grow ? "max" : newBytes.ToString(), mp], s.Token, Lines(s)), "btrfs filesystem resize");
                }, s);
                break;
            case FsKind.Xfs:
                if (!grow) throw new InvalidOperationException("XFS não diminui.");
                Need("xfs_growfs");
                await WithMount(p, async mp =>
                {
                    s.Progress(-1, "Expandindo XFS");
                    Ensure(await Proc.Run("xfs_growfs", [mp], s.Token, Lines(s)), "xfs_growfs");
                }, s);
                break;
            case FsKind.F2fs:
                if (!grow) throw new InvalidOperationException("F2FS não diminui com segurança.");
                Need("resize.f2fs");
                Ensure(await Proc.Run("resize.f2fs", [dev], s.Token, Lines(s)), "resize.f2fs");
                break;
            case FsKind.LinuxSwap:
                // swap não tem conteúdo a preservar: recria no tamanho novo com o mesmo UUID e rótulo
                if (grow || newBytes > 0)
                {
                    Need("mkswap");
                    var args = new List<string> { "-f" };
                    if (p.FsUuid != null) args.AddRange(["-U", p.FsUuid]);
                    if (!string.IsNullOrEmpty(p.Label)) args.AddRange(["-L", p.Label!]);
                    if (!grow) args.AddRange([dev, kib.ToString()]); else args.Add(dev);
                    Ensure(await Proc.Run("mkswap", args, s.Token, Lines(s)), "mkswap");
                }
                break;
            default:
                throw new InvalidOperationException($"{FsDetect.Name(p.FileSystem)} não é redimensionado aqui.");
        }
        KernelSync.Settle();
    }

    /// <summary>Roda com a partição montada: usa a montagem existente ou monta numa pasta temporária.</summary>
    public static async Task WithMount(PartitionInfo p, Func<string, Task> work, OpSink? s = null, bool readOnly = false)
    {
        var existing = p.MountPoints.FirstOrDefault();
        if (existing != null && p.HeldBy == null) { await work(existing); return; }
        string dev = p.DeviceNode ?? throw new InvalidOperationException("A partição não tem /dev.");
        string mp = Path.Combine("/run/libertyx", "m-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mp);
        var r = await Proc.Run("mount", readOnly ? ["-o", "ro", dev, mp] : [dev, mp]);
        if (!r.Ok) { try { Directory.Delete(mp); } catch { } throw new InvalidOperationException($"Não foi possível montar {dev}: {Last(r.Message)}"); }
        s?.Log($"Montada temporariamente em {mp}.");
        try { await work(mp); }
        finally
        {
            for (int i = 0; i < 5; i++)
            {
                var u = await Proc.Run("umount", [mp]);
                if (u.Ok) break;
                await Task.Delay(800);
            }
            try { Directory.Delete(mp); } catch { }
        }
    }

    // ───────── verificar ─────────

    public enum CheckMode { ReadOnly, Repair, RepairDeep }

    /// <summary>Verifica (e opcionalmente repara). Devolve o resumo em português.</summary>
    public static async Task<string> Check(PartitionInfo p, CheckMode mode, OpSink s)
    {
        string dev = p.DeviceNode ?? throw new InvalidOperationException("A partição não tem /dev.");
        if (p.IsMounted && mode != CheckMode.ReadOnly && p.FileSystem != FsKind.Btrfs)
            throw new InvalidOperationException($"{p.KernelName} está montada em {p.MountPoints[0]}. Desmonte para reparar (verificar sem corrigir funciona montada, mas o resultado pode acusar falsos erros).");
        s.Progress(-1, mode == CheckMode.ReadOnly ? "Verificando" : "Reparando");
        CommandResult r;
        switch (p.FileSystem)
        {
            case FsKind.Ext:
                Need("e2fsck");
                r = await Proc.Run("e2fsck", mode switch
                {
                    CheckMode.ReadOnly => ["-f", "-n", "-C", "0", dev],
                    CheckMode.Repair => ["-f", "-y", "-C", "0", dev],
                    _ => ["-f", "-y", "-c", "-C", "0", dev],
                }, s.Token, Lines(s));
                return r.ExitCode switch
                {
                    0 => "Nenhum problema encontrado.",
                    1 => "Erros encontrados e corrigidos.",
                    2 => "Erros corrigidos — reinicie se for o sistema em uso.",
                    4 when mode == CheckMode.ReadOnly => "Há erros. Rode de novo com “Verificar e corrigir” (com a partição desmontada).",
                    4 => "Ficaram erros sem correção. Faça uma imagem do disco e recupere os arquivos dela.",
                    8 => "O e2fsck não conseguiu rodar: " + Last(r.Message),
                    _ => $"e2fsck terminou com o código {r.ExitCode}: {Last(r.Message)}",
                };
            case FsKind.Xfs:
                Need("xfs_repair");
                r = await Proc.Run("xfs_repair", mode == CheckMode.ReadOnly ? ["-n", dev] : [dev], s.Token, Lines(s));
                if (r.ExitCode == 2) return "O journal do XFS tem alterações pendentes: monte e desmonte a partição uma vez (isso aplica o journal) e verifique de novo.";
                return r.Ok ? (mode == CheckMode.ReadOnly ? "Nenhum problema encontrado." : "Verificação e reparo concluídos.") : (mode == CheckMode.ReadOnly ? "Há erros. Use “Verificar e corrigir”." : "O xfs_repair não conseguiu reparar: " + Last(r.Message));
            case FsKind.Btrfs:
                Need("btrfs");
                if (p.IsMounted)
                {
                    s.Log("Partição montada: usando o scrub (confere a soma de verificação de todos os blocos e corrige pelas cópias, se houver).");
                    r = await Proc.Run("btrfs", ["scrub", "start", "-B", p.MountPoints[0]], s.Token, Lines(s));
                    return r.Ok ? "Scrub concluído sem erros incorrigíveis." : "O scrub achou erros: " + Last(r.Message);
                }
                string[] bargs = mode == CheckMode.ReadOnly ? ["check", "--readonly", "--progress", dev] : ["check", "--repair", "--progress", dev];
                r = await Proc.Run("btrfs", bargs, s.Token, Lines(s), stdin: "y\n");
                // logo depois de desmontar, o kernel ainda pode estar soltando o volume
                for (int i = 0; i < 5 && !r.Ok && (r.Error + r.Output).Contains("is currently mounted"); i++)
                {
                    await Task.Delay(1500, s.Token);
                    r = await Proc.Run("btrfs", bargs, s.Token, Lines(s), stdin: "y\n");
                }
                return r.Ok ? (mode == CheckMode.ReadOnly ? "Nenhum problema encontrado." : "Reparo concluído.") : (mode == CheckMode.ReadOnly ? "Há erros. Antes de reparar, faça uma imagem: o btrfs check --repair é o último recurso." : "O reparo não terminou bem: " + Last(r.Message));
            case FsKind.Ntfs:
                Need("ntfsfix");
                r = await Proc.Run("ntfsfix", mode == CheckMode.ReadOnly ? ["--no-action", dev] : ["--clear-dirty", dev], s.Token, Lines(s));
                return r.Ok ? (mode == CheckMode.ReadOnly ? "Estrutura básica do NTFS em ordem." : "Correções básicas feitas e o volume foi marcado para o chkdsk do Windows conferir a fundo na próxima vez.")
                            : "O ntfsfix achou problemas que só o chkdsk do Windows resolve: " + Last(r.Message);
            case FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32:
                Need("fsck.fat");
                r = await Proc.Run("fsck.fat", mode switch { CheckMode.ReadOnly => ["-n", "-v", dev], CheckMode.Repair => ["-a", "-w", "-v", dev], _ => ["-a", "-w", "-t", "-v", dev] }, s.Token, Lines(s));
                return r.ExitCode switch { 0 => "Nenhum problema encontrado.", 1 => mode == CheckMode.ReadOnly ? "Há erros. Use “Verificar e corrigir”." : "Erros encontrados e corrigidos.", _ => "O fsck.fat não conseguiu rodar: " + Last(r.Message) };
            case FsKind.ExFat:
                Need("fsck.exfat");
                r = await Proc.Run("fsck.exfat", mode == CheckMode.ReadOnly ? ["-n", "-v", dev] : ["-y", "-v", dev], s.Token, Lines(s));
                return r.ExitCode switch { 0 => "Nenhum problema encontrado.", 1 => "Erros encontrados e corrigidos.", 4 => mode == CheckMode.ReadOnly ? "Há erros. Use “Verificar e corrigir”." : "Ficaram erros sem correção.", _ => "O fsck.exfat não conseguiu rodar: " + Last(r.Message) };
            case FsKind.F2fs:
                Need("fsck.f2fs");
                r = await Proc.Run("fsck.f2fs", mode == CheckMode.ReadOnly ? ["--dry-run", dev] : ["-f", "-y", dev], s.Token, Lines(s));
                return r.Ok ? "Verificação concluída sem erros graves." : "O fsck.f2fs achou problemas: " + Last(r.Message);
            default:
                throw new InvalidOperationException($"{FsDetect.Name(p.FileSystem)} não tem verificador aqui.");
        }
    }

    // ───────── rótulo e UUID ─────────

    public static async Task SetLabel(PartitionInfo p, string label, OpSink s)
    {
        string dev = p.DeviceNode ?? throw new InvalidOperationException("A partição não tem /dev.");
        CommandResult r = p.FileSystem switch
        {
            FsKind.Ext => await Run1("e2label", [dev, label], s),
            FsKind.Xfs => await Run1("xfs_admin", ["-L", label.Length == 0 ? "--" : label, dev], s),
            FsKind.Btrfs => await Run1("btrfs", ["filesystem", "label", p.MountPoints.FirstOrDefault() ?? dev, label], s),
            FsKind.Ntfs => await Run1("ntfslabel", ["--force", dev, label], s),
            FsKind.ExFat => LinuxTools.Has("exfatlabel") ? await Run1("exfatlabel", [dev, label], s) : await Run1("tune.exfat", ["-L", label, dev], s),
            FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32 => await Run1("fatlabel", [dev, label.ToUpperInvariant()], s),
            FsKind.LinuxSwap => await Run1("swaplabel", ["-L", label, dev], s),
            FsKind.F2fs => await Run1("f2fslabel", [dev, label], s),
            FsKind.Luks => await Run1("cryptsetup", ["config", "--label", label, dev], s),
            _ => throw new InvalidOperationException($"Rótulo de {FsDetect.Name(p.FileSystem)} não é alterado aqui."),
        };
        Ensure(r, "Definir rótulo");
        KernelSync.Settle();
    }

    public static bool UuidByTool(FsKind k) => k is FsKind.Ext or FsKind.Xfs or FsKind.Btrfs or FsKind.LinuxSwap or FsKind.Luks;

    /// <summary>UUID novo (aleatório quando <paramref name="uuid"/> é nulo). NTFS/FAT/exFAT usam o número de série (motor próprio).</summary>
    public static async Task SetUuid(PartitionInfo p, string? uuid, OpSink s)
    {
        string dev = p.DeviceNode ?? throw new InvalidOperationException("A partição não tem /dev.");
        string u = uuid ?? Guid.NewGuid().ToString();
        switch (p.FileSystem)
        {
            case FsKind.Ext:
                Need("tune2fs", "e2fsck");
                s.Log("Conferindo antes (e2fsck -f), o tune2fs exige…");
                await Proc.Run("e2fsck", ["-f", "-p", dev], s.Token, Lines(s));
                Ensure(await Proc.Run("tune2fs", ["-U", u, dev], s.Token, Lines(s), stdin: "y\n"), "tune2fs -U");
                break;
            case FsKind.Xfs:
                Need("xfs_admin");
                Ensure(await Proc.Run("xfs_admin", ["-U", uuid ?? "generate", dev], s.Token, Lines(s)), "xfs_admin -U");
                break;
            case FsKind.Btrfs:
                Need("btrfstune");
                s.Log("O Btrfs reescreve o UUID em todos os metadados — pode demorar em volumes grandes.");
                Ensure(await Proc.Run("btrfstune", uuid == null ? ["-f", "-u", dev] : ["-f", "-U", u, dev], s.Token, Lines(s), stdin: "y\n"), "btrfstune");
                break;
            case FsKind.LinuxSwap:
                Ensure(await Run1("swaplabel", ["-U", u, dev], s), "swaplabel -U");
                break;
            case FsKind.Luks:
                Ensure(await Run1("cryptsetup", ["luksUUID", "-q", "--uuid", u, dev], s), "cryptsetup luksUUID");
                break;
            default:
                throw new InvalidOperationException($"UUID de {FsDetect.Name(p.FileSystem)} não é alterado aqui.");
        }
        s.Log("UUID novo. Se esta partição aparece no /etc/fstab ou no GRUB pelo UUID antigo, atualize lá também.");
        KernelSync.Settle();
    }

    private static async Task<CommandResult> Run1(string tool, string[] args, OpSink s)
    {
        Need(tool);
        return await Proc.Run(LinuxTools.Find(tool)!, args, s.Token, Lines(s));
    }

    // ───────── montar ─────────

    /// <summary>Monta em /media/&lt;usuário&gt;/&lt;rótulo&gt; (o mesmo lugar que o ambiente gráfico usaria).</summary>
    public static async Task<string> Mount(PartitionInfo p, string? where, bool readOnly, OpSink s)
    {
        string dev = p.DeviceNode ?? throw new InvalidOperationException("A partição não tem /dev.");
        string mp = where ?? DefaultMountPoint(p);
        Directory.CreateDirectory(mp);
        var opts = new List<string>();
        if (readOnly) opts.Add("ro");
        var user = LinuxSession.InvokingUser;
        // FAT/exFAT/NTFS não guardam dono: monta como do usuário, senão tudo vira root
        if (user != null && (FsDetect.IsFat(p.FileSystem) || p.FileSystem is FsKind.ExFat or FsKind.Ntfs)) opts.AddRange([$"uid={user.Uid}", $"gid={user.Gid}"]);
        var args = opts.Count > 0 ? new List<string> { "-o", string.Join(',', opts), dev, mp } : [dev, mp];
        var r = await Proc.Run("mount", args, s.Token, Lines(s));
        if (!r.Ok) { try { Directory.Delete(mp); } catch { } }
        if (!r.Ok && r.Message.Contains("unknown filesystem type"))
        {
            string fsName = FsDetect.Name(p.FileSystem);
            string hint = p.FileSystem == FsKind.ExFat ? "instale o módulo do kernel (pacote linux-modules-extra no Ubuntu) ou o exfat-fuse"
                        : p.FileSystem == FsKind.Ntfs ? "instale o ntfs-3g"
                        : "instale o pacote de módulos extras do kernel da sua distribuição";
            throw new InvalidOperationException($"O kernel deste sistema não tem suporte a {fsName} para montar — {hint}. Os arquivos continuam acessíveis pela Recuperação de dados, que lê o volume sem montar.");
        }
        Ensure(r, "Montar");
        return mp;
    }

    public static string DefaultMountPoint(PartitionInfo p)
    {
        string user = LinuxSession.InvokingUser?.Name ?? "root";
        string name = !string.IsNullOrWhiteSpace(p.Label) ? Regex.Replace(p.Label!, @"[/\\\0]", "_") : p.FsUuid ?? p.KernelName ?? "volume";
        string baseDir = Directory.Exists("/media") ? $"/media/{user}" : $"/mnt";
        string mp = Path.Combine(baseDir, name);
        for (int i = 2; Directory.Exists(mp) && Directory.EnumerateFileSystemEntries(mp).Any(); i++) mp = Path.Combine(baseDir, $"{name}{i}");
        return mp;
    }

    public static async Task Unmount(PartitionInfo p, OpSink s)
    {
        foreach (var mp in p.MountPoints.OrderByDescending(m => m.Length))
        {
            var r = await Proc.Run("umount", [mp], s.Token, Lines(s));
            if (!r.Ok)
            {
                var who = LinuxDiskSystem.WhoUses(mp);
                throw new InvalidOperationException($"{mp} está em uso{(who.Count > 0 ? " por: " + string.Join(", ", who.Take(6)) : "")}. Feche esses programas e tente de novo.");
            }
            if (mp.StartsWith("/media/")) try { Directory.Delete(mp); } catch { }
        }
        if (p.HasPageFile && p.DeviceNode != null) Ensure(await Proc.Run("swapoff", [p.DeviceNode], s.Token), "Desligar swap");
    }

    public static async Task SwapOn(PartitionInfo p, OpSink s) => Ensure(await Proc.Run("swapon", [p.DeviceNode!], s.Token, Lines(s)), "Ligar swap");

    // ───────── cópia de arquivos (unir, cluster, converter) ─────────

    /// <summary>Copia tudo de <paramref name="from"/> para <paramref name="to"/> preservando dono, permissões, links, ACL e atributos estendidos.</summary>
    public static async Task CopyTree(string from, string to, OpSink s, bool keepOwnership = true)
    {
        Directory.CreateDirectory(to);
        if (LinuxTools.Has("rsync"))
        {
            var args = new List<string> { "-a", "--info=progress2", "--no-inc-recursive" };
            if (keepOwnership) args.AddRange(["-H", "-A", "-X"]);
            else args.AddRange(["--no-owner", "--no-group", "--no-perms", "--chmod=ugo=rwX", "--modify-window=2"]);
            args.AddRange([from.TrimEnd('/') + "/", to.TrimEnd('/') + "/"]);
            var r = await Proc.Run("rsync", args, s.Token, l =>
            {
                var m = Regex.Match(l, @"^\s*([\d,\.]+)\s+(\d+)%\s+(\S+)");
                if (m.Success) s.Progress(int.Parse(m.Groups[2].Value) / 100.0, "Copiando arquivos", $"{m.Groups[1].Value} bytes · {m.Groups[3].Value}");
                else if (l.Trim().Length > 0 && !l.Contains("to-chk")) s.Log(l.Trim());
            });
            // 23/24: alguns arquivos sumiram/não foram lidos — o resto copiou; acima disso é falha
            if (r.ExitCode is not (0 or 24)) throw new InvalidOperationException($"A cópia falhou (rsync {r.ExitCode}): {Last(r.Message)}");
            return;
        }
        var c = await Proc.Run("cp", keepOwnership ? ["-a", from.TrimEnd('/') + "/.", to] : ["-r", "--no-preserve=ownership,mode", from.TrimEnd('/') + "/.", to], s.Token, Lines(s));
        Ensure(c, "Cópia dos arquivos");
    }

    /// <summary>"1.234,5" e "1,234.5" → número (as ferramentas falam em inglês; o kernel às vezes não).</summary>
    internal static long ParseLong(string s) => long.Parse(s.Replace(",", "").Replace(".", ""), CultureInfo.InvariantCulture);
}
