using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Partitioning;

namespace LibertyX.Partition.Core.Platform;

public sealed class DiskInfo
{
    public int Number { get; init; }
    public string DevicePath { get; init; } = "";
    public string Model { get; set; } = "Disco";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Bus { get; set; } = "";
    public bool Removable { get; set; }
    public bool? IsSsd { get; set; }
    public long Size { get; set; }
    public int SectorSize { get; set; } = 512;
    public int PhysicalSectorSize { get; set; } = 512;
    /// <summary>Disco onde está o Windows em uso — nada destrutivo é permitido nele.</summary>
    public bool IsSystem { get; set; }
    public bool IsImage { get; init; }
    public string? ImagePath { get; init; }
    public bool ReadOnly { get; set; }
    /// <summary>Linux: nome no kernel (sda, nvme0n1, dm-0).</summary>
    public string KernelName { get; set; } = "";
    /// <summary>Linux: volume lógico (LVM), LUKS aberto ou RAID — aparece como disco para recuperar e verificar.</summary>
    public string? VirtualKind { get; set; }
    public bool IsVirtual => VirtualKind != null;
    public PartitionTable Table { get; set; } = new();
    public List<PartitionInfo> Partitions { get; } = [];
    public SmartReport? Smart { get; set; }
    public string? Error { get; set; }

    public string Title => IsImage ? $"Imagem · {Path.GetFileName(ImagePath)}" : KernelName.Length > 0 ? DevicePath : $"Disco {Number}";
    public string StyleText => IsVirtual ? VirtualKind! : Table.Style switch { TableStyle.Gpt => "GPT", TableStyle.Mbr => "MBR", _ => "Sem tabela" };
    public string KindText => IsImage ? "Arquivo de imagem" : IsVirtual ? $"Volume {VirtualKind}" : Bus is "USB" or "SD" or "MMC" ? $"{Bus} removível" : IsSsd == true ? (Bus == "NVMe" ? "SSD NVMe" : "SSD") : IsSsd == false ? "HD" : Bus;
    /// <summary>
    /// Disco inteiro pode ser reescrito (limpar, converter, clonar por cima, excluir todas).
    /// Nunca o do sistema em uso, nem um volume lógico (LVM/LUKS) — esse se altera pela partição.
    /// </summary>
    public bool CanModify => !IsSystem && !ReadOnly && !IsVirtual;
    /// <summary>
    /// Partições podem mudar (criar no espaço livre, excluir, formatar…) desde que a própria não
    /// esteja em uso. No Linux vale também no disco do sistema, como no GParted: o kernel é
    /// avisado partição por partição. No Windows, o disco do sistema fica travado inteiro.
    /// </summary>
    public bool PartitionsEditable => !ReadOnly && !IsVirtual && (!IsSystem || !OperatingSystem.IsWindows());
}

public sealed class PartitionInfo
{
    public required DiskInfo Disk { get; init; }
    public PartitionEntry? Entry { get; init; }
    public bool IsUnallocated => Entry == null && !IsWholeDisk;
    /// <summary>Volume sem tabela de partições (pendrive formatado "direto").</summary>
    public bool IsWholeDisk { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public FsKind FileSystem { get; set; }
    public string? Label { get; set; }
    public string? Letter { get; set; }
    public List<string> MountPoints { get; } = [];
    public string? VolumePath { get; set; }
    public long? Used { get; set; }
    public long? Free { get; set; }
    public bool IsSystemVolume { get; set; }
    public bool HasPageFile { get; set; }
    public uint VolumeSerial { get; set; }
    public bool BitLocker { get; set; }
    /// <summary>ext2/ext3/ext4 quando o sistema é ext.</summary>
    public string? FsVariant { get; set; }
    /// <summary>Linux: o nó do dispositivo (/dev/sda1) e o nome curto (sda1).</summary>
    public string? DeviceNode { get; set; }
    public string? KernelName => DeviceNode?.StartsWith("/dev/") == true ? DeviceNode[5..] : null;
    public string? FsUuid { get; set; }
    /// <summary>Linux: em uso pelo sistema (/boot, /boot/efi, /usr, /var… ou swap ativa).</summary>
    public bool InUseBySystem { get; set; }
    /// <summary>Linux: aberta como LUKS, membro de LVM ou de RAID ativo (nome do mapeamento).</summary>
    public string? HeldBy { get; set; }
    public bool IsMounted => MountPoints.Count > 0;

    public long StartLba => Offset / Disk.SectorSize;
    public long Sectors => Length / Disk.SectorSize;
    public bool IsEsp => Entry != null && (Entry.TypeGuid == PartitionTypes.Esp || (Disk.Table.Style == TableStyle.Mbr && Entry.MbrType == 0xEF));
    public bool IsMsr => Entry?.TypeGuid == PartitionTypes.Msr;
    public bool IsRecovery => Entry != null && (Entry.TypeGuid == PartitionTypes.WinRecovery || Entry.MbrType == 0x27);
    public bool IsSystemCritical => IsSystemVolume || HasPageFile || InUseBySystem || (Disk.IsSystem && (IsEsp || IsMsr) && OperatingSystem.IsWindows());

    public string DisplayName => IsUnallocated ? "Não alocado" : Letter ?? KernelName ?? (Label is { Length: > 0 } ? Label : "*");

    public string TypeText => IsUnallocated ? "Espaço livre" : IsWholeDisk ? "Volume sem partição"
        : Disk.Table.Style == TableStyle.Gpt ? $"GPT · {PartitionTypes.GptName(Entry!.TypeGuid)}"
        : (Entry!.IsLogical ? "Lógica" : "Primária") + " · " + PartitionTypes.MbrName(Entry.MbrType);

    public string StatusText
    {
        get
        {
            var s = new List<string>();
            if (IsEsp) s.Add("Sistema EFI");
            if (Entry?.Active == true) s.Add("Ativa");
            if (IsSystemVolume) s.Add(OperatingSystem.IsWindows() ? "Windows" : "Sistema (/)");
            if (HasPageFile) s.Add(OperatingSystem.IsWindows() ? "Paginação" : "Swap ativa");
            if (!OperatingSystem.IsWindows() && !IsSystemVolume && MountPoints.Count > 0) s.Add("Montada em " + MountPoints[0]);
            if (HeldBy != null) s.Add(HeldBy);
            if (IsRecovery) s.Add("Recuperação");
            if (BitLocker) s.Add("BitLocker");
            return s.Count == 0 ? "—" : string.Join(" · ", s);
        }
    }
}

public interface IDiskSystem
{
    bool IsElevated { get; }
    List<DiskInfo> Enumerate();
    IBlockDevice OpenDisk(DiskInfo d, bool write);
    /// <summary>Leitura de uma partição: pelo volume montado quando existe (BitLocker já decifrado), senão direto no disco.</summary>
    IBlockDevice OpenPartition(PartitionInfo p, bool write = false);
    /// <summary>Tranca e desmonta os volumes do disco para escrita direta; devolve o que destranca.</summary>
    IDisposable LockVolumes(DiskInfo d, IEnumerable<PartitionInfo>? only = null);
    /// <summary>Pede ao sistema para reler a tabela de partições.</summary>
    void Rescan(DiskInfo d);
    SmartReport ReadSmart(DiskInfo d);
}

/// <summary>Monta a lista de partições (com lacunas livres) a partir da tabela lida.</summary>
public static class LayoutBuilder
{
    public static void Build(DiskInfo d, IBlockDevice dev)
    {
        d.Partitions.Clear();
        PartitionTable t;
        try { t = PartitionCodec.Read(dev); }
        catch (Exception ex) { d.Error = "Tabela ilegível: " + ex.Message; t = new PartitionTable { SectorSize = dev.SectorSize, DiskSectors = dev.Length / dev.SectorSize }; }
        d.Table = t;
        int ss = dev.SectorSize;
        if (t.Style == TableStyle.None)
        {
            var fs = SafeDetect(dev);
            if (fs is not (FsKind.Empty or FsKind.Unknown))
            {
                var whole = new PartitionInfo { Disk = d, IsWholeDisk = true, Offset = 0, Length = dev.Length, FileSystem = fs };
                if (fs == FsKind.Ext)
                    try
                    {
                        var x = FsDetect.ParseExt(dev.ReadBytes(1024, 1024));
                        if (x != null) { whole.FsVariant = x.Variant; whole.Label = x.Label.Length > 0 ? x.Label : null; whole.Used = (x.Blocks - x.FreeBlocks) * x.BlockSize; whole.Free = x.FreeBlocks * x.BlockSize; }
                    }
                    catch { }
                d.Partitions.Add(whole);
                return;
            }
            d.Partitions.Add(new PartitionInfo { Disk = d, Offset = 0, Length = dev.Length });
            return;
        }
        long min = t.AlignSectors;
        var ordered = t.Ordered.ToList();
        long cur = t.Style == TableStyle.Gpt ? t.FirstUsableLba : 1;
        long end = t.Style == TableStyle.Gpt ? t.LastUsableLba : t.DiskSectors - 1;
        foreach (var e in ordered)
        {
            if (e.StartLba - cur >= min) d.Partitions.Add(new PartitionInfo { Disk = d, Offset = cur * ss, Length = (e.StartLba - cur) * ss });
            var p = new PartitionInfo { Disk = d, Entry = e, Offset = e.StartLba * ss, Length = e.SectorCount * ss };
            if (p.Offset + p.Length <= dev.Length)
            {
                p.FileSystem = SafeDetect(dev.Slice(p.Offset, p.Length));
                if (p.FileSystem == FsKind.Ext)
                    try
                    {
                        var x = FsDetect.ParseExt(dev.ReadBytes(p.Offset + 1024, 1024));
                        if (x != null) { p.FsVariant = x.Variant; p.Label = x.Label.Length > 0 ? x.Label : null; p.Used = (x.Blocks - x.FreeBlocks) * x.BlockSize; p.Free = x.FreeBlocks * x.BlockSize; }
                    }
                    catch { }
                if (p.FileSystem is FsKind.HfsPlus or FsKind.Apfs)
                    try
                    {
                        var u = UsageCalculator.Compute(dev.Slice(p.Offset, p.Length), p.FileSystem, out var variant);
                        if (u != null) { p.FsVariant = variant; p.Label = u.Value.label; p.Used = u.Value.used; p.Free = u.Value.free; }
                    }
                    catch { }
            }
            d.Partitions.Add(p);
            cur = Math.Max(cur, e.EndLba + 1);
        }
        if (end + 1 - cur >= min) d.Partitions.Add(new PartitionInfo { Disk = d, Offset = cur * ss, Length = (end + 1 - cur) * ss });
    }

    private static FsKind SafeDetect(IBlockDevice v)
    {
        try { return FsDetect.Detect(v); } catch { return FsKind.Unknown; }
    }
}

/// <summary>Uso de espaço de uma partição não montada, lido do próprio sistema de arquivos.</summary>
public static class UsageCalculator
{
    public static (long used, long free, string? label)? Compute(IBlockDevice vol, FsKind fs) => Compute(vol, fs, out _);

    public static (long used, long free, string? label)? Compute(IBlockDevice vol, FsKind fs, out string? variant)
    {
        variant = null;
        try
        {
            switch (fs)
            {
                case FsKind.Ntfs:
                    {
                        var v = NtfsVolume.Open(vol, out _);
                        var bmp = v?.LoadBitmap();
                        if (v == null || bmp == null) return null;
                        long used = NtfsVolume.CountSetBits(bmp, v.TotalClusters) * v.ClusterSize;
                        long total = v.TotalClusters * v.ClusterSize;
                        return (used, total - used, v.ReadLabel());
                    }
                case FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32:
                    {
                        var v = FatVolume.Open(vol, out _);
                        if (v == null) return null;
                        try { v.ParseDirectory(v.ReadDirectory(v.Kind == FsKind.Fat32 ? v.RootCluster : 0, false)); } catch { }
                        long free = v.CountFreeClusters() * v.ClusterSize;
                        long total = v.ClusterCount * v.ClusterSize;
                        return (total - free, free, v.Label);
                    }
                case FsKind.Ext:
                    {
                        var x = FsDetect.ParseExt(vol.ReadBytes(1024, 1024));
                        return x == null ? null : ((x.Blocks - x.FreeBlocks) * x.BlockSize, x.FreeBlocks * x.BlockSize, x.Label);
                    }
                case FsKind.Apfs:
                    {
                        var c = ApfsContainer.Open(vol, out _);
                        if (c == null) return null;
                        variant = c.Volumes.Any(x => x.Encrypted) ? "APFS (criptografado)" : "APFS";
                        long total = (long)c.BlockCount * c.BlockSize, used = Math.Min(total, c.UsedBytes);
                        var names = c.Volumes.Where(x => x.Role is 0 or 0x2 or 0x40).Select(x => x.Name).Where(n => n.Length > 0).ToList();
                        if (names.Count == 0) names = c.Volumes.Select(x => x.Name).Take(1).ToList();
                        return (used, total - used, names.Count > 0 ? string.Join(" + ", names) : null);
                    }
                case FsKind.HfsPlus:
                    {
                        // o cabeçalho já traz o total e o livre; o nome é o da pasta raiz no catálogo
                        var v = HfsVolume.Open(vol, out _);
                        if (v == null) return null;
                        variant = v.Variant;
                        long bs = v.BlockSize;
                        return ((long)(v.TotalBlocks - v.FreeBlocks) * bs, (long)v.FreeBlocks * bs, v.Label.Length > 0 ? v.Label : null);
                    }
                case FsKind.ExFat:
                    {
                        var v = ExFatVolume.Open(vol, out _);
                        if (v == null) return null;
                        v.ParseDirectory(v.ReadDirectory(v.RootCluster, 0, false), true);
                        if (v.Bitmap == null) return null;
                        long used = NtfsVolume.CountSetBits(v.Bitmap, v.ClusterCount) * v.ClusterSize;
                        long total = v.ClusterCount * v.ClusterSize;
                        return (used, total - used, v.Label);
                    }
            }
        }
        catch { }
        return null;
    }
}
