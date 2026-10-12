using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Partitioning;

namespace LibertyX.Partition.ViewModels;

/// <summary>A tela inicial: todos os discos com o mapa de partições, e o painel de ações da seleção.</summary>
public sealed partial class DisksPageViewModel : PageViewModel
{
    public override string Title => "Discos e partições";
    public override string IconKey => "Dashboard";

    public ObservableCollection<DiskVM> Disks { get; } = [];
    [ObservableProperty] private DiskVM? _selectedDisk;
    [ObservableProperty] private PartitionVM? _selectedPartition;
    public ObservableCollection<ActionGroup> Actions { get; } = [];
    public ObservableCollection<InfoRow> Details { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _inspectorTitle = "";
    [ObservableProperty] private string _inspectorSubtitle = "";
    [ObservableProperty] private double _inspectorUsage;
    [ObservableProperty] private string _inspectorUsageText = "";
    [ObservableProperty] private bool _inspectorHasUsage;
    [ObservableProperty] private Avalonia.Media.Color _inspectorColor = Ui.Accent;
    [ObservableProperty] private string? _inspectorWarning;

    public bool HasSelection => SelectedDisk != null;
    public bool Empty => Disks.Count == 0;

    public override void OnDisksChanged()
    {
        int? diskNo = SelectedDisk?.D.Number;
        long? partOfs = SelectedPartition?.P.Offset;
        Disks.Clear();
        foreach (var d in Main.Disks) Disks.Add(new DiskVM(d));
        long total = Main.Disks.Where(d => !d.IsImage).Sum(d => d.Size);
        int n = Main.Disks.Count(d => !d.IsImage), imgs = Main.Disks.Count(d => d.IsImage);
        Summary = $"{n} disco{(n == 1 ? "" : "s")} · {Fmt.Size(total)}" + (imgs > 0 ? $" · {imgs} imagem(ns) anexada(s)" : "");
        var disk = Disks.FirstOrDefault(d => d.D.Number == diskNo) ?? Disks.FirstOrDefault();
        var part = disk?.Partitions.FirstOrDefault(p => p.P.Offset == partOfs);
        if (part != null) Select(part);
        else if (disk != null) Select(disk);
        else { SelectedDisk = null; SelectedPartition = null; Actions.Clear(); Details.Clear(); }
        OnPropertyChanged(nameof(Empty));
        OnPropertyChanged(nameof(HasSelection));
    }

    public override void OnPendingChanged() { foreach (var d in Disks) d.RefreshPending(); }

    public override void OnShown(object? arg) { if (Disks.Count == 0 && Main.Disks.Count > 0) OnDisksChanged(); }

    public void Select(DiskVM d)
    {
        foreach (var x in Disks) { x.IsSelected = x == d; foreach (var p in x.Partitions) p.IsSelected = false; }
        SelectedDisk = d;
        SelectedPartition = null;
        var D = d.D;
        InspectorTitle = d.Title;
        InspectorSubtitle = d.Model;
        InspectorColor = Ui.Accent;
        InspectorUsage = d.UsedFraction;
        InspectorHasUsage = true;
        InspectorUsageText = $"{d.UsedFraction * 100:0}%";
        InspectorWarning = D.IsSystem ? "Disco do Windows em uso: operações destrutivas ficam bloqueadas." : D.Error;
        Details.Clear();
        Details.Add(new("Tipo", D.KindText));
        Details.Add(new("Capacidade", $"{Fmt.Size(D.Size)} ({D.Size:N0} bytes)"));
        Details.Add(new("Tabela", D.StyleText + (D.Table.Style == TableStyle.Gpt ? $" · {D.Table.DiskGuid.ToString().ToUpperInvariant()[..8]}…" : D.Table.Style == TableStyle.Mbr ? $" · assinatura {D.Table.DiskSignature:X8}" : "")));
        Details.Add(new("Setor", $"{D.SectorSize} B lógico · {D.PhysicalSectorSize} B físico"));
        if (!string.IsNullOrEmpty(D.Serial)) Details.Add(new("Nº de série", D.Serial));
        if (D.Smart?.TemperatureC is { } t) Details.Add(new("Temperatura", $"{t} °C"));
        if (D.Smart?.PowerOnHours is { } h) Details.Add(new("Horas ligado", $"{h:N0} h"));
        foreach (var w in D.Table.Warnings) Details.Add(new("Aviso", w));
        Actions.Clear();
        foreach (var g in ActionsBuilder.ForDisk(d)) Actions.Add(g);
        OnPropertyChanged(nameof(HasSelection));
    }

    public void Select(PartitionVM p)
    {
        foreach (var x in Disks) { x.IsSelected = false; foreach (var q in x.Partitions) q.IsSelected = q == p; }
        SelectedDisk = p.Disk;
        SelectedPartition = p;
        var P = p.P;
        InspectorTitle = p.Title;
        InspectorSubtitle = P.IsUnallocated ? $"{p.SizeText} livres em {p.Disk.Title}" : $"{p.FsText} · {p.SizeText} · {p.Disk.Title}";
        InspectorColor = p.Color;
        InspectorHasUsage = p.HasUsage;
        InspectorUsage = p.UsedFraction;
        InspectorUsageText = p.UsagePercent;
        InspectorWarning = P.IsWindowsVolume ? "Volume do Windows em uso: só redimensionar, rotular e verificar são permitidos aqui."
                         : P.BitLocker ? "Volume protegido por BitLocker." : null;
        Details.Clear();
        if (!P.IsUnallocated)
        {
            Details.Add(new("Sistema de arquivos", p.FsText));
            Details.Add(new("Usado", p.HasUsage ? $"{p.UsedText} ({p.UsagePercent})" : "—"));
            Details.Add(new("Livre", p.FreeText));
            Details.Add(new("Tipo", P.TypeText));
            if (P.StatusText != "—") Details.Add(new("Status", P.StatusText));
        }
        Details.Add(new("Início", $"setor {P.StartLba:N0}{(Core.Operations.DiskOperations.IsAligned(P) ? " · alinhada" : " · DESALINHADA")}"));
        Details.Add(new("Tamanho", $"{P.Sectors:N0} setores"));
        if (P.Entry?.UniqueGuid is { } g && g != Guid.Empty) Details.Add(new("GUID", g.ToString().ToUpperInvariant()));
        Actions.Clear();
        foreach (var gr in ActionsBuilder.ForPartition(p)) Actions.Add(gr);
        OnPropertyChanged(nameof(HasSelection));
    }
}
