using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Platform;

namespace LibertyX.Partition.ViewModels;

public sealed partial class PartitionVM : ObservableObject
{
    public PartitionInfo P { get; }
    public DiskVM Disk { get; }
    [ObservableProperty] private bool _isSelected;

    public PartitionVM(PartitionInfo p, DiskVM disk) { P = p; Disk = disk; }

    public string Name => P.IsUnallocated ? L.T("Não alocado") : P.Letter ?? P.KernelName ?? "*:";
    public string LabelText => P.IsUnallocated ? "" : !string.IsNullOrEmpty(P.Label) ? P.Label! : P.IsEsp ? L.T("Sistema EFI") : P.IsMsr ? L.T("Reservada") : P.IsRecovery ? L.T("Recuperação") : L.T("Sem rótulo");
    public string Title => P.IsUnallocated ? L.T("Não alocado") : P.Letter != null ? $"{P.Letter} {LabelText}" : P.KernelName != null ? $"{P.KernelName} · {LabelText}" : LabelText;
    public string SizeText => Fmt.Size(P.Length);
    public string UsedText => P.IsUnallocated ? "—" : Fmt.Size(P.Used);
    public string FreeText => P.IsUnallocated ? Fmt.Size(P.Length) : Fmt.Size(P.Free);
    // "RAW" é como o Windows chama o volume sem sistema de arquivos reconhecido (e cabe na coluna)
    public string FsText => P.IsUnallocated ? "—" : P.FsVariant ?? (P.FileSystem == FsKind.Unknown ? "RAW" : FsDetect.Name(P.FileSystem));
    public string TypeText => P.TypeText;
    public string StatusText => P.StatusText;
    public double UsedFraction => P.Used is { } u && P.Length > 0 ? Math.Clamp((double)u / P.Length, 0, 1) : 0;
    public bool HasUsage => P.Used != null && !P.IsUnallocated;
    public string UsagePercent => HasUsage ? $"{UsedFraction * 100:0}%" : "";
    public Color Color => Ui.PartitionColor(P);
    public IBrush ColorBrush => new SolidColorBrush(Color);
    public bool IsUnallocated => P.IsUnallocated;
    public string Short => P.IsUnallocated ? L.T("Livre") : P.Letter ?? (P.IsEsp ? "EFI" : P.IsMsr ? "MSR" : P.IsRecovery ? "Rec." : P.IsMounted && !OperatingSystem.IsWindows() ? MountShort : FsText);
    /// <summary>Linux: o ponto de montagem curto para caber na faixa ("/", "/home", "…/PENDRIVE").</summary>
    private string MountShort { get { var m = P.MountPoints[0]; return m.Length <= 10 ? m : "…/" + Path.GetFileName(m.TrimEnd('/')); } }

    public IRelayCommand SelectCommand => new RelayCommand(() => MainViewModel.Instance.Page<DisksPageViewModel>("discos").Select(this));
}

public sealed partial class DiskVM : ObservableObject
{
    public DiskInfo D { get; }
    public ObservableCollection<PartitionVM> Partitions { get; } = [];
    [ObservableProperty] private bool _isSelected;

    public DiskVM(DiskInfo d)
    {
        D = d;
        foreach (var p in d.Partitions) Partitions.Add(new PartitionVM(p, this));
    }

    public string Title => D.Title;
    public string Model => D.Model;
    public string Info => $"{D.KindText} · {D.StyleText} · {Fmt.Size(D.Size)}";
    public string SizeText => Fmt.Size(D.Size);
    public bool IsSystem => D.IsSystem;
    public bool IsImage => D.IsImage;
    public string? Error => D.Error;
    public bool HasError => D.Error != null;
    public string HealthText => D.Smart == null ? "" : Ui.HealthText(D.Smart.Health);
    public string HealthClass => D.Smart == null ? "" : Ui.HealthClass(D.Smart.Health);
    public bool HasHealth => D.Smart != null && D.Smart.Health != HealthLevel.Unknown;
    public bool HealthGood => D.Smart?.Health == HealthLevel.Good;
    public bool HealthCaution => D.Smart?.Health == HealthLevel.Caution;
    public bool HealthBad => D.Smart?.Health == HealthLevel.Bad;
    public string? TempText => D.Smart?.TemperatureC is { } t ? $"{t} °C" : null;
    public bool HasTemp => D.Smart?.TemperatureC != null;
    public Geometry? Icon => Ui.Icon(D.IsImage ? "Disc" : D.Bus is "USB" ? "Usb" : D.Bus == "NVMe" ? "Ssd" : "Disk");
    public double UsedFraction
    {
        get
        {
            long used = D.Partitions.Where(p => !p.IsUnallocated).Sum(p => p.Used ?? p.Length);
            return D.Size > 0 ? Math.Clamp((double)used / D.Size, 0, 1) : 0;
        }
    }
    public IRelayCommand SelectCommand => new RelayCommand(() => MainViewModel.Instance.Page<DisksPageViewModel>("discos").Select(this));

    public int PendingCount => MainViewModel.Instance.Pending.Count(o => o.DiskNumber == D.Number);
    public bool HasPending => PendingCount > 0;
    public string PendingText => PendingCount == 1 ? L.T("1 alteração pendente") : L.T($"{PendingCount} alterações pendentes");
    public void RefreshPending() { OnPropertyChanged(nameof(PendingCount)); OnPropertyChanged(nameof(HasPending)); OnPropertyChanged(nameof(PendingText)); }
}

public sealed partial class ActionItem : ObservableObject
{
    public string Title { get; init; } = "";
    public Geometry? Icon { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Reason { get; init; }
    public bool Dangerous { get; init; }
    public IAsyncRelayCommand Command { get; init; } = null!;
    public string? Tip => Enabled ? null : Reason;

    public static ActionItem Of(string title, string icon, Func<Task> run, string? disabledReason = null, bool dangerous = false) => new()
    {
        Title = title, Icon = Ui.Icon(icon), Enabled = disabledReason == null, Reason = disabledReason, Dangerous = dangerous,
        Command = new AsyncRelayCommand(run, () => disabledReason == null),
    };
}

public sealed class ActionGroup(string title, IEnumerable<ActionItem> items)
{
    public string Title { get; } = title.ToUpperInvariant();
    public List<ActionItem> Items { get; } = items.ToList();
}

public sealed record InfoRow(string Key, string Value);
