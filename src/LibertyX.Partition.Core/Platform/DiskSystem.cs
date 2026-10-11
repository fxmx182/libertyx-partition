using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Platform.Windows;

namespace LibertyX.Partition.Core.Platform;

/// <summary>
/// Ponto de entrada: discos reais do sistema + imagens anexadas pelo usuário
/// (.img/.dd/.raw/.bin/.iso), que entram na lista como se fossem discos.
/// </summary>
public static class DiskSystem
{
    public static IDiskSystem Native { get; } = OperatingSystem.IsWindows() ? new WinDiskSystem() : new LinuxDiskSystem();
    public static List<string> AttachedImages { get; } = [];

    public static List<DiskInfo> Enumerate()
    {
        var list = new List<DiskInfo>();
        if (Environment.GetEnvironmentVariable("LIBERTYX_ONLY_IMAGES") != "1")
            try { list.AddRange(Native.Enumerate()); } catch { }
        int n = 100;
        foreach (var path in AttachedImages.ToList())
        {
            var d = new DiskInfo { Number = n++, IsImage = true, ImagePath = path, DevicePath = path, Model = Path.GetFileName(path), Bus = "Imagem", ReadOnly = true };
            try
            {
                using var dev = new ImageFileDevice(path);
                d.Size = dev.Length;
                LayoutBuilder.Build(d, dev);
                foreach (var p in d.Partitions.Where(p => !p.IsUnallocated && p.FileSystem != FileSystems.FsKind.Unknown))
                {
                    var u = UsageCalculator.Compute(dev.Slice(p.Offset, p.Length), p.FileSystem);
                    if (u != null) { p.Used = u.Value.used; p.Free = u.Value.free; p.Label = u.Value.label; }
                }
            }
            catch (Exception ex) { d.Error = ex.Message; }
            list.Add(d);
        }
        return list;
    }

    /// <summary>
    /// Impressão digital barata do hardware de armazenamento (quais discos existem e de que
    /// tamanho, quais letras estão montadas). Muda quando um pendrive entra ou sai, ou quando
    /// um cartão é posto no leitor — é o gatilho do plug and play, sem reler tudo a cada segundo.
    /// </summary>
    public static string Signature()
    {
        var sb = new System.Text.StringBuilder();
        if (OperatingSystem.IsWindows()) sb.Append(WinDiskSystem.QuickSignature());
        else if (Directory.Exists("/sys/block"))
            foreach (var dir in Directory.GetDirectories("/sys/block").OrderBy(x => x))
            {
                try { sb.Append(Path.GetFileName(dir)).Append(':').Append(File.ReadAllText(Path.Combine(dir, "size")).Trim()).Append(';'); } catch { }
            }
        try { sb.Append('|').Append(string.Join(",", Environment.GetLogicalDrives())); } catch { }
        sb.Append('|').Append(string.Join(",", AttachedImages));
        return sb.ToString();
    }

    public static IBlockDevice OpenDisk(DiskInfo d, bool write) =>
        d.IsImage ? new ImageFileDevice(d.ImagePath!, write, d.SectorSize) : Native.OpenDisk(d, write);

    public static IBlockDevice OpenPartition(PartitionInfo p, bool write = false)
    {
        if (p.Disk.IsImage) return new OwnedSlice(new ImageFileDevice(p.Disk.ImagePath!, write, p.Disk.SectorSize), p.Offset, p.Length);
        return Native.OpenPartition(p, write);
    }
}

/// <summary>Usado só no desenvolvimento (Linux): discos de /sys/block, leitura direta se houver permissão.</summary>
public sealed class LinuxDiskSystem : IDiskSystem
{
    public bool IsElevated => Environment.UserName == "root";

    public List<DiskInfo> Enumerate()
    {
        var res = new List<DiskInfo>();
        if (!Directory.Exists("/sys/block")) return res;
        int n = 0;
        foreach (var dir in Directory.GetDirectories("/sys/block").OrderBy(x => x))
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith("loop") || name.StartsWith("ram") || name.StartsWith("dm-") || name.StartsWith("zram") || name.StartsWith("sr")) continue;
            string Read(string f) { try { return File.ReadAllText(Path.Combine(dir, f)).Trim(); } catch { return ""; } }
            long.TryParse(Read("size"), out long sectors);
            if (sectors == 0) continue;
            var d = new DiskInfo
            {
                Number = n++, DevicePath = "/dev/" + name, Model = Read("device/model") is { Length: > 0 } m ? m : name,
                Size = sectors * 512, SectorSize = int.TryParse(Read("queue/logical_block_size"), out var ls) ? ls : 512,
                PhysicalSectorSize = int.TryParse(Read("queue/physical_block_size"), out var ps) ? ps : 512,
                Removable = Read("removable") == "1", IsSsd = Read("queue/rotational") == "0",
                Bus = name.StartsWith("nvme") ? "NVMe" : Read("removable") == "1" ? "USB" : "SATA", Serial = Read("device/serial"),
            };
            try
            {
                using var dev = OpenDisk(d, false);
                LayoutBuilder.Build(d, dev);
            }
            catch (Exception ex) { d.Error = ex.Message; }
            res.Add(d);
        }
        return res;
    }

    public IBlockDevice OpenDisk(DiskInfo d, bool write) => new ImageFileDevice(d.DevicePath, write, d.SectorSize, d.Size);
    public IBlockDevice OpenPartition(PartitionInfo p, bool write = false) => new OwnedSlice(OpenDisk(p.Disk, write), p.Offset, p.Length);
    public IDisposable LockVolumes(DiskInfo d, IEnumerable<PartitionInfo>? only = null) => new Nothing();
    public void Rescan(DiskInfo d) { }
    public SmartReport ReadSmart(DiskInfo d) => new() { Summary = "S.M.A.R.T. só é lido no Windows." };
    private sealed class Nothing : IDisposable { public void Dispose() { } }
}
