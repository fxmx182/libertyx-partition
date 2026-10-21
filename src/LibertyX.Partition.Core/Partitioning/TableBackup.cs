using System.Text.Json;
using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Partitioning;

/// <summary>
/// Antes de qualquer escrita de tabela, os setores que vão mudar são salvos num arquivo
/// .lxbak — dá para desfazer qualquer operação, mesmo depois de fechar o programa.
/// </summary>
public static class TableBackup
{
    public static string Folder
    {
        get
        {
            return Platform.AppPaths.EnsureDocuments("Backups de tabela");
        }
    }

    private sealed record Header(string Disk, long DiskBytes, int SectorSize, DateTime When, string Reason, List<long[]> Ranges);

    public static string Save(IBlockDevice dev, IEnumerable<(long lba, int count)> ranges, string diskName, string reason)
    {
        int ss = dev.SectorSize;
        var list = ranges.Where(r => r.count > 0 && r.lba >= 0).Distinct().ToList();
        string safe = string.Concat(diskName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).Trim();
        string path = Path.Combine(Folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{(safe.Length > 0 ? safe : "disco")}.lxbak");
        using var fs = File.Create(path);
        var hdr = JsonSerializer.SerializeToUtf8Bytes(new Header(diskName, dev.Length, ss, DateTime.Now, reason, list.Select(r => new[] { r.lba, (long)r.count }).ToList()));
        fs.Write(BitConverter.GetBytes(hdr.Length));
        fs.Write(hdr);
        foreach (var (lba, count) in list)
        {
            var buf = new byte[count * ss];
            int n = dev.Read(lba * ss, buf);
            fs.Write(buf, 0, buf.Length);
            _ = n;
        }
        fs.Close();
        if (OperatingSystem.IsLinux()) Platform.Linux.LinuxSession.GiveBack(path, false);
        return path;
    }

    public static (string disk, long bytes, DateTime when, string reason) Describe(string path)
    {
        using var fs = File.OpenRead(path);
        var h = ReadHeader(fs);
        return (h.Disk, h.DiskBytes, h.When, h.Reason);
    }

    private static Header ReadHeader(Stream fs)
    {
        var lenB = new byte[4];
        fs.ReadExactly(lenB);
        var hb = new byte[BitConverter.ToInt32(lenB)];
        fs.ReadExactly(hb);
        return JsonSerializer.Deserialize<Header>(hb)!;
    }

    public static void Restore(IBlockDevice dev, string path)
    {
        using var fs = File.OpenRead(path);
        var h = ReadHeader(fs);
        if (h.SectorSize != dev.SectorSize) throw new InvalidOperationException("O backup é de um disco com outro tamanho de setor.");
        if (h.DiskBytes != dev.Length) throw new InvalidOperationException($"O backup é de um disco de {h.DiskBytes:N0} bytes; este tem {dev.Length:N0}.");
        foreach (var r in h.Ranges)
        {
            var buf = new byte[r[1] * h.SectorSize];
            fs.ReadExactly(buf);
            dev.Write(r[0] * h.SectorSize, buf);
        }
        dev.Flush();
    }
}
