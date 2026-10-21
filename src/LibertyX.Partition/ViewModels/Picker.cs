using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Platform;

namespace LibertyX.Partition.ViewModels;

public sealed partial class PickItem : ObservableObject
{
    public DiskInfo Disk { get; init; } = null!;
    public PartitionInfo? Partition { get; init; }
    public bool IsDisk => Partition == null;
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string? Badge { get; init; }
    public Geometry? Icon { get; init; }
    public IBrush Accent { get; init; } = Brushes.Gold;
    public double Usage { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Reason { get; init; }
    [ObservableProperty] private bool _isSelected;
    public PickerViewModel Owner { get; init; } = null!;
    public IRelayCommand Pick => new RelayCommand(() => Owner.Select(this), () => Enabled);
}

public sealed class PickGroup
{
    public required PickItem Header { get; init; }
    public List<PickItem> Items { get; } = [];
}

/// <summary>Escolha de disco/partição em blocos — usado por quase todas as ferramentas.</summary>
public sealed partial class PickerViewModel : ViewModelBase
{
    public ObservableCollection<PickGroup> Groups { get; } = [];
    [ObservableProperty] private PickItem? _selected;
    public bool AllowDisks { get; init; } = true;
    public bool AllowPartitions { get; init; } = true;
    public bool AllowUnallocated { get; init; }
    public bool ShowPartitions { get; init; } = true;
    public Func<DiskInfo, string?>? DiskFilter { get; init; }
    public Func<PartitionInfo, string?>? PartitionFilter { get; init; }
    public event Action<PickItem?>? Changed;

    public void Load(IEnumerable<DiskInfo> disks)
    {
        var prevDisk = Selected?.Disk.Number;
        var prevOfs = Selected?.Partition?.Offset;
        Groups.Clear();
        PickItem? restore = null;
        foreach (var d in disks)
        {
            string? dr = AllowDisks ? DiskFilter?.Invoke(d) : "Escolha uma partição.";
            var head = new PickItem
            {
                Owner = this, Disk = d, Title = d.Title, Subtitle = $"{d.Model} · {d.KindText} · {Fmt.Size(d.Size)}",
                Icon = Ui.Icon(d.IsImage ? "Disc" : d.Bus == "USB" ? "Usb" : "Disk"), Enabled = dr == null, Reason = dr,
                Badge = d.IsSystem ? Ui.SystemBadge : d.IsImage ? "Imagem" : d.IsVirtual ? d.VirtualKind : null,
            };
            var g = new PickGroup { Header = head };
            if (ShowPartitions)
                foreach (var p in d.Partitions)
                {
                    if (p.IsUnallocated && !AllowUnallocated) continue;
                    string? pr = !AllowPartitions ? "Escolha o disco inteiro." : PartitionFilter?.Invoke(p);
                    var c = Ui.PartitionColor(p);
                    var vm = new PartitionVM(p, new DiskVM(d));
                    var it = new PickItem
                    {
                        Owner = this, Disk = d, Partition = p, Title = vm.Title,
                        Subtitle = p.IsUnallocated ? $"{Fmt.Size(p.Length)} livres" : $"{vm.FsText} · {vm.SizeText}" + (vm.HasUsage ? $" · {vm.UsagePercent} usado" : ""),
                        Icon = Ui.Icon(p.IsUnallocated ? "Usage" : p.BitLocker ? "Lock" : "Disk"), Accent = new SolidColorBrush(c), Usage = vm.UsedFraction,
                        Enabled = pr == null, Reason = pr,
                    };
                    g.Items.Add(it);
                    if (prevDisk == d.Number && prevOfs == p.Offset) restore = it;
                }
            if (prevDisk == d.Number && prevOfs == null) restore = head;
            Groups.Add(g);
        }
        Selected = null;
        if (restore is { Enabled: true }) Select(restore);
        else Changed?.Invoke(null);
    }

    public void Select(PickItem? it)
    {
        foreach (var g in Groups) { g.Header.IsSelected = g.Header == it; foreach (var i in g.Items) i.IsSelected = i == it; }
        Selected = it;
        Changed?.Invoke(it);
    }

    public void Select(DiskInfo d, PartitionInfo? p)
    {
        foreach (var g in Groups)
        {
            if (g.Header.Disk.Number != d.Number || g.Header.Disk.IsImage != d.IsImage) continue;
            var it = p == null ? g.Header : g.Items.FirstOrDefault(i => i.Partition?.Offset == p.Offset);
            if (it is { Enabled: true }) { Select(it); return; }
        }
    }
}
