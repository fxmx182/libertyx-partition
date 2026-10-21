using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;

namespace LibertyX.Partition.ViewModels;

/// <summary>
/// Monta o painel de ações da seleção. Toda ação indisponível aparece mesmo assim, apagada,
/// com o motivo no balão — melhor que sumir sem explicação.
/// </summary>
public static partial class ActionsBuilder
{
    private static MainViewModel Main => MainViewModel.Instance;
    private static bool Win => OperatingSystem.IsWindows();
    private const string OnlyWindows = "Disponível só no Windows.";
    private const long MB = 1024 * 1024;

    private static string? DiskLock(DiskInfo d) =>
        d.IsSystem ? (Win ? "Disco do Windows em uso — use a mídia inicializável para isso." : "Disco do sistema em uso — para alterá-lo inteiro, inicie por um pendrive Linux.")
        : d.IsVirtual ? $"Volume {d.VirtualKind}: altere pela partição física de origem."
        : d.ReadOnly ? (d.IsImage ? "Imagem anexada só para leitura." : "Mídia protegida contra gravação (trava do cartão ou disco só leitura).") : null;

    private static string? PartLock(PartitionInfo p) =>
        p.IsSystemCritical ? "Partição do sistema em uso." : p.Disk.ReadOnly ? "Imagem anexada só para leitura." : null;

    private static string? NeedsVolume(PartitionInfo p) =>
        !Win ? OnlyWindows : p.VolumePath == null ? "A partição precisa estar montada (com sistema de arquivos reconhecido pelo Windows)." : null;

    public static IEnumerable<ActionGroup> ForDisk(DiskVM d)
    {
        var D = d.D;
        string? lockR = DiskLock(D);
        var g1 = new List<ActionItem>();
        if (D.Table.Style == TableStyle.None && !D.IsImage && !D.IsVirtual)
            g1.Add(ActionItem.Of("Inicializar disco (GPT/MBR)", "Create", () => InitializeDisk(D), lockR ?? (D.Partitions.Any(p => p.IsWholeDisk && (p.IsMounted || p.HeldBy != null)) ? "O volume deste disco está em uso — desmonte antes." : null)));
        g1.Add(ActionItem.Of("Recuperar dados do disco", "RecoverFiles", () => { Main.Navigate("recuperar", d.D); return Task.CompletedTask; }));
        g1.Add(ActionItem.Of("Recuperar partição perdida", "RecoverPartition", () => { Main.Navigate("particoes", d.D); return Task.CompletedTask; }));
        g1.Add(ActionItem.Of("Criar imagem do disco", "Backup", () => { Main.Navigate("imagem", d.D); return Task.CompletedTask; }));
        g1.Add(ActionItem.Of("Teste de superfície", "Surface", () => { Main.Navigate("superficie", d.D); return Task.CompletedTask; }));
        g1.Add(ActionItem.Of("Saúde e propriedades", "Health", () => { Main.Navigate("saude", d.D); return Task.CompletedTask; }));
        yield return new ActionGroup("Resgate", g1);

        var g2 = new List<ActionItem>();
        if (D.IsSystem) g2.Add(ActionItem.Of("Migrar sistema para SSD/HD", "Migrate", () => { Main.Navigate("migrar"); return Task.CompletedTask; }));
        g2.Add(ActionItem.Of("Copiar disco", "CopyDisk", () => { Main.Navigate("copiar-disco", d.D); return Task.CompletedTask; }, D.IsImage ? "Abra a imagem com Restaurar imagem." : null));
        g2.Add(ActionItem.Of(D.Table.Style == TableStyle.Gpt ? "Converter para MBR" : "Converter para GPT", "Convert", () => { Main.Navigate("converter", d.D); return Task.CompletedTask; }, D.Table.Style == TableStyle.None ? "Disco sem tabela." : null));
        g2.Add(ActionItem.Of("Alinhar todas as partições", "Align", () => { Main.Navigate("alinhar", d.D); return Task.CompletedTask; }, lockR));
        g2.Add(ActionItem.Of("Restaurar backup de tabela", "Restore", () => RestoreTableBackup(D), lockR));
        if (Lin && !D.IsImage && !D.IsVirtual)
            g2.Add(ActionItem.Of("Reler tabela (avisar o kernel)", "Refresh", () => Main.Run($"Relendo a tabela de {D.Title}", s =>
            {
                DiskSystem.Native.Rescan(MainViewModel.Fresh(D));
                s.Log("O kernel foi avisado. Partições em uso continuam como estavam até serem desmontadas.");
            }, success: "Tabela relida")));
        yield return new ActionGroup("Disco", g2);

        yield return new ActionGroup("Apagar", [
            ActionItem.Of("Limpar disco (sobrescrever)", "Wipe", () => { Main.Navigate("limpar", d.D); return Task.CompletedTask; }, lockR, true),
            ActionItem.Of("Excluir todas as partições", "DeleteAll", () => { Main.Navigate("excluir-todas", d.D); return Task.CompletedTask; }, lockR, true),
        ]);
    }

    public static IEnumerable<ActionGroup> ForPartition(PartitionVM pv)
    {
        var p = pv.P;
        if (Lin && !p.Disk.IsImage)
        {
            foreach (var g in ForPartitionLinux(pv)) yield return g;
            yield break;
        }
        if (p.IsUnallocated)
        {
            yield return new ActionGroup("Espaço livre", [
                ActionItem.Of("Criar partição", "Create", () => CreatePartition(pv), DiskLock(p.Disk) is { } r && p.Disk.IsImage ? r : p.Disk.Table.Style == TableStyle.None ? "Inicialize o disco primeiro." : p.Disk.ReadOnly ? "Imagem só para leitura." : null),
                ActionItem.Of("Recuperar dados desta área", "RecoverFiles", () => { Main.Navigate("recuperar", p); return Task.CompletedTask; }),
                ActionItem.Of("Procurar partição perdida aqui", "RecoverPartition", () => { Main.Navigate("particoes", p.Disk); return Task.CompletedTask; }),
                ActionItem.Of("Teste de superfície", "Surface", () => { Main.Navigate("superficie", p); return Task.CompletedTask; }),
            ]);
            yield break;
        }
        string? lockR = PartLock(p);
        bool ntfs = p.FileSystem is FsKind.Ntfs or FsKind.ReFS;
        var (before, after) = Neighbors(pv);

        yield return new ActionGroup("Alterar partição", [
            ActionItem.Of("Mover / redimensionar", "Resize", () => ResizeMove(pv),
                p.Disk.ReadOnly ? "Imagem só para leitura." : p.IsWholeDisk ? "Volume sem tabela de partições." : null),
            ActionItem.Of("Estender partição", "Extend", () => Extend(pv),
                p.Disk.ReadOnly ? "Imagem só para leitura." : !ntfs ? "O Windows só estende NTFS e ReFS." : (before?.IsUnallocated != true && after?.IsUnallocated != true) ? "Não há espaço livre ao lado. Reduza ou mova a vizinha primeiro." : null),
            ActionItem.Of("Unir com a próxima", "Merge", () => Merge(pv),
                !Win ? OnlyWindows : !ntfs || p.Letter == null ? "A partição precisa ser NTFS com letra." : after == null || after.IsUnallocated || after.Letter == null ? "A partição seguinte precisa ter letra (e vir logo depois desta)." : PartLock(after) ?? null),
            ActionItem.Of("Dividir partição", "Split", () => Split(pv), !Win ? OnlyWindows : !ntfs || p.VolumePath == null ? "Dividir exige NTFS montado (o Windows reduz o volume)." : null),
            ActionItem.Of("Alterar tamanho do cluster", "Cluster", () => ChangeCluster(pv), lockR ?? NeedsVolume(p) ?? (p.Letter == null ? "Dê uma letra à partição primeiro." : null)),
            ActionItem.Of(FsDetect.IsFat(p.FileSystem) ? "Converter FAT → NTFS" : "Converter NTFS → FAT32/exFAT", "FsConvert", () => FsConvert(pv),
                lockR ?? NeedsVolume(p) ?? (p.Letter == null ? "Dê uma letra à partição primeiro." : !(FsDetect.IsFat(p.FileSystem) || p.FileSystem == FsKind.Ntfs) ? "Só NTFS e FAT." : null)),
        ]);

        yield return new ActionGroup("Gerenciar partição", [
            ActionItem.Of("Formatar", "Format", () => Format(pv), lockR, true),
            ActionItem.Of("Excluir partição", "Delete", () => Delete(pv), lockR, true),
            ActionItem.Of("Copiar partição", "CopyPartition", () => { Main.Navigate("copiar-particao", p); return Task.CompletedTask; }),
            ActionItem.Of("Alinhar partição", "Align", () => Align(pv), lockR ?? (DiskOperations.IsAligned(p) ? "Já está alinhada a 1 MB." : null)),
            ActionItem.Of("Limpar partição", "Wipe", () => { Main.Navigate("limpar", p); return Task.CompletedTask; }, lockR, true),
            ActionItem.Of("Definir rótulo", "Label", () => SetLabel(pv), NeedsVolume(p)),
            ActionItem.Of("Alterar letra", "Letter", () => SetLetter(pv), !Win ? OnlyWindows : p.IsSystemCritical ? "Partição do sistema em uso." : p.VolumePath == null ? "Sem volume montável." : null),
            ActionItem.Of("Alterar ID de tipo", "TypeId", () => ChangeType(pv), lockR),
            ActionItem.Of("Alterar número de série", "Serial", () => ChangeSerial(pv), lockR ?? (p.FileSystem is not (FsKind.Ntfs or FsKind.ExFat) && !FsDetect.IsFat(p.FileSystem) ? "Só NTFS, FAT e exFAT." : null)),
            .. (p.Disk.Table.Style == TableStyle.Mbr && !p.Entry!.IsLogical
                ? new[] { ActionItem.Of(p.Entry.Active ? "Desmarcar como ativa" : "Definir como ativa", "Active", () => SetActive(pv), lockR) } : []),
        ]);

        yield return new ActionGroup("Verificar partição", [
            ActionItem.Of("Verificar sistema de arquivos", "CheckFs", () => CheckFs(pv), !Win ? OnlyWindows : p.VolumePath == null ? "Sem volume montado." : null),
            ActionItem.Of("Explorar partição", "Explore", () => { Main.Navigate("recuperar", new RecoveryTarget(p, true)); return Task.CompletedTask; }),
            ActionItem.Of("Teste de superfície", "Surface", () => { Main.Navigate("superficie", p); return Task.CompletedTask; }),
            ActionItem.Of("Propriedades da partição", "Properties", () => Properties(pv)),
            ActionItem.Of("Recuperação de dados", "RecoverFiles", () => { Main.Navigate("recuperar", p); return Task.CompletedTask; }),
            .. (p.BitLocker ? new[] { ActionItem.Of("Gerenciar BitLocker", "BitLocker", () => { Main.Navigate("bitlocker"); return Task.CompletedTask; }) } : []),
        ]);
    }

    private static (PartitionInfo? before, PartitionInfo? after) Neighbors(PartitionVM pv)
    {
        var list = pv.Disk.D.Partitions.OrderBy(x => x.Offset).ToList();
        int i = list.IndexOf(pv.P);
        return (i > 0 ? list[i - 1] : null, i >= 0 && i + 1 < list.Count ? list[i + 1] : null);
    }

    /// <summary>Relê o disco do sistema (depois de uma etapa feita pelo Windows) e acha a partição pelo deslocamento.</summary>
    private static (DiskInfo disk, PartitionInfo? part) Reload(DiskInfo d, long offset)
    {
        var all = DiskSystem.Enumerate();
        var nd = all.First(x => x.Number == d.Number && x.IsImage == d.IsImage);
        return (nd, nd.Partitions.FirstOrDefault(x => x.Offset == offset && !x.IsUnallocated));
    }

    private static List<char> FreeLetters()
    {
        var used = DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])).ToHashSet();
        return Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Where(c => !used.Contains(c)).ToList();
    }

    private static void Ensure(CommandResult r, string what)
    {
        if (!r.Ok) throw new InvalidOperationException($"{what}: {r.Message}");
    }
}
