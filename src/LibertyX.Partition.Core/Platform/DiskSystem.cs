using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Platform.Linux;
using LibertyX.Partition.Core.Platform.Windows;

namespace LibertyX.Partition.Core.Platform;

/// <summary>
/// Ponto de entrada: discos reais do sistema + imagens anexadas pelo usuário
/// (.img/.dd/.raw/.bin/.iso), que entram na lista como se fossem discos.
/// </summary>
public static class DiskSystem
{
    public static IDiskSystem Native { get; } = OperatingSystem.IsWindows() ? new WinDiskSystem() : OperatingSystem.IsLinux() ? new LinuxDiskSystem() : new NoDiskSystem();
    public static List<string> AttachedImages { get; } = [];

    public static List<DiskInfo> Enumerate()
    {
        var list = new List<DiskInfo>();
        if (Environment.GetEnvironmentVariable("LIBERTYX_ONLY_IMAGES") != "1")
            try { list.AddRange(Native.Enumerate()); } catch { }
        int n = 100;
        foreach (var path in AttachedImages.ToList())
        {
            var d = new DiskInfo { Number = n++, IsImage = true, ImagePath = path, DevicePath = path, Model = Path.GetFileName(path), Bus = L.T("Imagem"), ReadOnly = true };
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
        if (OperatingSystem.IsWindows())
        {
            sb.Append(WinDiskSystem.QuickSignature());
            try { sb.Append('|').Append(string.Join(",", Environment.GetLogicalDrives())); } catch { }
        }
        else if (OperatingSystem.IsLinux() && Directory.Exists("/sys/block")) sb.Append(LinuxDiskSystem.QuickSignature());
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

/// <summary>Sistemas sem camada nativa (macOS no desenvolvimento): só imagens anexadas.</summary>
public sealed class NoDiskSystem : IDiskSystem
{
    public bool IsElevated => false;
    public List<DiskInfo> Enumerate() => [];
    public IBlockDevice OpenDisk(DiskInfo d, bool write) => new ImageFileDevice(d.DevicePath, write, d.SectorSize, d.Size);
    public IBlockDevice OpenPartition(PartitionInfo p, bool write = false) => new OwnedSlice(OpenDisk(p.Disk, write), p.Offset, p.Length);
    public IDisposable LockVolumes(DiskInfo d, IEnumerable<PartitionInfo>? only = null) => new Nothing();
    public void Rescan(DiskInfo d) { }
    public SmartReport ReadSmart(DiskInfo d) => new();
    private sealed class Nothing : IDisposable { public void Dispose() { } }
}

/// <summary>Fatia que fecha o disco junto — para entregar uma partição como dispositivo independente.</summary>
public sealed class OwnedSlice(IBlockDevice disk, long offset, long length) : IBlockDevice
{
    private readonly SubDevice _s = new(disk, offset, length);
    public string Name => _s.Name;
    public long Length => _s.Length;
    public int SectorSize => _s.SectorSize;
    public bool CanWrite => _s.CanWrite;
    public int Read(long o, Span<byte> b) => _s.Read(o, b);
    public void Write(long o, ReadOnlySpan<byte> b) => _s.Write(o, b);
    public void Flush() => _s.Flush();
    public void Dispose() => disk.Dispose();
}
