using System.Runtime.Versioning;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using static LibertyX.Partition.Core.Platform.Linux.LinuxNative;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Discos do Linux lidos direto do kernel: /sys/block (geometria, partições, quem segura cada
/// uma), /proc/self/mountinfo e /proc/swaps (o que está em uso), e o banco do udev (modelo,
/// série, barramento, rótulo, UUID). Nada depende de lsblk ou udisks.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDiskSystem : IDiskSystem
{
    public bool IsElevated => GetEuid() == 0;

    /// <summary>Mostrar /dev/loop* como discos (autoteste e quem trabalha com imagens montadas por losetup).</summary>
    public static bool ShowLoops => Environment.GetEnvironmentVariable("LIBERTYX_LOOPS") == "1";

    // ───────── o que está montado ─────────

    public sealed record Mount(string MajMin, string MountPoint, string FsType, string Source, string Options);

    public static List<Mount> ReadMounts()
    {
        var res = new List<Mount>();
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/mountinfo"))
            {
                // 36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue
                var f = line.Split(' ');
                int dash = Array.IndexOf(f, "-");
                if (f.Length < 7 || dash < 0 || dash + 2 >= f.Length) continue;
                res.Add(new Mount(f[2], Unescape(f[4]), f[dash + 1], Unescape(f[dash + 2]), f[5]));
            }
        }
        catch { }
        return res;
    }

    private static string Unescape(string s) => !s.Contains('\\') ? s
        : System.Text.RegularExpressions.Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    /// <summary>Nomes do kernel (sda2, dm-1) com swap ativa.</summary>
    public static HashSet<string> ActiveSwaps()
    {
        var s = new HashSet<string>();
        try
        {
            foreach (var line in File.ReadAllLines("/proc/swaps").Skip(1))
            {
                var dev = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (dev == null || !dev.StartsWith("/dev/")) continue;
                s.Add(KernelNameOf(dev) ?? dev[5..]);
            }
        }
        catch { }
        return s;
    }

    /// <summary>/dev/mapper/vg-root ou /dev/disk/by-uuid/… → dm-0, sda2.</summary>
    public static string? KernelNameOf(string devPath)
    {
        try
        {
            var fi = new FileInfo(devPath);
            string full = fi.LinkTarget != null ? Path.GetFullPath(fi.ResolveLinkTarget(true)!.FullName) : devPath;
            if (full.StartsWith(DevNodes.AltDir + "/")) return Path.GetFileName(full);
            return full.StartsWith("/dev/") ? full[5..] : null;
        }
        catch { return null; }
    }

    private static string Sys(string path) { try { return File.ReadAllText(path).Trim(); } catch { return ""; } }

    /// <summary>Banco do udev: E:ID_MODEL=…, E:ID_SERIAL_SHORT=…, E:ID_FS_LABEL=…</summary>
    public static Dictionary<string, string> Udev(string majMin)
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadAllLines($"/run/udev/data/b{majMin}"))
                if (line.StartsWith("E:"))
                {
                    int i = line.IndexOf('=');
                    if (i > 2) d[line[2..i]] = line[(i + 1)..];
                }
        }
        catch { }
        return d;
    }

    private static string UdevText(string v) => System.Text.RegularExpressions.Regex.Replace(v, @"\\x([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()).Trim();

    /// <summary>Tudo o que está "em cima" de um dispositivo: dm-crypt, LVM, RAID — recursivo.</summary>
    public static List<string> HoldersOf(string kernelName)
    {
        var res = new List<string>();
        void Walk(string n, int depth)
        {
            if (depth > 6) return;
            string dir = $"/sys/class/block/{n}/holders";
            if (!Directory.Exists(dir)) return;
            foreach (var h in Directory.GetFileSystemEntries(dir).Select(Path.GetFileName))
            {
                if (h == null || res.Contains(h)) continue;
                res.Add(h);
                Walk(h, depth + 1);
            }
        }
        Walk(kernelName, 0);
        return res;
    }

    /// <summary>As partições/discos físicos debaixo de um dm/md (o contrário de holders).</summary>
    public static List<string> SlavesOf(string kernelName)
    {
        var res = new List<string>();
        void Walk(string n, int depth)
        {
            string dir = $"/sys/class/block/{n}/slaves";
            if (depth > 6 || !Directory.Exists(dir)) { if (depth > 0) res.Add(n); return; }
            var s = Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Where(x => x != null).ToList();
            if (s.Count == 0) { if (depth > 0) res.Add(n); return; }
            foreach (var x in s) Walk(x!, depth + 1);
        }
        Walk(kernelName, 0);
        return res;
    }

    private static string DmLabel(string dm)
    {
        string name = Sys($"/sys/class/block/{dm}/dm/name"), uuid = Sys($"/sys/class/block/{dm}/dm/uuid");
        if (uuid.StartsWith("CRYPT-BITLK")) return $"BitLocker aberto ({name})";
        if (uuid.StartsWith("CRYPT-")) return $"LUKS aberto ({name})";
        if (uuid.StartsWith("LVM-")) return $"LVM ({name})";
        return dm.StartsWith("md") ? $"RAID {dm}" : name.Length > 0 ? name : dm;
    }

    private static string DmKind(string dm)
    {
        if (dm.StartsWith("md")) return "RAID";
        string uuid = Sys($"/sys/class/block/{dm}/dm/uuid");
        return uuid.StartsWith("CRYPT-BITLK") ? "BitLocker" : uuid.StartsWith("CRYPT-") ? "LUKS" : uuid.StartsWith("LVM-") ? "LVM" : "Mapeado";
    }

    /// <summary>Número estável por nome do kernel: o mesmo pendrive não troca de número quando outro entra.</summary>
    public static int StableNumber(string name)
    {
        static int Letters(string s) { int v = 0; foreach (char c in s) { if (c < 'a' || c > 'z') return -1; v = v * 26 + (c - 'a' + 1); } return v - 1; }
        static int Num(string s) => int.TryParse(s, out var v) ? v : 0;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(sd|vd|xvd|hd)([a-z]+)$");
        if (m.Success) return m.Groups[1].Value switch { "sd" => 0, "vd" => 1000, "xvd" => 2000, _ => 3000 } + Letters(m.Groups[2].Value);
        m = System.Text.RegularExpressions.Regex.Match(name, @"^nvme(\d+)n(\d+)$");
        if (m.Success) return 4000 + Num(m.Groups[1].Value) * 16 + Num(m.Groups[2].Value);
        m = System.Text.RegularExpressions.Regex.Match(name, @"^(mmcblk|loop|dm-|md|nbd|zd)(\d+)$");
        if (m.Success) return m.Groups[1].Value switch { "mmcblk" => 5000, "loop" => 6000, "dm-" => 7000, "md" => 8000, "nbd" => 9000, _ => 9500 } + Num(m.Groups[2].Value);
        return 9900 + Math.Abs(name.GetHashCode() % 99);
    }

    private static bool SkipDisk(string n) =>
        n.StartsWith("ram") || n.StartsWith("zram") || n.StartsWith("sr") || n.StartsWith("fd") || n.StartsWith("mtdblock")
        || (n.StartsWith("loop") && !ShowLoops) || System.Text.RegularExpressions.Regex.IsMatch(n, @"^mmcblk\d+(boot\d|rpmb)$");

    private static string BusOf(string name, string sysPath, Dictionary<string, string> u)
    {
        string real = "";
        try { real = new DirectoryInfo(sysPath).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(sysPath); } catch { }
        if (name.StartsWith("nvme")) return "NVMe";
        if (real.Contains("/usb") || u.GetValueOrDefault("ID_BUS") == "usb") return "USB";
        if (name.StartsWith("mmcblk")) return Sys($"/sys/block/{name}/device/type") == "MMC" ? "MMC" : "SD";
        if (name.StartsWith("vd") || real.Contains("/virtio")) return "Virtio";
        if (name.StartsWith("loop")) return "Loop";
        if (name.StartsWith("nbd")) return "Rede (NBD)";
        if (real.Contains("/ata")) return "SATA";
        if (u.GetValueOrDefault("ID_BUS") is "scsi" || real.Contains("/host")) return real.Contains("sas") ? "SAS" : "SCSI";
        return u.GetValueOrDefault("ID_BUS", "Desconhecido").ToUpperInvariant();
    }

    public List<DiskInfo> Enumerate()
    {
        var res = new List<DiskInfo>();
        if (!Directory.Exists("/sys/block")) return res;
        var mounts = ReadMounts();
        var swaps = ActiveSwaps();
        // por dispositivo do kernel: pelo maj:min quando é o do bloco, senão pela origem da montagem
        // (o Btrfs registra um número anônimo 0:NN, e só o caminho /dev/… diz de onde veio)
        var byDev = new Dictionary<string, List<Mount>>();
        foreach (var m in mounts)
        {
            string? kn = m.MajMin.StartsWith("0:") ? null : DevFromMajMin(m.MajMin);
            if (kn == null && m.Source.StartsWith("/dev/")) kn = KernelNameOf(m.Source);
            if (kn == null) continue;
            if (!byDev.TryGetValue(kn, out var l)) byDev[kn] = l = [];
            l.Add(m);
        }
        // de qual dispositivo do kernel vem o "/" (atravessando LVM/LUKS até a partição física)
        string? rootDev = null;
        var root = mounts.LastOrDefault(m => m.MountPoint == "/");
        if (root != null) rootDev = root.MajMin.StartsWith("0:") && root.Source.StartsWith("/dev/") ? KernelNameOf(root.Source) : DevFromMajMin(root.MajMin);
        var rootPhys = rootDev == null ? [] : SlavesOf(rootDev).DefaultIfEmpty(rootDev).ToHashSet();
        string[] criticalMounts = ["/", "/boot", "/boot/efi", "/efi", "/usr", "/var", "/home", "/nix"];

        foreach (var dir in Directory.GetDirectories("/sys/block").OrderBy(x => StableNumber(Path.GetFileName(x))))
        {
            string name = Path.GetFileName(dir);
            if (SkipDisk(name)) continue;
            bool virt = name.StartsWith("dm-") || name.StartsWith("md");
            long.TryParse(Sys(Path.Combine(dir, "size")), out long sectors512);
            if (sectors512 == 0) continue;
            if (name.StartsWith("loop") && Sys(Path.Combine(dir, "loop/backing_file")).Length == 0) continue;
            string majMin = Sys(Path.Combine(dir, "dev"));
            var u = Udev(majMin);
            if (virt)
            {
                // um volume lógico que tem partições em cima (raro) ou que é só membro de outro: só os de topo
                if (Directory.Exists(Path.Combine(dir, "holders")) && Directory.GetFileSystemEntries(Path.Combine(dir, "holders")).Length > 0) continue;
            }
            string model = UdevText(u.GetValueOrDefault("ID_MODEL_ENC", u.GetValueOrDefault("ID_MODEL", "")));
            if (model.Length == 0) model = Sys(Path.Combine(dir, "device/model"));
            string vendor = Sys(Path.Combine(dir, "device/vendor"));
            if (vendor.Length > 0 && vendor is not ("ATA" or "NVMe") && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) model = vendor + " " + model;
            if (name.StartsWith("mmcblk")) model = Sys(Path.Combine(dir, "device/name")) is { Length: > 0 } nm ? $"Cartão {nm}" : "Cartão SD";
            if (name.StartsWith("loop")) model = "Loop · " + Path.GetFileName(Sys(Path.Combine(dir, "loop/backing_file")));
            if (virt) model = name.StartsWith("md") ? $"RAID {Sys(Path.Combine(dir, "md/level"))} ({name})" : Sys(Path.Combine(dir, "dm/name"));
            var d = new DiskInfo
            {
                Number = StableNumber(name), DevicePath = DevNodes.Node(name), KernelName = name,
                Model = model.Trim().Length > 0 ? model.Trim() : name,
                Size = sectors512 * 512,
                SectorSize = int.TryParse(Sys(Path.Combine(dir, "queue/logical_block_size")), out var ls) && ls > 0 ? ls : 512,
                PhysicalSectorSize = int.TryParse(Sys(Path.Combine(dir, "queue/physical_block_size")), out var ps) && ps > 0 ? ps : 512,
                Removable = Sys(Path.Combine(dir, "removable")) == "1",
                IsSsd = virt ? null : Sys(Path.Combine(dir, "queue/rotational")) switch { "0" => true, "1" => false, _ => null },
                Serial = UdevText(u.GetValueOrDefault("ID_SERIAL_SHORT", Sys(Path.Combine(dir, "device/serial")))),
                Firmware = u.GetValueOrDefault("ID_REVISION", Sys(Path.Combine(dir, "device/firmware_rev"))),
                ReadOnly = Sys(Path.Combine(dir, "ro")) == "1",
                VirtualKind = virt ? DmKind(name) : null,
            };
            d.Bus = virt ? d.VirtualKind! : BusOf(name, dir, u);
            if (d.Bus is "SD" or "MMC" or "USB") d.Removable = true;
            if (name.StartsWith("nvme"))
            {
                string ctrl = System.Text.RegularExpressions.Regex.Match(name, @"^nvme\d+").Value;
                if (d.Serial.Length == 0) d.Serial = Sys($"/sys/class/nvme/{ctrl}/serial");
                if (d.Firmware.Length == 0) d.Firmware = Sys($"/sys/class/nvme/{ctrl}/firmware_rev");
                if (d.Model == name) d.Model = Sys($"/sys/class/nvme/{ctrl}/model");
            }
            d.IsSystem = rootPhys.Contains(name) || Directory.GetDirectories(dir).Any(p => rootPhys.Contains(Path.GetFileName(p)));

            // partições conhecidas pelo kernel, por posição: o que a tabela diz × o que o kernel usa
            var kparts = new List<(string name, long start, long size, string majMin)>();
            foreach (var pd in Directory.GetDirectories(dir).Where(p => File.Exists(Path.Combine(p, "partition"))))
                if (long.TryParse(Sys(Path.Combine(pd, "start")), out var st) && long.TryParse(Sys(Path.Combine(pd, "size")), out var sz))
                    kparts.Add((Path.GetFileName(pd), st * 512, sz * 512, Sys(Path.Combine(pd, "dev"))));
            try
            {
                if (d.Size > 0)
                {
                    using var dev = Bounded.Run(() => (IBlockDevice?)OpenDisk(d, false), TimeSpan.FromSeconds(15), null, out bool slow);
                    if (slow || dev == null) d.Error = "O disco não respondeu em 15 segundos — mídia com defeito ou leitor travado. Tire e ponha de novo e clique em Atualizar.";
                    else LayoutBuilder.Build(d, dev);
                }
            }
            catch (Exception ex) { d.Error = ex.Message; }

            foreach (var p in d.Partitions.Where(p => !p.IsUnallocated))
            {
                string pname = name, pmm = majMin;
                if (!p.IsWholeDisk)
                {
                    var k = kparts.FirstOrDefault(x => x.start == p.Offset);
                    if (k.name == null) { p.HeldBy = "o kernel ainda não vê esta partição"; continue; }
                    pname = k.name; pmm = k.majMin;
                    if (k.size != p.Length) p.HeldBy = $"o kernel vê {Operations.DiskOperations.Size(k.size)} — releia a tabela";
                }
                p.DeviceNode = DevNodes.Node(pname);
                p.VolumePath = p.DeviceNode;
                var pu = Udev(pmm);
                if (pu.TryGetValue("ID_FS_UUID", out var uu)) p.FsUuid = uu;
                if (string.IsNullOrEmpty(p.Label) && pu.TryGetValue("ID_FS_LABEL_ENC", out var lb)) p.Label = UdevText(lb);
                // montagens: da própria partição e do que estiver aberto em cima dela (LUKS, LVM)
                var chain = new List<(string kn, string mm)> { (pname, pmm) };
                foreach (var h in HoldersOf(pname)) chain.Add((h, Sys($"/sys/class/block/{h}/dev")));
                var holders = chain.Skip(1).Select(c => c.kn).ToList();
                if (holders.Count > 0) p.HeldBy = string.Join(" · ", holders.Select(DmLabel).Distinct());
                foreach (var (kn, mm) in chain)
                {
                    if (byDev.TryGetValue(kn, out var ms))
                        foreach (var m in ms.OrderBy(m => m.MountPoint.Length))
                            if (!p.MountPoints.Contains(m.MountPoint)) p.MountPoints.Add(m.MountPoint);
                    if (swaps.Contains(kn)) p.HasPageFile = true;
                }
                if (p.MountPoints.Contains("/")) p.IsSystemVolume = true;
                if (p.MountPoints.Any(m => criticalMounts.Contains(m))) p.InUseBySystem = true;
                // uso real do volume montado (statvfs); sem montagem, o superbloco
                var mp = p.MountPoints.FirstOrDefault();
                if (mp != null && holders.Count == 0)
                    try { var di = new DriveInfo(mp); if (di.TotalSize > 0) { p.Free = di.AvailableFreeSpace; p.Used = di.TotalSize - di.TotalFreeSpace; } } catch { }
                // o superbloco manda: o banco do udev só se atualiza quando ele processa o evento (às vezes, segundos depois)
                if (p.FileSystem == FsKind.Ext)
                    try { using var v = OpenPartition(p); if (FsDetect.ParseExt(v.ReadBytes(1024, 1024)) is { } sb) p.FsUuid = sb.Uuid.ToString(); } catch { }
                if (p.Used == null && p.FileSystem is FsKind.Xfs or FsKind.Btrfs or FsKind.F2fs or FsKind.Luks or FsKind.LinuxSwap or FsKind.LvmPv)
                    FillSuper(d, p);
                else if (p.FileSystem is FsKind.Xfs or FsKind.Btrfs or FsKind.F2fs or FsKind.Luks or FsKind.LvmPv) FillSuper(d, p, usage: false);
            }
            // partições que o kernel tem e a tabela não (tabela apagada com o volume ainda montado)
            var orphan = kparts.Where(k => !d.Partitions.Any(p => p.Offset == k.start && !p.IsUnallocated)).Select(k => k.name).ToList();
            if (orphan.Count > 0 && d.Error == null)
                d.Error = $"O kernel ainda usa {string.Join(", ", orphan)}, que não está mais na tabela. Desmonte e clique em Atualizar (ou reinicie).";
            res.Add(d);
        }
        return res;
    }

    private void FillSuper(DiskInfo d, PartitionInfo p, bool usage = true)
    {
        try
        {
            using var v = OpenPartition(p);
            var s = FsDetect.ReadSuper(v, p.FileSystem);
            if (s == null) return;
            p.FsVariant ??= s.Variant;
            if (string.IsNullOrEmpty(p.Label) && !string.IsNullOrEmpty(s.Label)) p.Label = s.Label;
            if (s.Uuid != null) p.FsUuid = s.Uuid;
            if (usage && s.Total is { } t && s.Free is { } f) { p.Used = t - f; p.Free = f; }
        }
        catch { }
    }

    public static string? DevFromMajMin(string majMin)
    {
        try
        {
            var link = new FileInfo($"/sys/dev/block/{majMin}").LinkTarget;
            return link == null ? null : Path.GetFileName(link);
        }
        catch { return null; }
    }

    /// <summary>Impressão digital barata: discos presentes, tamanhos e o que está montado.</summary>
    public static string QuickSignature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var dir in Directory.GetDirectories("/sys/block").OrderBy(x => x))
        {
            string n = Path.GetFileName(dir);
            if (SkipDisk(n)) continue;
            sb.Append(n).Append(':').Append(Sys(Path.Combine(dir, "size"))).Append(';');
            foreach (var pd in Directory.GetDirectories(dir).Where(p => File.Exists(Path.Combine(p, "partition"))))
                sb.Append(Path.GetFileName(pd)).Append('@').Append(Sys(Path.Combine(pd, "start"))).Append('+').Append(Sys(Path.Combine(pd, "size"))).Append(',');
        }
        try { sb.Append('|').Append(string.Join(",", ReadMounts().Where(m => m.Source.StartsWith("/dev/")).Select(m => m.Source + ">" + m.MountPoint))); } catch { }
        return sb.ToString();
    }

    public IBlockDevice OpenDisk(DiskInfo d, bool write) =>
        d.IsImage ? new ImageFileDevice(d.ImagePath!, write, d.SectorSize) : new LinuxDevice(d.DevicePath, write, d.SectorSize, d.Size);

    public IBlockDevice OpenPartition(PartitionInfo p, bool write = false)
    {
        if (p.Disk.IsImage) return new OwnedSlice(new ImageFileDevice(p.Disk.ImagePath!, write, p.Disk.SectorSize), p.Offset, p.Length);
        // pela partição do kernel quando ela bate com a tabela (o kernel confere os limites);
        // senão, fatia do disco — é o caso da recuperação de partição que o kernel ainda não conhece
        if (p.DeviceNode != null && File.Exists(p.DeviceNode) && !p.IsWholeDisk)
        {
            try
            {
                var dev = new LinuxDevice(p.DeviceNode, write, p.Disk.SectorSize);
                if (dev.Length == p.Length) return dev;
                dev.Dispose();
            }
            catch (IOException) { }
        }
        return new OwnedSlice(OpenDisk(p.Disk, write), p.Offset, p.Length);
    }

    // ───────── trancar: desmontar o que está em uso ─────────

    public IDisposable LockVolumes(DiskInfo d, IEnumerable<PartitionInfo>? only = null)
    {
        if (d.IsImage) return new Unlock([], null);
        var targets = (only ?? d.Partitions.Where(p => !p.IsUnallocated)).ToList();
        // estado de agora, não o da lista (o usuário pode ter montado algo depois)
        var fresh = Enumerate().FirstOrDefault(x => x.DevicePath == d.DevicePath);
        var now = fresh == null ? targets : targets.Select(t => fresh.Partitions.FirstOrDefault(f => f.Offset == t.Offset && !f.IsUnallocated) ?? t).ToList();
        var critical = now.Where(p => p.IsSystemCritical).ToList();
        if (critical.Count > 0)
            throw new IOException($"{string.Join(", ", critical.Select(p => p.KernelName ?? p.DisplayName))} está em uso pelo sistema ({string.Join(", ", critical.SelectMany(p => p.MountPoints).DefaultIfEmpty("swap"))}). Para alterá-la, inicie por um pendrive Linux.");
        var held = now.Where(p => p.HeldBy != null && p.KernelName != null && HoldersOf(p.KernelName).Count > 0).ToList();
        if (held.Count > 0)
            throw new IOException($"{string.Join(", ", held.Select(p => $"{p.KernelName} ({p.HeldBy})"))}: feche antes o que está aberto em cima (Criptografia → Fechar, ou desative o grupo LVM).");
        var inhibit = AutomountInhibitor.Start();
        var undone = new List<(string dev, string mp, string fs)>();
        try
        {
            var mounts = ReadMounts();
            foreach (var p in now)
            {
                if (p.HasPageFile && p.DeviceNode != null && SwapOff(p.DeviceNode) != 0)
                    throw new IOException($"Não foi possível desligar a swap em {p.DeviceNode}: {ErrText(Errno)}.");
                // mais fundo primeiro: /mnt/a/b antes de /mnt/a
                foreach (var mp in p.MountPoints.OrderByDescending(m => m.Length))
                {
                    string fs = mounts.FirstOrDefault(m => m.MountPoint == mp)?.FsType ?? "auto";
                    if (Umount2(mp, 0) != 0)
                    {
                        int e = Errno;
                        if (e == EBUSY)
                        {
                            var who = WhoUses(mp);
                            throw new IOException($"{mp} está em uso{(who.Count > 0 ? " por: " + string.Join(", ", who.Take(6)) : "")}. Feche esses programas (ou janelas abertas nessa pasta) e tente de novo.");
                        }
                        if (e != EINVAL) throw new IOException($"Não foi possível desmontar {mp}: {ErrText(e)}.");
                    }
                    undone.Add((p.DeviceNode!, mp, fs));
                }
            }
        }
        catch
        {
            Remount(undone);
            inhibit?.Dispose();
            throw;
        }
        return new Unlock(undone, inhibit);
    }

    private static void Remount(List<(string dev, string mp, string fs)> list)
    {
        foreach (var (dev, mp, _) in list.AsEnumerable().Reverse())
        {
            if (!File.Exists(dev) || !Directory.Exists(mp)) continue;
            Proc.RunSync("mount", [dev, mp], TimeSpan.FromSeconds(30));
        }
    }

    private sealed class Unlock(List<(string dev, string mp, string fs)> undone, IDisposable? inhibit) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            inhibit?.Dispose();
            // volta a montar onde estava (o Windows faz isso sozinho ao destrancar)
            Remount(undone);
        }
    }

    /// <summary>Quais programas estão com arquivos ou a pasta atual dentro de <paramref name="mountPoint"/>.</summary>
    public static List<string> WhoUses(string mountPoint)
    {
        var res = new HashSet<string>();
        string pre = mountPoint.TrimEnd('/') + "/";
        bool Inside(string? t) => t != null && (t == mountPoint || t.StartsWith(pre));
        foreach (var pd in Directory.GetDirectories("/proc").Where(x => int.TryParse(Path.GetFileName(x), out _)))
        {
            try
            {
                bool hit = Inside(new DirectoryInfo(Path.Combine(pd, "cwd")).LinkTarget);
                if (!hit && Directory.Exists(Path.Combine(pd, "fd")))
                    foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(pd, "fd")))
                        if (Inside(new FileInfo(fd).LinkTarget)) { hit = true; break; }
                if (hit) res.Add($"{Sys(Path.Combine(pd, "comm"))} ({Path.GetFileName(pd)})");
            }
            catch { }
        }
        return res.ToList();
    }

    // ───────── avisar o kernel ─────────

    public void Rescan(DiskInfo d)
    {
        if (d.IsImage || d.IsVirtual) return;
        try { KernelSync.Reread(d); } catch { }
    }

    public SmartReport ReadSmart(DiskInfo d)
    {
        if (d.IsImage) return new SmartReport { Summary = "Imagem de disco: sem S.M.A.R.T." };
        if (d.IsVirtual) return new SmartReport { Summary = $"Volume {d.VirtualKind}: a saúde é a dos discos físicos que o compõem." };
        return LinuxSmart.Read(d);
    }
}
