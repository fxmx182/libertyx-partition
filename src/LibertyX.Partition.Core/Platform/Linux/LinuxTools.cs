namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// As ferramentas do sistema que o LibertyX usa no Linux (mkfs.*, fsck, resize2fs, cryptsetup…),
/// onde estão, e de que pacote vêm em cada família de distribuição — para a tela dizer
/// exatamente o que falta e instalar com um clique.
/// </summary>
public static class LinuxTools
{
    public sealed record Tool(string Exe, string Purpose, string Group, Dictionary<string, string> Packages);

    private static Dictionary<string, string> P(string all, string? fedora = null, string? arch = null, string? suse = null, string? debian = null, string? alpine = null) => new()
    {
        ["debian"] = debian ?? all, ["fedora"] = fedora ?? all, ["arch"] = arch ?? all, ["suse"] = suse ?? all, ["alpine"] = alpine ?? all, ["void"] = all,
    };

    public static readonly List<Tool> Catalog =
    [
        new("mkfs.ext4", L.T("Formatar ext2/ext3/ext4"), "Ext2/3/4", P("e2fsprogs")),
        new("e2fsck", L.T("Verificar e reparar ext"), "Ext2/3/4", P("e2fsprogs")),
        new("resize2fs", L.T("Redimensionar ext"), "Ext2/3/4", P("e2fsprogs")),
        new("tune2fs", L.T("Rótulo, UUID e opções de ext"), "Ext2/3/4", P("e2fsprogs")),
        new("mkfs.xfs", L.T("Formatar XFS"), "XFS", P("xfsprogs")),
        new("xfs_repair", L.T("Reparar XFS"), "XFS", P("xfsprogs")),
        new("xfs_growfs", L.T("Aumentar XFS"), "XFS", P("xfsprogs")),
        new("xfs_admin", L.T("Rótulo e UUID de XFS"), "XFS", P("xfsprogs")),
        new("mkfs.btrfs", L.T("Formatar Btrfs"), L.T("Btrfs"), P("btrfs-progs")),
        new("btrfs", L.T("Verificar, redimensionar e rotular Btrfs"), L.T("Btrfs"), P("btrfs-progs")),
        new("btrfstune", L.T("UUID de Btrfs"), L.T("Btrfs"), P("btrfs-progs")),
        new("mkfs.fat", L.T("Formatar FAT16/FAT32"), "FAT", P("dosfstools")),
        new("fsck.fat", L.T("Verificar FAT"), "FAT", P("dosfstools")),
        new("fatlabel", L.T("Rótulo de FAT"), "FAT", P("dosfstools")),
        new("fatresize", L.T("Redimensionar FAT"), "FAT", P("fatresize")),
        new("mkfs.exfat", L.T("Formatar exFAT"), "exFAT", P("exfatprogs")),
        new("fsck.exfat", L.T("Verificar exFAT"), "exFAT", P("exfatprogs")),
        new("tune.exfat", L.T("Rótulo de exFAT"), "exFAT", P("exfatprogs")),
        new("mkntfs", L.T("Formatar NTFS"), "NTFS", P("ntfs-3g", fedora: "ntfsprogs", suse: "ntfsprogs", alpine: "ntfs-3g-progs")),
        new("ntfsresize", L.T("Redimensionar NTFS"), "NTFS", P("ntfs-3g", fedora: "ntfsprogs", suse: "ntfsprogs", alpine: "ntfs-3g-progs")),
        new("ntfsfix", L.T("Corrigir NTFS (marca para o chkdsk do Windows)"), "NTFS", P("ntfs-3g", fedora: "ntfsprogs", suse: "ntfsprogs", alpine: "ntfs-3g-progs")),
        new("ntfslabel", L.T("Rótulo de NTFS"), "NTFS", P("ntfs-3g", fedora: "ntfsprogs", suse: "ntfsprogs", alpine: "ntfs-3g-progs")),
        new("mkfs.f2fs", L.T("Formatar F2FS (cartões e pendrives)"), "F2FS", P("f2fs-tools")),
        new("fsck.f2fs", L.T("Verificar F2FS"), "F2FS", P("f2fs-tools")),
        new("mkswap", L.T("Criar área de troca (swap)"), L.T("Básico"), P("util-linux")),
        new("fstrim", L.T("TRIM do espaço livre em SSD (Otimizar)"), L.T("Básico"), P("util-linux")),
        new("e4defrag", L.T("Desfragmentar ext4 em HD (Otimizar)"), "Ext2/3/4", P("e2fsprogs")),
        new("xfs_fsr", L.T("Desfragmentar XFS em HD (Otimizar)"), "XFS", P("xfsprogs")),
        new("wipefs", L.T("Apagar assinaturas de sistemas de arquivos"), L.T("Básico"), P("util-linux")),
        new("mount", L.T("Montar e desmontar"), L.T("Básico"), P("util-linux", debian: "mount")),
        new("udevadm", L.T("Esperar o sistema criar /dev depois de mudanças"), L.T("Básico"), P("systemd", alpine: "eudev")),
        new("cryptsetup", L.T("LUKS e BitLocker (abrir, fechar, criar, chaves)"), L.T("Criptografia"), P("cryptsetup")),
        new("lvm", L.T("Volumes lógicos LVM"), L.T("LVM e RAID"), P("lvm2")),
        new("mdadm", L.T("RAID por software"), L.T("LVM e RAID"), P("mdadm")),
        new("smartctl", L.T("S.M.A.R.T. de adaptadores USB difíceis"), L.T("Saúde"), P("smartmontools")),
        new("nvme", L.T("Apagamento seguro de SSD NVMe"), L.T("Saúde"), P("nvme-cli")),
        new("hdparm", L.T("Apagamento seguro de SSD SATA"), L.T("Saúde"), P("hdparm")),
        new("rsync", L.T("Copiar arquivos (unir, cluster, migrar)"), L.T("Arquivos"), P("rsync")),
        new("wimlib-imagex", L.T("Dividir install.wim (pendrive do Windows)"), L.T("Pendrive de boot"), P("wimtools", fedora: "wimlib-utils", arch: "wimlib", suse: "wimlib")),
        new("grub-install", L.T("Reinstalar o GRUB (migrar sistema, reparar boot)"), "Boot", P("grub2-common", debian: "grub2-common", fedora: "grub2-tools", arch: "grub", suse: "grub2")),
    ];

    private static readonly string[] ExtraDirs = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];
    private static readonly Dictionary<string, string?> Cache = [];

    /// <summary>Caminho completo de uma ferramenta, ou nulo. grub-install também vale como grub2-install.</summary>
    public static string? Find(string exe)
    {
        lock (Cache) if (Cache.TryGetValue(exe, out var hit)) return hit;
        string? found = null;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(ExtraDirs).Distinct();
        foreach (var name in exe == "grub-install" ? new[] { "grub-install", "grub2-install" } : [exe])
        {
            foreach (var d in dirs)
            {
                var full = Path.Combine(d, name);
                if (File.Exists(full)) { found = full; break; }
            }
            if (found != null) break;
        }
        lock (Cache) Cache[exe] = found;
        return found;
    }

    public static void Forget() { lock (Cache) Cache.Clear(); }

    public static bool Has(string exe) => Find(exe) != null;

    // ───────── distribuição ─────────

    public sealed record Distro(string Id, string Name, string Family);

    private static Distro? _distro;
    public static Distro Current => _distro ??= ReadDistro();

    private static Distro ReadDistro()
    {
        var kv = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadAllLines(File.Exists("/etc/os-release") ? "/etc/os-release" : "/usr/lib/os-release"))
            {
                int i = line.IndexOf('=');
                if (i > 0) kv[line[..i]] = line[(i + 1)..].Trim('"');
            }
        }
        catch { }
        string id = kv.GetValueOrDefault("ID", "linux");
        string like = id + " " + kv.GetValueOrDefault("ID_LIKE", "");
        string fam = like.Contains("debian") || like.Contains("ubuntu") ? "debian"
            : like.Contains("fedora") || like.Contains("rhel") || like.Contains("centos") ? "fedora"
            : like.Contains("arch") ? "arch"
            : like.Contains("suse") ? "suse"
            : like.Contains("alpine") ? "alpine"
            : like.Contains("void") ? "void" : "debian";
        return new Distro(id, kv.GetValueOrDefault("PRETTY_NAME", L.T("Linux")), fam);
    }

    public static string PackageOf(string exe)
    {
        var t = Catalog.FirstOrDefault(x => x.Exe == exe);
        return t == null ? exe : t.Packages.GetValueOrDefault(Current.Family, t.Packages["debian"]);
    }

    /// <summary>A linha de comando que instala os pacotes nesta distribuição (para mostrar e para rodar).</summary>
    public static (string exe, List<string> args)? InstallCommand(IEnumerable<string> packages)
    {
        var pk = packages.Distinct().ToList();
        if (pk.Count == 0) return null;
        return Current.Family switch
        {
            "debian" when Has("apt-get") => ("apt-get", ["install", "-y", "--no-install-recommends", .. pk]),
            "fedora" when Has("dnf") => ("dnf", ["install", "-y", .. pk]),
            "fedora" when Has("yum") => ("yum", ["install", "-y", .. pk]),
            "arch" when Has("pacman") => ("pacman", ["-S", "--noconfirm", "--needed", .. pk]),
            "suse" when Has("zypper") => ("zypper", ["--non-interactive", "install", .. pk]),
            "alpine" when Has("apk") => ("apk", ["add", .. pk]),
            "void" when Has("xbps-install") => ("xbps-install", ["-y", .. pk]),
            _ => null,
        };
    }

    public static string InstallHint(IEnumerable<string> exes)
    {
        var pk = exes.Select(PackageOf).Distinct().ToList();
        var c = InstallCommand(pk);
        return c is { } x ? $"sudo {x.exe} {string.Join(' ', x.args)}" : L.T("instale: ") + string.Join(", ", pk);
    }

    /// <summary>Falta alguma? Devolve a mensagem pronta para a tela (ou nulo se está tudo lá).</summary>
    public static string? Missing(params string[] exes)
    {
        var miss = exes.Where(e => !Has(e)).ToList();
        if (miss.Count == 0) return null;
        return L.T($"Falta {string.Join(", ", miss)} no sistema. Instale em Utilidades → Ferramentas do sistema, ou: {InstallHint(miss)}");
    }

    public static async Task<CommandResult> Install(IEnumerable<string> exes, CancellationToken ct, Action<string>? onLine)
    {
        var pk = exes.Select(PackageOf).Distinct().ToList();
        var c = InstallCommand(pk);
        if (c == null) return new CommandResult(1, "", L.T("Não reconheci o gerenciador de pacotes desta distribuição. Instale: ") + string.Join(", ", pk));
        if (Current.Family == "debian") await Proc.Run("apt-get", ["update", "-q"], ct, onLine, env: new Dictionary<string, string> { ["DEBIAN_FRONTEND"] = "noninteractive" });
        var r = await Proc.Run(c.Value.exe, c.Value.args, ct, onLine, env: new Dictionary<string, string> { ["DEBIAN_FRONTEND"] = "noninteractive" });
        Forget();
        return r;
    }
}
