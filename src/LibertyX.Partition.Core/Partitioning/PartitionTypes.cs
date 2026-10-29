namespace LibertyX.Partition.Core.Partitioning;

public static class PartitionTypes
{
    public static readonly Guid Esp = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid Msr = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid WinRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    public static readonly Guid LdmMetadata = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    public static readonly Guid LdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    public static readonly Guid StorageSpaces = new("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");
    public static readonly Guid LinuxFs = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    public static readonly Guid LinuxSwap = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
    public static readonly Guid LinuxLvm = new("E6D6D379-F507-44C2-A23C-238F2A3DF928");
    public static readonly Guid LinuxRaid = new("A19D880F-05FC-4D3B-A006-743F0F84911E");
    public static readonly Guid BiosBoot = new("21686148-6449-6E6F-744E-656564454649");
    public static readonly Guid AppleHfs = new("48465300-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid AppleApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    // tipos da especificação de partições detectáveis (systemd/UAPI): o systemd monta sozinho pelo tipo
    public static readonly Guid LinuxLuks = new("CA7D7CCB-63ED-4C53-861C-1742536059CC");
    public static readonly Guid LinuxRootX64 = new("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709");
    public static readonly Guid LinuxRootArm64 = new("B921B045-1DF0-41C3-AF44-4C6F280D3FAE");
    public static readonly Guid LinuxHome = new("933AC7E1-2EB4-4F13-B844-0E14E2AEF915");
    public static readonly Guid LinuxSrv = new("3B8F8425-20E0-4F3B-907F-1A25A76F98E8");
    public static readonly Guid LinuxVar = new("4D21B016-B534-45C2-A9FB-5C16E091FD2D");
    public static readonly Guid LinuxXbootldr = new("BC13C2FF-59E6-4262-A352-B275FD6F7172");
    public static readonly Guid LinuxReserved = new("8DA63339-0007-60C0-C436-083AC8230908");
    public static readonly Guid ChromeOsKernel = new("FE3A2A5D-4F32-41A7-B725-ACCC3285A309");
    public static readonly Guid FreeBsdData = new("516E7CB4-6ECF-11D6-8FF8-00022D09712B");
    public static readonly Guid FreeBsdUfs = new("516E7CB6-6ECF-11D6-8FF8-00022D09712B");
    public static readonly Guid FreeBsdZfs = new("516E7CBA-6ECF-11D6-8FF8-00022D09712B");
    public static readonly Guid SolarisZfs = new("6A898CC3-1DD2-11B2-99A6-080020736631");

    /// <summary>Bits de atributo GPT com significado (os de 48 a 63 dependem do tipo).</summary>
    public const ulong AttrRequired = 1UL << 0, AttrNoBlockIo = 1UL << 1, AttrLegacyBoot = 1UL << 2,
        AttrReadOnly = 1UL << 60, AttrShadow = 1UL << 61, AttrHidden = 1UL << 62, AttrNoAutomount = 1UL << 63;

    /// <summary>Tipo GPT/MBR certo para o sistema de arquivos recém-criado (no Linux).</summary>
    public static (Guid gpt, byte mbr) ForFileSystem(FileSystems.FsKind fs, bool esp = false) => fs switch
    {
        _ when esp => (Esp, 0xEF),
        FileSystems.FsKind.Ntfs or FileSystems.FsKind.ExFat => (BasicData, 0x07),
        FileSystems.FsKind.Fat32 => (BasicData, 0x0C),
        FileSystems.FsKind.Fat16 or FileSystems.FsKind.Fat12 => (BasicData, 0x0E),
        FileSystems.FsKind.LinuxSwap => (LinuxSwap, 0x82),
        FileSystems.FsKind.Luks => (LinuxLuks, 0x83),
        FileSystems.FsKind.LvmPv => (LinuxLvm, 0x8E),
        FileSystems.FsKind.MdRaid => (LinuxRaid, 0xFD),
        _ => (LinuxFs, 0x83),
    };

    /// <summary>Atributo GPT "requerido pela plataforma" + "sem letra" — o que o Windows põe na partição de recuperação.</summary>
    public const ulong RecoveryAttributes = 0x8000000000000001UL;

    public static string GptName(Guid g)
    {
        if (g == Esp) return L.T("Sistema EFI");
        if (g == Msr) return L.T("Reservada da Microsoft");
        if (g == BasicData) return L.T("Dados básicos");
        if (g == WinRecovery) return L.T("Recuperação do Windows");
        if (g == LdmMetadata) return L.T("Metadados LDM (disco dinâmico)");
        if (g == LdmData) return L.T("Dados LDM (disco dinâmico)");
        if (g == StorageSpaces) return L.T("Espaços de Armazenamento");
        if (g == LinuxFs) return L.T("Linux");
        if (g == LinuxSwap) return L.T("Linux swap");
        if (g == LinuxLvm) return L.T("Linux LVM");
        if (g == LinuxRaid) return L.T("Linux RAID");
        if (g == BiosBoot) return L.T("BIOS boot");
        if (g == LinuxLuks) return L.T("Linux LUKS");
        if (g == LinuxRootX64) return L.T("Linux raiz (x86-64)");
        if (g == LinuxRootArm64) return L.T("Linux raiz (ARM64)");
        if (g == LinuxHome) return L.T("Linux /home");
        if (g == LinuxSrv) return L.T("Linux /srv");
        if (g == LinuxVar) return L.T("Linux /var");
        if (g == LinuxXbootldr) return L.T("Linux /boot (XBOOTLDR)");
        if (g == LinuxReserved) return L.T("Linux reservada");
        if (g == ChromeOsKernel) return L.T("ChromeOS kernel");
        if (g == FreeBsdData) return "FreeBSD";
        if (g == FreeBsdUfs) return L.T("FreeBSD UFS");
        if (g == FreeBsdZfs) return L.T("FreeBSD ZFS");
        if (g == SolarisZfs) return L.T("ZFS (Solaris/Apple)");
        if (g == AppleHfs) return L.T("Apple HFS+");
        if (g == AppleApfs) return L.T("Apple APFS");
        return L.T("Tipo ") + g.ToString().ToUpperInvariant();
    }

    public static string MbrName(byte t) => t switch
    {
        0x00 => L.T("Vazia"),
        0x01 => "FAT12",
        0x04 or 0x06 or 0x0E => "FAT16",
        0x05 or 0x0F or 0x85 => L.T("Estendida"),
        0x07 => L.T("NTFS / exFAT"),
        0x0B or 0x0C => "FAT32",
        0x11 or 0x14 or 0x16 or 0x1B or 0x1C or 0x1E => L.T("Oculta (FAT)"),
        0x17 => L.T("Oculta (NTFS)"),
        0x27 => L.T("Recuperação do Windows"),
        0x42 => L.T("Disco dinâmico"),
        0x82 => L.T("Linux swap"),
        0x83 => L.T("Linux"),
        0x8E => L.T("Linux LVM"),
        0xA5 => "FreeBSD",
        0xAF => L.T("Apple HFS+"),
        0xEE => L.T("GPT protetora"),
        0xEF => L.T("Sistema EFI"),
        0xFD => L.T("Linux RAID"),
        _ => L.T($"Tipo 0x{t:X2}"),
    };

    public static bool IsExtended(byte t) => t is 0x05 or 0x0F or 0x85;

    /// <summary>Conversão MBR → GPT. MSR não existe em MBR, então o caminho de volta não a traz.</summary>
    public static (Guid type, ulong attrs) MbrToGpt(byte t) => t switch
    {
        0xEF => (Esp, 0),
        0x27 => (WinRecovery, RecoveryAttributes),
        0x82 => (LinuxSwap, 0),
        0x83 => (LinuxFs, 0),
        0x8E => (LinuxLvm, 0),
        0xFD => (LinuxRaid, 0),
        0xAF => (AppleHfs, 0),
        _ => (BasicData, 0),
    };

    /// <summary>Conversão GPT → MBR; <paramref name="fs"/> decide o byte certo para dados básicos.</summary>
    public static byte? GptToMbr(Guid g, FileSystems.FsKind fs)
    {
        if (g == Msr) return null; // descartada
        if (g == Esp) return 0xEF;
        if (g == WinRecovery) return 0x27;
        if (g == LinuxSwap) return 0x82;
        if (g == LinuxFs) return 0x83;
        if (g == LinuxLvm) return 0x8E;
        if (g == LinuxRaid) return 0xFD;
        if (g == AppleHfs) return 0xAF;
        return fs switch
        {
            FileSystems.FsKind.Fat12 => 0x01,
            FileSystems.FsKind.Fat16 => 0x0E,
            FileSystems.FsKind.Fat32 => 0x0C,
            FileSystems.FsKind.Ext or FileSystems.FsKind.Xfs or FileSystems.FsKind.Btrfs or FileSystems.FsKind.F2fs or FileSystems.FsKind.Luks
                or FileSystems.FsKind.ReiserFs or FileSystems.FsKind.Jfs or FileSystems.FsKind.Bcachefs => 0x83,
            FileSystems.FsKind.LinuxSwap => 0x82,
            FileSystems.FsKind.LvmPv => 0x8E,
            FileSystems.FsKind.MdRaid => 0xFD,
            _ => 0x07,
        };
    }

    /// <summary>Tipos conhecidos para o seletor "Alterar ID de tipo".</summary>
    public static readonly (Guid id, string name)[] CommonGpt =
    [
        (BasicData, GptName(BasicData)), (Esp, GptName(Esp)), (Msr, GptName(Msr)), (WinRecovery, GptName(WinRecovery)),
        (LinuxFs, GptName(LinuxFs)), (LinuxSwap, GptName(LinuxSwap)), (LinuxLvm, GptName(LinuxLvm)), (LinuxRaid, GptName(LinuxRaid)),
        (LinuxLuks, GptName(LinuxLuks)), (LinuxRootX64, GptName(LinuxRootX64)), (LinuxRootArm64, GptName(LinuxRootArm64)), (LinuxHome, GptName(LinuxHome)),
        (LinuxVar, GptName(LinuxVar)), (LinuxSrv, GptName(LinuxSrv)), (LinuxXbootldr, GptName(LinuxXbootldr)),
        (BiosBoot, GptName(BiosBoot)), (AppleHfs, GptName(AppleHfs)), (AppleApfs, GptName(AppleApfs)), (SolarisZfs, GptName(SolarisZfs)), (FreeBsdData, GptName(FreeBsdData)),
    ];

    public static readonly byte[] CommonMbr = [0x07, 0x0C, 0x0B, 0x0E, 0x06, 0x01, 0x27, 0xEF, 0x17, 0x1C, 0x83, 0x82, 0x8E, 0xFD, 0xAF];
}
