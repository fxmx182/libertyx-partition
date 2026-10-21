using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using Microsoft.Win32.SafeHandles;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed unsafe class WinDiskSystem : IDiskSystem
{
    public bool IsElevated
    {
        get { try { return Native.IsUserAnAdmin(); } catch { return false; } }
    }

    public sealed class VolumeData
    {
        public string GuidPath = "";              // \\?\Volume{...}\
        public List<string> Paths = [];           // C:\, D:\pasta\
        public string? Label, FileSystem;
        public uint Serial;
        public long? Total, Free;
        public List<(int disk, long offset, long length)> Extents = [];
        /// <summary>As consultas ao volume passaram do limite (mídia com defeito).</summary>
        public bool NotResponding;
        public string DevicePath => GuidPath.TrimEnd('\\').Replace(@"\\?\", @"\\.\");
        public string? Letter => Paths.FirstOrDefault(p => p.Length == 3 && p[1] == ':')?[..2];
    }

    public List<VolumeData> Volumes { get; private set; } = [];

    private static string BusName(int t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        0xA => "SAS", 0xB => "SATA", 0xC => "SD", 0xD => "MMC", 0xE => "Virtual", 0xF => "VHD", 0x10 => "Espaços de Armazenamento",
        0x11 => "NVMe", 0x12 => "SCM", 0x13 => "UFS", _ => "Desconhecido",
    };

    public List<VolumeData> EnumerateVolumes()
    {
        var res = new List<VolumeData>();
        char* name = stackalloc char[300];
        char* paths = stackalloc char[2048];
        IntPtr fh = Native.FindFirstVolumeW(name, 300);
        if (fh == (IntPtr)(-1)) return res;
        try
        {
            do
            {
                var v = new VolumeData { GuidPath = new string(name) };
                if (Native.GetVolumePathNamesForVolumeNameW(v.GuidPath, paths, 2048, out _))
                {
                    var all = new string(paths, 0, 2048).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    v.Paths.AddRange(all);
                }
                // estas consultas leem a mídia: num cartão com defeito ficam presas — 5 s no máximo
                Bounded.Run(() => { FillVolume(v); return true; }, TimeSpan.FromSeconds(5), false, out bool slow);
                if (slow) v.NotResponding = true;
                res.Add(v);
            } while (Native.FindNextVolumeW(fh, name, 300));
        }
        finally { Native.FindVolumeClose(fh); }
        return res;
    }

    private static void FillVolume(VolumeData v)
    {
        char* lbl = stackalloc char[261];
        char* fs = stackalloc char[261];
        if (Native.GetVolumeInformationW(v.GuidPath, lbl, 261, out uint serial, out _, out _, fs, 261))
        {
            v.Label = new string(lbl);
            v.FileSystem = new string(fs);
            v.Serial = serial;
            if (Native.GetDiskFreeSpaceExW(v.GuidPath, out _, out ulong total, out ulong free)) { v.Total = (long)total; v.Free = (long)free; }
        }
        using var h = Native.Open(v.DevicePath, 0);
        if (h.IsInvalid) return;
        var ext = Native.Ioctl(h, Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 256);
        if (ext is { Length: >= 8 })
        {
            int n = (int)U32(ext, 0);
            var list = new List<(int, long, long)>();
            for (int i = 0; i < n && 8 + i * 24 + 24 <= ext.Length; i++)
                list.Add(((int)U32(ext, 8 + i * 24), I64(ext, 8 + i * 24 + 8), I64(ext, 8 + i * 24 + 16)));
            lock (v) v.Extents.AddRange(list);
        }
    }

    private static byte[] PropertyQuery(int propertyId, int extra = 0)
    {
        var q = new byte[12 + extra];
        BitConverter.TryWriteBytes(q.AsSpan(0), propertyId);
        return q;
    }

    public List<DiskInfo> Enumerate()
    {
        Volumes = EnumerateVolumes();
        string winDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var winVol = Volumes.FirstOrDefault(v => v.Paths.Any(p => p.Equals(winDrive, StringComparison.OrdinalIgnoreCase)));
        var pageFileDrives = PageFileDrives();
        var disks = new List<DiskInfo>();

        for (int n = 0; n < 64; n++)
        {
            int num = n;
            // um disco que não responde não pode segurar a lista inteira: 20 s por disco
            var d = Bounded.Run(() => ReadDisk(num, winVol, pageFileDrives), TimeSpan.FromSeconds(20), null, out bool slow);
            if (slow)
            {
                d = new DiskInfo { Number = n, DevicePath = $@"\\.\PhysicalDrive{n}", Model = "Disco que não respondeu", Size = 0,
                    Error = "O disco não respondeu em 20 segundos — cartão ou pendrive com defeito, ou um leitor travado. Tire e ponha de novo e clique em Atualizar." };
                // ainda sem tabela: o painel mostra o aviso e nenhuma ação de escrita
            }
            if (d != null) disks.Add(d);
        }
        return disks;
    }

    private DiskInfo? ReadDisk(int n, VolumeData? winVol, HashSet<char> pageFileDrives)
    {
        {
            string path = $@"\\.\PhysicalDrive{n}";
            using var h = Native.Open(path, 0);
            if (h.IsInvalid) return null;
            var d = new DiskInfo { Number = n, DevicePath = path };
            var geo = Native.Ioctl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 256);
            if (geo is { Length: >= 32 })
            {
                d.SectorSize = (int)U32(geo, 20);
                d.Size = I64(geo, 24);
                if (d.SectorSize is not (512 or 1024 or 2048 or 4096)) d.SectorSize = 512;
            }
            else return null; // leitor de cartão vazio etc.
            var desc = Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, PropertyQuery(0), 1024);
            if (desc is { Length: >= 36 })
            {
                d.Removable = desc[10] != 0;
                string vendor = Native.AnsiAt(desc, (int)U32(desc, 12)) ?? "";
                string product = Native.AnsiAt(desc, (int)U32(desc, 16)) ?? "";
                d.Firmware = Native.AnsiAt(desc, (int)U32(desc, 20)) ?? "";
                d.Serial = Native.AnsiAt(desc, (int)U32(desc, 24)) ?? "";
                d.Bus = BusName((int)U32(desc, 28));
                string model = (vendor.Length > 0 && !product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? vendor + " " : "") + product;
                d.Model = model.Trim().Length > 0 ? model.Trim() : "Disco";
            }
            var seek = Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, PropertyQuery(7), 64);
            if (seek is { Length: >= 9 }) d.IsSsd = seek[8] == 0;
            var align = Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, PropertyQuery(6), 64);
            if (align is { Length: >= 24 }) d.PhysicalSectorSize = (int)U32(align, 20);

            d.IsSystem = winVol?.Extents.Any(e => e.disk == n) == true;
            try
            {
                using var dev = new WinDevice(path, false, d.SectorSize, d.Size);
                LayoutBuilder.Build(d, dev);
                foreach (var p in d.Partitions.Where(p => !p.IsUnallocated))
                {
                    var vol = Volumes.FirstOrDefault(v => v.Extents.Any(e => e.disk == n && e.offset == p.Offset));
                    if (vol != null)
                    {
                        p.VolumePath = vol.GuidPath;
                        p.Letter = vol.Letter;
                        p.MountPoints.AddRange(vol.Paths);
                        if (!string.IsNullOrEmpty(vol.Label)) p.Label = vol.Label;
                        p.VolumeSerial = vol.Serial;
                        if (vol.Total is { } t && vol.Free is { } f) { p.Used = t - f; p.Free = f; }
                        p.IsSystemVolume = vol == winVol;
                        p.HasPageFile = vol.Letter != null && pageFileDrives.Contains(vol.Letter[0]);
                        if (p.FileSystem == FsKind.BitLocker && vol.FileSystem is "NTFS" or "FAT32" or "exFAT") p.BitLocker = true;
                    }
                    if (p.FileSystem == FsKind.BitLocker) p.BitLocker = true;
                }
            }
            catch (Exception ex) { d.Error = ex.Message; }
            var stuck = Volumes.Where(v => v.NotResponding && v.Extents.Any(e => e.disk == n)).ToList();
            if (stuck.Count > 0 && d.Error == null) d.Error = "Um volume deste disco não responde ao Windows (mídia com defeito ou formatação interrompida) — formate de novo ou tire e ponha o cartão.";
            return d;
        }
    }

    private static HashSet<char> PageFileDrives()
    {
        var s = new HashSet<char>();
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            if (k?.GetValue("ExistingPageFiles") is string[] files)
                foreach (var f in files)
                {
                    var t = f.Replace(@"\??\", "");
                    if (t.Length > 1 && t[1] == ':') s.Add(char.ToUpperInvariant(t[0]));
                }
        }
        catch { }
        return s;
    }

    /// <summary>Discos presentes e seus tamanhos, só com consultas que não leem a mídia.</summary>
    public static string QuickSignature()
    {
        var sb = new System.Text.StringBuilder();
        for (int n = 0; n < 64; n++)
        {
            using var h = Native.Open($@"\\.\PhysicalDrive{n}", 0);
            if (h.IsInvalid) continue;
            var geo = Native.Ioctl(h, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 256);
            sb.Append(n).Append(':').Append(geo is { Length: >= 32 } ? I64(geo, 24) : -1).Append(';');
        }
        return sb.ToString();
    }

    public IBlockDevice OpenDisk(DiskInfo d, bool write)
    {
        if (d.IsImage) return new ImageFileDevice(d.ImagePath!, write, d.SectorSize);
        return new WinDevice(d.DevicePath, write, d.SectorSize, d.Size);
    }

    public IBlockDevice OpenPartition(PartitionInfo p, bool write = false)
    {
        if (!write && p.VolumePath != null && !p.Disk.IsImage)
        {
            try
            {
                var vol = Volumes.FirstOrDefault(v => v.GuidPath == p.VolumePath);
                if (vol != null) return new WinDevice(vol.DevicePath, false, p.Disk.SectorSize, null, isVolume: true);
            }
            catch { }
        }
        var disk = OpenDisk(p.Disk, write);
        return new OwnedSlice(disk, p.Offset, p.Length);
    }

    public IDisposable LockVolumes(DiskInfo d, IEnumerable<PartitionInfo>? only = null)
    {
        if (d.IsImage) return new Locks([]);
        Volumes = EnumerateVolumes();
        var targets = Volumes.Where(v => v.Extents.Any(e => e.disk == d.Number &&
            (only == null || only.Any(p => p.Offset == e.offset)))).ToList();
        var handles = new List<SafeFileHandle>();
        try
        {
            foreach (var v in targets)
            {
                var h = Native.Open(v.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE);
                if (h.IsInvalid) throw new IOException($"Não foi possível abrir o volume {v.Letter ?? v.GuidPath}.");
                handles.Add(h);
                bool locked = false;
                for (int i = 0; i < 10 && !locked; i++)
                {
                    locked = Native.IoctlNoData(h, Native.FSCTL_LOCK_VOLUME);
                    if (!locked) Thread.Sleep(300);
                }
                if (!locked)
                    throw new IOException($"O volume {v.Letter ?? v.Label ?? v.GuidPath} está em uso. Feche os programas e janelas que usam esse disco e tente de novo.");
                Native.IoctlNoData(h, Native.FSCTL_DISMOUNT_VOLUME);
            }
        }
        catch
        {
            foreach (var h in handles) h.Dispose();
            throw;
        }
        return new Locks(handles);
    }

    private sealed class Locks(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var h in handles)
            {
                Native.IoctlNoData(h, Native.FSCTL_UNLOCK_VOLUME);
                h.Dispose();
            }
        }
    }

    public void Rescan(DiskInfo d)
    {
        if (d.IsImage) return;
        using var h = Native.Open(d.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE);
        if (!h.IsInvalid) Native.IoctlNoData(h, Native.IOCTL_DISK_UPDATE_PROPERTIES);
    }

    public SmartReport ReadSmart(DiskInfo d)
    {
        if (d.IsImage) return new SmartReport { Summary = "Imagem de disco: sem S.M.A.R.T." };
        using var h = Native.Open(d.DevicePath, Native.GENERIC_READ | Native.GENERIC_WRITE);
        if (h.IsInvalid) return new SmartReport();
        if (d.Bus == "NVMe")
        {
            // STORAGE_PROPERTY_QUERY + STORAGE_PROTOCOL_SPECIFIC_DATA + 512 bytes de log
            var q = new byte[8 + 40 + 512];
            BitConverter.TryWriteBytes(q.AsSpan(0), 50);  // StorageDeviceProtocolSpecificProperty
            BitConverter.TryWriteBytes(q.AsSpan(4), 0);   // PropertyStandardQuery
            BitConverter.TryWriteBytes(q.AsSpan(8), 3);   // ProtocolTypeNvme
            BitConverter.TryWriteBytes(q.AsSpan(12), 2);  // NVMeDataTypeLogPage
            BitConverter.TryWriteBytes(q.AsSpan(16), 2);  // NVME_LOG_PAGE_HEALTH_INFO
            BitConverter.TryWriteBytes(q.AsSpan(24), 40); // ProtocolDataOffset
            BitConverter.TryWriteBytes(q.AsSpan(28), 512);
            var outb = Native.Ioctl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, q, q.Length);
            if (outb is { Length: >= 48 })
            {
                int dataOfs = 8 + (int)U32(outb, 8 + 16);
                if (dataOfs + 512 <= outb.Length) return SmartParser.ParseNvme(outb[dataOfs..(dataOfs + 512)]);
            }
        }
        var vals = AtaSmart(h, d.Number, 0xD0);
        if (vals != null) return SmartParser.ParseAta(vals, AtaSmart(h, d.Number, 0xD1));
        // último recurso: só o "vai falhar / não vai falhar"
        var pf = Native.Ioctl(h, Native.IOCTL_STORAGE_PREDICT_FAILURE, null, 516);
        if (pf is { Length: >= 4 })
            return new SmartReport
            {
                Health = U32(pf, 0) != 0 ? HealthLevel.Bad : HealthLevel.Good, Source = "Previsão de falha do Windows",
                Summary = U32(pf, 0) != 0 ? "O disco prevê falha. Faça uma imagem dele agora." : "Sem previsão de falha (dados detalhados indisponíveis).",
            };
        return new SmartReport();
    }

    private static byte[]? AtaSmart(SafeFileHandle h, int drive, byte feature)
    {
        var inp = new byte[33];
        BitConverter.TryWriteBytes(inp.AsSpan(0), 512);   // cBufferSize
        inp[4] = feature;                                 // bFeaturesReg
        inp[5] = 1; inp[6] = 1;                           // count, number
        inp[7] = 0x4F; inp[8] = 0xC2;                     // cyl low/high: assinatura SMART
        inp[9] = (byte)(0xA0 | ((drive & 1) << 4));       // drive/head
        inp[10] = 0xB0;                                   // SMART
        inp[12] = (byte)drive;                            // bDriveNumber
        var outb = Native.Ioctl(h, Native.SMART_RCV_DRIVE_DATA, inp, 16 + 512);
        if (outb == null || outb.Length < 16 + 362) return null;
        var data = outb[16..];
        return data.Skip(2).Take(360).Any(b => b != 0) ? data : null;
    }
}
