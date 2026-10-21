using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Leva o Linux em uso para outro disco, pronto para dar boot. Não é cópia setor a setor (o sistema
/// está rodando e o disco novo costuma ter outro tamanho): recria cada sistema de arquivos do
/// sistema no disco novo — mesmo tipo e rótulo, UUID novo —, copia tudo com rsync preservando
/// dono, permissões, ACL, atributos e links (duas passadas: a segunda pega o que mudou durante a
/// primeira), corrige fstab, GRUB, resume e entradas do systemd-boot para os UUIDs novos, e
/// reinstala o carregador de boot dentro do sistema copiado (chroot), com o initramfs gerado de novo.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxMigrate
{
    public sealed class Piece
    {
        public required string MountPoint { get; init; }     // "/", "/boot", "/boot/efi", "/home", "[SWAP]"
        public required string SourceDev { get; init; }      // /dev/nvme0n1p2, /dev/mapper/vg-root
        public required string FsType { get; init; }        // ext4, xfs, btrfs, vfat, swap
        public string? Uuid { get; init; }
        public string? PartUuid { get; init; }
        public string? Label { get; init; }
        public string? PartLabel { get; init; }
        public long Size { get; init; }
        public long Used { get; init; }
        public bool Required { get; init; }
        public bool Include { get; set; } = true;
        public long TargetSize { get; set; }
        public string? NewDev { get; set; }
        public string? NewUuid { get; set; }
        public Guid NewPartUuid { get; set; }
        /// <summary>Btrfs: subvolumes montados deste volume (nome, ponto de montagem).</summary>
        public List<(string subvol, string mountPoint)> Subvols { get; } = [];
        public string Title => MountPoint == "[SWAP]" ? "swap" : MountPoint;
    }

    public sealed class Plan
    {
        public List<Piece> Pieces { get; } = [];
        public bool Uefi { get; init; }
        public string? Problem { get; set; }
        public List<string> Notes { get; } = [];
        public required DiskInfo Target { get; init; }
        public string Loader { get; set; } = "grub";
        public long Need => Pieces.Where(p => p.Include).Sum(p => p.FsType == "swap" ? p.Size : p.Used);
    }

    private static readonly string[] RequiredMounts = ["/", "/boot", "/boot/efi", "/efi", "/usr", "/var"];
    private static readonly string[] Supported = ["ext4", "ext3", "ext2", "xfs", "btrfs", "vfat", "f2fs"];

    private static string Read(string p) { try { return File.ReadAllText(p).Trim(); } catch { return ""; } }

    private static long BlockSize(string kn) => long.TryParse(Read($"/sys/class/block/{kn}/size"), out var s) ? s * 512 : 0;

    /// <summary>Monta o plano: o que é do sistema, quanto ocupa, como fica no disco novo, e se dá.</summary>
    public static Plan Analyze(DiskInfo target, bool includeOptional = true)
    {
        var plan = new Plan { Target = target, Uefi = Directory.Exists("/sys/firmware/efi") };
        var mounts = LinuxDiskSystem.ReadMounts();
        var root = mounts.LastOrDefault(m => m.MountPoint == "/");
        string? rootKn = root == null ? null : root.Source.StartsWith("/dev/") ? LinuxDiskSystem.KernelNameOf(root.Source) : null;
        if (rootKn == null) { plan.Problem = "A raiz (/) não está num disco (sistema ao vivo, contêiner ou rede): não há o que migrar."; return plan; }
        string rootUuid = Read($"/sys/class/block/{rootKn}/dm/uuid");
        if (rootUuid.StartsWith("CRYPT-")) { plan.Problem = "A raiz está criptografada (LUKS). A migração recriaria o sistema sem criptografia — use Copiar disco iniciando por um pendrive, que leva o volume criptografado como está."; return plan; }
        if (rootKn.StartsWith("md")) { plan.Problem = "A raiz está num RAID por software: migre pelo próprio mdadm (adicionando o disco novo ao conjunto)."; return plan; }
        // os discos físicos do sistema (atravessando LVM)
        var sysPhys = LinuxDiskSystem.SlavesOf(rootKn).DefaultIfEmpty(rootKn).Select(DiskOf).ToHashSet();
        if (sysPhys.Contains(target.KernelName)) { plan.Problem = "O destino é o próprio disco do sistema."; return plan; }

        var swaps = LinuxDiskSystem.ActiveSwaps();
        var byDev = new Dictionary<string, Piece>();
        foreach (var m in mounts.Where(m => m.Source.StartsWith("/dev/")).OrderBy(m => m.MountPoint.Length))
        {
            if (m.MountPoint.StartsWith("/run") || m.MountPoint.StartsWith("/snap/") || m.MountPoint.StartsWith("/var/lib/docker") || m.MountPoint.StartsWith("/proc") || m.MountPoint.StartsWith("/sys")) continue;
            string? kn = LinuxDiskSystem.KernelNameOf(m.Source);
            if (kn == null) continue;
            var phys = LinuxDiskSystem.SlavesOf(kn).DefaultIfEmpty(kn).Select(DiskOf).ToHashSet();
            if (!phys.Overlaps(sysPhys)) continue;   // está em outro disco: fica onde está
            if (!Supported.Contains(m.FsType)) { if (RequiredMounts.Contains(m.MountPoint)) plan.Problem = $"{m.MountPoint} usa {m.FsType}, que a migração não recria."; continue; }
            if (byDev.TryGetValue(kn, out var existing))
            {
                // mesmo volume montado de novo: subvolume do Btrfs, ou bind
                if (m.FsType == "btrfs" && SubvolOf(m.Options) is { } sv) existing.Subvols.Add((sv, m.MountPoint));
                continue;
            }
            var u = LinuxDiskSystem.Udev(Read($"/sys/class/block/{kn}/dev"));
            long used = 0;
            try { var di = new DriveInfo(m.MountPoint); used = di.TotalSize - di.TotalFreeSpace; } catch { }
            var piece = new Piece
            {
                MountPoint = m.MountPoint, SourceDev = m.Source, FsType = m.FsType == "ext3" || m.FsType == "ext2" ? m.FsType : m.FsType,
                Uuid = u.GetValueOrDefault("ID_FS_UUID"), PartUuid = u.GetValueOrDefault("ID_PART_ENTRY_UUID"), Label = u.TryGetValue("ID_FS_LABEL", out var l) && l.Length > 0 ? l : null,
                PartLabel = u.TryGetValue("ID_PART_ENTRY_NAME", out var pl) && pl.Length > 0 ? pl : null,
                Size = BlockSize(kn), Used = used, Required = RequiredMounts.Contains(m.MountPoint) || m.MountPoint == "/home",
            };
            if (m.FsType == "btrfs" && SubvolOf(m.Options) is { } sv0) piece.Subvols.Add((sv0, m.MountPoint));
            piece.Include = piece.Required || includeOptional;
            byDev[kn] = piece;
            plan.Pieces.Add(piece);
        }
        foreach (var sw in swaps)
        {
            var phys = LinuxDiskSystem.SlavesOf(sw).DefaultIfEmpty(sw).Select(DiskOf).ToHashSet();
            if (!phys.Overlaps(sysPhys) || !File.Exists($"/sys/class/block/{sw}/dev")) continue;
            var u = LinuxDiskSystem.Udev(Read($"/sys/class/block/{sw}/dev"));
            long size = Math.Min(BlockSize(sw), 16L << 30);
            plan.Pieces.Add(new Piece
            {
                MountPoint = "[SWAP]", SourceDev = "/dev/" + sw, FsType = "swap", Uuid = u.GetValueOrDefault("ID_FS_UUID"), PartUuid = u.GetValueOrDefault("ID_PART_ENTRY_UUID"),
                Label = u.TryGetValue("ID_FS_LABEL", out var l) && l.Length > 0 ? l : null, Size = size, Used = size, Required = false, Include = true,
            });
        }
        if (!plan.Pieces.Any(p => p.MountPoint == "/")) { plan.Problem ??= "Não achei a raiz entre as montagens do disco do sistema."; return plan; }
        if (plan.Uefi && !plan.Pieces.Any(p => p.MountPoint is "/boot/efi" or "/efi"))
            plan.Notes.Add("Inicia por UEFI, mas não há partição EFI montada: será criada uma nova (512 MB).");
        if (File.Exists("/boot/efi/loader/loader.conf") || File.Exists("/efi/loader/loader.conf") || File.Exists("/boot/loader/loader.conf")) plan.Loader = "systemd-boot";
        else if (!Directory.Exists("/boot/grub") && !Directory.Exists("/boot/grub2")) { plan.Loader = "?"; plan.Notes.Add("Carregador de boot não reconhecido (nem GRUB nem systemd-boot): os arquivos vão, mas o boot do disco novo terá de ser configurado à mão."); }
        if (LinuxTools.Missing("rsync") is { } miss) plan.Problem ??= miss;
        foreach (var p in plan.Pieces.Where(p => p.FsType != "swap"))
        {
            string tool = p.FsType switch { "vfat" => "mkfs.fat", "xfs" => "mkfs.xfs", "btrfs" => "mkfs.btrfs", "f2fs" => "mkfs.f2fs", _ => "mkfs.ext4" };
            if (LinuxTools.Missing(tool) is { } m2) plan.Problem ??= m2;
        }
        Size(plan);
        return plan;
    }

    private static string DiskOf(string kn)
    {
        // sda3 → sda; nvme0n1p2 → nvme0n1; dm-0 → dm-0
        try
        {
            if (File.Exists($"/sys/class/block/{kn}/partition"))
                return Path.GetFileName(Path.GetDirectoryName(new DirectoryInfo($"/sys/class/block/{kn}").ResolveLinkTarget(true)!.FullName)!);
        }
        catch { }
        return kn;
    }

    private static string? SubvolOf(string options)
    {
        var m = Regex.Match(options + ",", @"(?:^|,)subvol=([^,]*),");
        if (!m.Success) return null;
        string s = m.Groups[1].Value.TrimStart('/');
        return s.Length == 0 ? null : s;
    }

    /// <summary>Refaz os tamanhos depois que a pessoa marcou ou desmarcou partições opcionais.</summary>
    public static Plan Resize(Plan plan)
    {
        if (plan.Problem != null && plan.Problem.StartsWith("Não cabe")) plan.Problem = null;
        Size(plan);
        return plan;
    }

    /// <summary>Tamanhos no disco novo: EFI, /boot e swap como estão; os outros, o usado com folga; a raiz fica com o resto.</summary>
    private static void Size(Plan plan)
    {
        const long MB = 1 << 20, GB = 1L << 30;
        long avail = plan.Target.Size - 2 * MB - 1 * MB;   // alinhamento + GPT de reserva
        if (!plan.Uefi) avail -= MB;                       // BIOS boot
        if (plan.Uefi && !plan.Pieces.Any(p => p.MountPoint is "/boot/efi" or "/efi")) avail -= 512 * MB;
        long fixedSum = 0;
        foreach (var p in plan.Pieces.Where(p => p.Include))
        {
            long t = p.MountPoint switch
            {
                "/boot/efi" or "/efi" => Math.Max(300 * MB, p.Size),
                "/boot" => Math.Max(512 * MB, Math.Min(p.Size, 2 * GB)),
                "[SWAP]" => p.Size,
                "/" => 0,
                _ => (long)(p.Used * 1.25) + GB,
            };
            p.TargetSize = (t + MB - 1) / MB * MB;
            fixedSum += p.TargetSize;
        }
        var root = plan.Pieces.First(p => p.MountPoint == "/");
        long rootMin = (long)(root.Used * 1.15) + 2 * GB;
        long left = avail - fixedSum;
        if (left < rootMin)
        {
            // não cabe com folga: tira a swap e aperta os opcionais antes de desistir
            var swap = plan.Pieces.FirstOrDefault(p => p.MountPoint == "[SWAP]" && p.Include);
            if (swap != null && left + swap.TargetSize >= rootMin) { swap.Include = false; left += swap.TargetSize; plan.Notes.Add("A swap ficou de fora para caber (dá para criar um arquivo de swap depois)."); }
        }
        if (left < rootMin)
        {
            plan.Problem ??= $"Não cabe: o sistema precisa de pelo menos {DiskOperations.Size(fixedSum + rootMin)} e o disco novo tem {DiskOperations.Size(plan.Target.Size)}." +
                (plan.Pieces.Any(p => !p.Required && p.Include && p.MountPoint != "[SWAP]") ? " Deixe de fora as partições de dados (só as do sistema)." : "");
            root.TargetSize = Math.Max(0, left) / MB * MB;
            return;
        }
        root.TargetSize = left / MB * MB;
    }

    // ───────── executar ─────────

    private static Action<string> Lines(OpSink s) => l => { if (l.Trim().Length > 0 && !l.Contains("to-chk")) s.Log(l.TrimEnd()); };

    private static async Task Must(Task<CommandResult> t, string what)
    {
        var r = await t;
        if (!r.Ok) throw new InvalidOperationException($"{what}: {r.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()}");
    }

    public static async Task Run(Plan plan, OpSink s)
    {
        if (plan.Problem != null) throw new InvalidOperationException(plan.Problem);
        var target = plan.Target;
        if (target.IsSystem) throw new InvalidOperationException("O destino é o disco do sistema.");
        const long MB = 1 << 20;
        int ss = target.SectorSize;
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var pieces = plan.Pieces.Where(p => p.Include).ToList();
        var esp = pieces.FirstOrDefault(p => p.MountPoint is "/boot/efi" or "/efi");

        // 1. tabela nova (GPT) — EFI ou BIOS boot primeiro, depois /boot, raiz, os outros e a swap no fim
        s.Log($"Disco novo: {target.DevicePath} ({target.Model}, {DiskOperations.Size(target.Size)}) — tabela GPT, {(plan.Uefi ? "UEFI" : "BIOS")}.");
        var order = new List<Piece>();
        if (esp != null) order.Add(esp);
        order.AddRange(pieces.Where(p => p.MountPoint == "/boot"));
        order.AddRange(pieces.Where(p => p.MountPoint == "/"));
        order.AddRange(pieces.Where(p => !order.Contains(p) && p.MountPoint != "[SWAP]").OrderBy(p => p.MountPoint));
        order.AddRange(pieces.Where(p => p.MountPoint == "[SWAP]"));
        var t = new PartitionTable { Style = TableStyle.Gpt, SectorSize = ss, DiskSectors = target.Size / ss };
        long cur = MB / ss;
        Piece? newEsp = null;
        if (!plan.Uefi)
        {
            t.Entries.Add(new PartitionEntry { StartLba = cur, SectorCount = MB / ss, TypeGuid = PartitionTypes.BiosBoot, UniqueGuid = Guid.NewGuid(), Name = "BIOS boot" });
            cur += MB / ss;
        }
        else if (esp == null)
        {
            newEsp = new Piece { MountPoint = "/boot/efi", SourceDev = "", FsType = "vfat", Label = "EFI", TargetSize = 512 * MB, Required = true };
            order.Insert(0, newEsp);
        }
        var slotOf = new Dictionary<Piece, long>();
        foreach (var p in order)
        {
            long sectors = p.TargetSize / ss;
            if (p.MountPoint == "/")
            {
                // a raiz fica com o que sobra, sem invadir as que vêm depois nem a cópia da GPT no fim
                long after = order.Skip(order.IndexOf(p) + 1).Sum(x => x.TargetSize / ss + MB / ss);
                sectors = Math.Min(sectors, (PartitionCodec.GptLastUsable(t) - cur + 1 - after) / (MB / ss) * (MB / ss));
            }
            Guid type = p.MountPoint switch
            {
                "/boot/efi" or "/efi" => PartitionTypes.Esp,
                "/" => arch == "arm64" ? PartitionTypes.LinuxRootArm64 : PartitionTypes.LinuxRootX64,
                "/home" => PartitionTypes.LinuxHome,
                "/var" => PartitionTypes.LinuxVar,
                "/srv" => PartitionTypes.LinuxSrv,
                "[SWAP]" => PartitionTypes.LinuxSwap,
                _ => PartitionTypes.LinuxFs,
            };
            p.NewPartUuid = Guid.NewGuid();
            t.Entries.Add(new PartitionEntry { StartLba = cur, SectorCount = sectors, TypeGuid = type, UniqueGuid = p.NewPartUuid, Name = p.MountPoint == "[SWAP]" ? "swap" : p.MountPoint == "/" ? "root" : p.MountPoint.Trim('/').Replace('/', '-') });
            slotOf[p] = cur * ss;
            cur += sectors;
            cur = (cur + MB / ss - 1) / (MB / ss) * (MB / ss);
        }
        using (var dev = DiskSystem.OpenDisk(target, true))
        using (DiskSystem.Native.LockVolumes(target))
        {
            // apaga restos de assinaturas no começo de cada partição nova (um superbloco velho confunde o blkid)
            dev.Write(0, new byte[MB]);
            foreach (var e in t.Entries) dev.Write(e.StartLba * ss, new byte[Math.Min(MB, e.SectorCount * ss)]);
            PartitionCodec.Write(dev, t);
            DiskSystem.Native.Rescan(target);
        }

        // 2. sistemas de arquivos novos, com o mesmo tipo e rótulo
        foreach (var p in order)
        {
            p.NewDev = KernelSync.WaitPartitionNode(target, slotOf[p]) ?? throw new InvalidOperationException($"O kernel não criou o /dev da partição de {p.Title}.");
            s.Log($"{p.Title}: {p.NewDev} · {p.FsType} · {DiskOperations.Size(p.TargetSize)}");
            s.Progress(-1, $"Formatando {p.Title}");
            string key = p.FsType switch { "vfat" => "fat32", "swap" => "swap", "xfs" => "xfs", "btrfs" => "btrfs", "f2fs" => "f2fs", "ext3" => "ext3", "ext2" => "ext2", _ => "ext4" };
            await LinuxFs.Mkfs(p.NewDev, key, p.Label ?? (p.MountPoint is "/boot/efi" or "/efi" ? "EFI" : ""), 0, true, s);
            var u = LinuxDiskSystem.Udev(Read($"/sys/class/block/{Path.GetFileName(p.NewDev)}/dev"));
            p.NewUuid = u.GetValueOrDefault("ID_FS_UUID") ?? await BlkidUuid(p.NewDev);
        }

        // 3. monta a árvore nova em /run/libertyx/migrar
        string mnt = "/run/libertyx/migrar";
        Directory.CreateDirectory(mnt);
        var mounted = new Stack<string>();
        try
        {
            foreach (var p in order.Where(p => p.FsType != "swap").OrderBy(p => p.MountPoint == "/" ? 0 : p.MountPoint.Count(c => c == '/')))
            {
                if (p.FsType == "btrfs" && p.Subvols.Count > 0)
                {
                    // recria os subvolumes com os mesmos nomes (o fstab monta por subvol=)
                    string top = Path.Combine("/run/libertyx", "btrfs-" + Guid.NewGuid().ToString("N")[..6]);
                    Directory.CreateDirectory(top);
                    await Must(Proc.Run("mount", [p.NewDev!, top]), "Montar o Btrfs novo");
                    foreach (var (sv, _) in p.Subvols)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(top, sv))!);
                        await Must(Proc.Run("btrfs", ["subvolume", "create", Path.Combine(top, sv)]), "Criar subvolume " + sv);
                    }
                    await Proc.Run("umount", [top]);
                    try { Directory.Delete(top); } catch { }
                    foreach (var (sv, mp) in p.Subvols.OrderBy(x => x.mountPoint.Length))
                    {
                        string at = mnt + (mp == "/" ? "" : mp);
                        Directory.CreateDirectory(at);
                        await Must(Proc.Run("mount", ["-o", "subvol=" + sv, p.NewDev!, at]), $"Montar {mp}");
                        mounted.Push(at);
                    }
                    continue;
                }
                string where = mnt + (p.MountPoint == "/" ? "" : p.MountPoint);
                Directory.CreateDirectory(where);
                await Must(Proc.Run("mount", [p.NewDev!, where]), $"Montar {p.Title} novo");
                mounted.Push(where);
            }

            // 4. cópia: duas passadas (a segunda pega o que o sistema mudou durante a primeira)
            var sources = new List<(string from, string to)>();
            foreach (var p in order.Where(p => p.FsType != "swap" && p != newEsp))
            {
                if (p.Subvols.Count > 0) foreach (var (_, mp) in p.Subvols) sources.Add((mp, mnt + (mp == "/" ? "" : mp)));
                else sources.Add((p.MountPoint, mnt + (p.MountPoint == "/" ? "" : p.MountPoint)));
            }
            for (int pass = 1; pass <= 2; pass++)
            {
                foreach (var (from, to) in sources)
                {
                    s.Log(pass == 1 ? $"Copiando {from} …" : $"Segunda passada em {from} (o que mudou enquanto copiava)…");
                    var args = new List<string> { "-aAXHx", "--numeric-ids", "--delete", "--info=progress2", "--no-inc-recursive" };
                    if (from == "/") foreach (var ex in new[] { "/dev/*", "/proc/*", "/sys/*", "/run/*", "/tmp/*", "/mnt/*", "/media/*", "/lost+found", "/swapfile", "/swap.img" }) args.Add("--exclude=" + ex);
                    args.AddRange([from.TrimEnd('/') + "/", to.TrimEnd('/') + "/"]);
                    var r = await Proc.Run("rsync", args, s.Token, l =>
                    {
                        var m = Regex.Match(l, @"^\s*([\d,\.]+)\s+(\d+)%\s+(\S+)");
                        if (m.Success) s.Progress(int.Parse(m.Groups[2].Value) / 100.0, pass == 1 ? $"Copiando {from}" : $"Conferindo {from}", $"{m.Groups[1].Value} bytes · {m.Groups[3].Value}");
                        else if (l.Trim().Length > 0 && !l.Contains("to-chk") && !l.StartsWith("file has vanished")) s.Log(l.Trim());
                    });
                    // 24 = arquivos sumiram durante a cópia (normal num sistema rodando)
                    if (r.ExitCode is not (0 or 24)) throw new InvalidOperationException($"A cópia de {from} falhou (rsync {r.ExitCode}): {r.Message.Split('\n').LastOrDefault()}");
                    s.PauseGate.Wait(s.Token);
                }
            }
            // o arquivo de swap não vai (pode ser grande e no Btrfs precisa de atributos especiais): recria vazio do mesmo tamanho
            foreach (var sf in new[] { "/swapfile", "/swap.img" }.Where(File.Exists))
            {
                long size = new FileInfo(sf).Length;
                s.Log($"Recriando {sf} ({DiskOperations.Size(size)}) no disco novo…");
                string to = mnt + sf;
                await Proc.Run("truncate", ["-s", "0", to]);
                if (order.First(p => p.MountPoint == "/").FsType == "btrfs") await Proc.Run("chattr", ["+C", to]);
                await Proc.Run("fallocate", ["-l", size.ToString(), to]);
                File.SetUnixFileMode(to, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await Proc.Run("mkswap", [to]);
            }

            // 5. UUIDs novos no fstab, GRUB, resume, systemd-boot
            s.Progress(-1, "Ajustando fstab e boot");
            Fixup(plan, order, mnt, s);

            // 6. carregador de boot dentro do sistema copiado
            await Bootloader(plan, order, mnt, newEsp != null, s);
            s.Log("Gravando o cache no disco novo…");
            await Proc.Run("sync", []);
        }
        finally
        {
            foreach (var b in new[] { "sys/firmware/efi/efivars", "run", "sys", "proc", "dev/pts", "dev" })
                await Proc.Run("umount", ["-l", Path.Combine(mnt, b)]);
            while (mounted.Count > 0) await Proc.Run("umount", [mounted.Pop()]);
        }
        s.Log("Pronto. Para iniciar pelo disco novo, escolha-o no menu de boot (F12, F8, F11 ou Esc ao ligar) ou mude a ordem na BIOS/UEFI. Depois de conferir que tudo funciona, o disco antigo pode ser formatado.");
    }

    private static async Task<string?> BlkidUuid(string dev)
    {
        var r = await Proc.Run("blkid", ["-s", "UUID", "-o", "value", dev]);
        return r.Ok ? r.Output.Trim() : null;
    }

    /// <summary>Troca, nos arquivos de configuração do sistema copiado, cada referência ao volume antigo pela nova.</summary>
    private static void Fixup(Plan plan, List<Piece> order, string mnt, OpSink s)
    {
        var map = new List<(string from, string to)>();
        foreach (var p in order.Where(p => p.NewUuid != null && p.SourceDev.Length > 0))
        {
            if (p.Uuid != null) map.Add((p.Uuid, p.NewUuid!));
            if (p.PartUuid != null) map.Add((p.PartUuid, p.NewPartUuid.ToString()));
        }
        // caminhos de dispositivo (LVM, /dev/sdX) e rótulos viram UUID=: com os dois discos ligados, o
        // mesmo rótulo existiria duas vezes e o sistema poderia montar o antigo
        var devMap = new List<(string from, string to)>();
        foreach (var p in order.Where(p => p.NewUuid != null && p.SourceDev.Length > 0))
        {
            devMap.Add((p.SourceDev, "UUID=" + p.NewUuid));
            if (p.Label != null) { devMap.Add(("LABEL=" + p.Label, "UUID=" + p.NewUuid)); devMap.Add(("/dev/disk/by-label/" + p.Label, "UUID=" + p.NewUuid)); }
            if (p.PartLabel != null) { devMap.Add(("PARTLABEL=" + p.PartLabel, "UUID=" + p.NewUuid)); devMap.Add(("/dev/disk/by-partlabel/" + p.PartLabel, "UUID=" + p.NewUuid)); }
            string? kn = LinuxDiskSystem.KernelNameOf(p.SourceDev);
            if (kn != null)
            {
                string dmName = Read($"/sys/class/block/{kn}/dm/name");
                if (dmName.Length > 0)
                {
                    devMap.Add(("/dev/mapper/" + dmName, "UUID=" + p.NewUuid));
                    // /dev/vg/lv
                    var parts = Regex.Match(dmName, @"^(.+?[^-])-([^-].*)$");
                    if (parts.Success) devMap.Add(($"/dev/{parts.Groups[1].Value.Replace("--", "-")}/{parts.Groups[2].Value.Replace("--", "-")}", "UUID=" + p.NewUuid));
                }
                devMap.Add(("/dev/" + kn, "UUID=" + p.NewUuid));
            }
        }
        var files = new List<string> { "etc/fstab", "etc/crypttab", "etc/default/grub", "etc/initramfs-tools/conf.d/resume", "etc/kernel/cmdline", "etc/uswsusp.conf" };
        foreach (var dir in new[] { "etc/default/grub.d", "etc/dracut.conf.d", "boot/loader/entries", "efi/loader/entries", "boot/efi/loader/entries" })
            if (Directory.Exists(Path.Combine(mnt, dir))) files.AddRange(Directory.GetFiles(Path.Combine(mnt, dir)).Select(f => Path.GetRelativePath(mnt, f)));
        foreach (var gc in new[] { "boot/grub/grub.cfg", "boot/grub2/grub.cfg" }) files.Add(gc);
        foreach (var efiDir in new[] { "boot/efi/EFI", "efi/EFI" })
            if (Directory.Exists(Path.Combine(mnt, efiDir)))
                files.AddRange(Directory.GetFiles(Path.Combine(mnt, efiDir), "grub.cfg", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(mnt, f)));
        foreach (var rel in files.Distinct())
        {
            string f = Path.Combine(mnt, rel);
            if (!File.Exists(f)) continue;
            string txt = File.ReadAllText(f), orig = txt;
            foreach (var (a, b) in map) txt = Regex.Replace(txt, Regex.Escape(a), b, RegexOptions.IgnoreCase);
            if (rel == "etc/fstab" || rel == "etc/crypttab")
                foreach (var (a, b) in devMap.OrderByDescending(x => x.from.Length))
                    txt = Regex.Replace(txt, @"(?m)^(\s*)" + Regex.Escape(a) + @"(?=\s)", "$1" + b);
            else
                foreach (var (a, b) in devMap.OrderByDescending(x => x.from.Length))
                    txt = Regex.Replace(txt, @"(?<=root=|resume=)" + Regex.Escape(a) + @"(?=[\s""'])", b);
            if (rel == "etc/fstab") txt = NofailForOld(plan, order, txt);
            if (txt != orig) { File.WriteAllText(f, txt); s.Log($"Ajustado: /{rel}"); }
        }
    }

    /// <summary>
    /// Linhas do fstab que continuam apontando para o disco antigo (dados que ficaram lá) ganham
    /// "nofail": se o disco antigo for retirado, o sistema novo inicia mesmo assim.
    /// </summary>
    private static string NofailForOld(Plan plan, List<Piece> order, string fstab)
    {
        var migrated = order.Where(p => p.NewUuid != null).Select(p => p.NewUuid!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = fstab.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.TrimStart().StartsWith('#') || l.Trim().Length == 0) continue;
            var f = Regex.Split(l.Trim(), @"\s+");
            if (f.Length < 4 || f[1] is "none" || f[2] is "proc" or "sysfs" or "tmpfs" or "devpts") continue;
            string spec = f[0];
            if (spec.StartsWith("UUID=") && migrated.Contains(spec[5..])) continue;
            if (!(spec.StartsWith("UUID=") || spec.StartsWith("/dev/") || spec.StartsWith("PARTUUID=") || spec.StartsWith("LABEL="))) continue;
            if (f[3].Contains("nofail")) continue;
            f[3] += ",nofail,x-systemd.device-timeout=10s";
            lines[i] = string.Join('\t', f);
        }
        return string.Join('\n', lines);
    }

    private static async Task Bootloader(Plan plan, List<Piece> order, string mnt, bool freshEsp, OpSink s)
    {
        if (plan.Loader == "?") { s.Log("Carregador de boot não reconhecido: configure o boot do disco novo à mão."); return; }
        foreach (var (src, dst) in new[] { ("/dev", "dev"), ("/dev/pts", "dev/pts"), ("/proc", "proc"), ("/sys", "sys"), ("/run", "run") })
        {
            Directory.CreateDirectory(Path.Combine(mnt, dst));
            await Proc.Run("mount", ["--bind", src, Path.Combine(mnt, dst)]);
        }
        if (plan.Uefi && Directory.Exists("/sys/firmware/efi/efivars"))
            await Proc.Run("mount", ["--bind", "/sys/firmware/efi/efivars", Path.Combine(mnt, "sys/firmware/efi/efivars")]);
        bool Has(string p) => File.Exists(Path.Combine(mnt, p.TrimStart('/')));
        Task<CommandResult> Chroot(params string[] cmd) => Proc.Run("chroot", [mnt, .. cmd], s.Token, Lines(s));
        string disk = plan.Target.DevicePath;
        string espMp = order.FirstOrDefault(p => p.MountPoint is "/efi")?.MountPoint ?? "/boot/efi";
        bool arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

        // initramfs primeiro: o disco novo pode ser de outro tipo (SATA → NVMe) e a raiz não está mais no LVM
        s.Progress(-1, "Gerando o initramfs");
        if (Has("/usr/sbin/update-initramfs")) await Chroot("update-initramfs", "-u", "-k", "all");
        else if (Has("/usr/bin/dracut") || Has("/usr/sbin/dracut")) await Chroot("dracut", "-f", "--regenerate-all");
        else if (Has("/usr/bin/mkinitcpio")) await Chroot("mkinitcpio", "-P");

        if (plan.Loader == "systemd-boot")
        {
            s.Progress(-1, "Instalando o systemd-boot");
            await Must(Chroot("bootctl", "--esp-path=" + espMp, "install", "--no-variables"), "bootctl install");
            if (Has("/usr/bin/kernel-install"))
                foreach (var k in Directory.Exists(Path.Combine(mnt, "lib/modules")) ? Directory.GetDirectories(Path.Combine(mnt, "lib/modules")).Select(Path.GetFileName) : [])
                    if (File.Exists(Path.Combine(mnt, "lib/modules", k!, "vmlinuz"))) await Chroot("kernel-install", "add", k!, $"/lib/modules/{k}/vmlinuz");
            s.Log("systemd-boot instalado no disco novo (também como carregador padrão EFI/BOOT, sem mexer no NVRAM deste computador).");
            return;
        }

        s.Progress(-1, "Instalando o GRUB");
        string grubInstall = Has("/usr/sbin/grub-install") || Has("/usr/bin/grub-install") ? "grub-install" : "grub2-install";
        bool fedoraLike = Has("/usr/sbin/grub2-mkconfig") && !Has("/usr/sbin/update-grub");
        if (plan.Uefi)
        {
            string target = arm ? "arm64-efi" : "x86_64-efi";
            if (freshEsp || !fedoraLike)
            {
                // instalação normal (sem tocar no NVRAM deste PC) + a cópia em EFI/BOOT, que qualquer firmware acha sozinho
                string id = Directory.Exists(Path.Combine(mnt, espMp.TrimStart('/'), "EFI")) ? Directory.GetDirectories(Path.Combine(mnt, espMp.TrimStart('/'), "EFI")).Select(Path.GetFileName).FirstOrDefault(n => n is not ("BOOT" or "Boot" or "Microsoft")) ?? "linux" : "linux";
                await Must(Chroot(grubInstall, $"--target={target}", $"--efi-directory={espMp}", $"--bootloader-id={id}", "--no-nvram", "--recheck"), "grub-install (UEFI)");
                await Must(Chroot(grubInstall, $"--target={target}", $"--efi-directory={espMp}", "--removable", "--no-nvram"), "grub-install --removable");
            }
            else s.Log("Fedora/RHEL com UEFI: o shim e o GRUB assinados já estão na partição EFI copiada; só a configuração é refeita.");
        }
        else await Must(Chroot(grubInstall, "--target=i386-pc", "--recheck", disk), "grub-install (BIOS)");

        s.Progress(-1, "Gerando a configuração do GRUB");
        if (Has("/usr/sbin/update-grub")) await Must(Chroot("update-grub"), "update-grub");
        else if (Has("/usr/sbin/grub2-mkconfig")) await Must(Chroot("grub2-mkconfig", "-o", Directory.Exists(Path.Combine(mnt, "boot/grub2")) ? "/boot/grub2/grub.cfg" : "/boot/grub/grub.cfg"), "grub2-mkconfig");
        else if (Has("/usr/bin/grub-mkconfig") || Has("/usr/sbin/grub-mkconfig")) await Must(Chroot("grub-mkconfig", "-o", "/boot/grub/grub.cfg"), "grub-mkconfig");
        s.Log("GRUB instalado no disco novo.");
    }
}
