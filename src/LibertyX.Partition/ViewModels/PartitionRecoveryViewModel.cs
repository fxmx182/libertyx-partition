using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

namespace LibertyX.Partition.ViewModels;

public sealed partial class FoundRow : ObservableObject
{
    public FoundPartition F { get; }
    private readonly PartitionRecoveryViewModel _owner;
    public FoundRow(FoundPartition f, PartitionRecoveryViewModel owner, int ss) { F = f; _owner = owner; SectorSize = ss; _isChecked = !f.InCurrentTable && f.Verified && !f.Conflicting; }
    public int SectorSize { get; }
    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => _owner.Changed();
    public string FsText => F.Variant ?? FsDetect.Name(F.FileSystem);
    public string LabelText => string.IsNullOrEmpty(F.Label) ? (F.LooksLikeEsp ? "Sistema EFI" : F.LooksLikeRecovery ? "Recuperação" : "Sem rótulo") : F.Label!;
    public string SizeText => Fmt.Size(F.SectorCount * SectorSize);
    public string RangeText => $"setores {F.StartLba:N0} – {F.EndLba:N0}";
    public string Evidence => F.Evidence;
    public bool Verified => F.Verified;
    public bool NotVerified => !F.Verified;
    public bool InTable => F.InCurrentTable;
    public bool Conflicting => F.Conflicting;
    public bool Confirmed => F.BackupConfirmed;
    public Avalonia.Media.IBrush ColorBrush => new Avalonia.Media.SolidColorBrush(F.LooksLikeEsp ? Avalonia.Media.Color.Parse("#FFE08A") : Ui.FsColor(F.FileSystem));
    public IRelayCommand Explore => new RelayCommand(() => _owner.ExploreRow(this));
}

/// <summary>Procura partições perdidas pelos setores de boot e devolve-as à tabela (ou só explora os arquivos).</summary>
public sealed partial class PartitionRecoveryViewModel : PageViewModel
{
    public override string Title => "Recuperar partição";
    public override string IconKey => "RecoverPartition";

    public PickerViewModel Picker { get; } = new() { ShowPartitions = false };
    public ObservableCollection<FoundRow> Found { get; } = [];
    public ObservableCollection<PartitionVM> Preview { get; } = [];
    [ObservableProperty] private bool _deep;
    [ObservableProperty] private int _step;
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private long _foundCount;
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private int _styleIndex;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string? _restoreBlock;

    public bool IsChoosing => Step == 0;
    public bool IsScanning => Step == 1;
    public bool IsResults => Step == 2;
    public bool CanStart => Picker.Selected != null;
    public bool CanRestore => RestoreBlock == null && Found.Any(f => f.IsChecked);
    partial void OnStepChanged(int value) { OnPropertyChanged(nameof(IsChoosing)); OnPropertyChanged(nameof(IsScanning)); OnPropertyChanged(nameof(IsResults)); }
    partial void OnRestoreBlockChanged(string? value) => OnPropertyChanged(nameof(CanRestore));

    private DiskInfo? _disk;
    private CancellationTokenSource? _cts;

    public PartitionRecoveryViewModel() { Picker.Changed += _ => OnPropertyChanged(nameof(CanStart)); }

    public override void OnDisksChanged() { if (Step == 0) Picker.Load(Main.Disks); }

    public override void OnShown(object? arg)
    {
        if (Picker.Groups.Count == 0) Picker.Load(Main.Disks);
        if (arg is DiskInfo d) { Step = 0; Picker.Select(d, null); }
    }

    [RelayCommand]
    private async Task Start()
    {
        var sel = Picker.Selected;
        if (sel == null) return;
        _disk = sel.Disk;
        _cts = new CancellationTokenSource();
        Step = 1; Fraction = 0; FoundCount = 0; Phase = "Procurando setores de boot";
        var sw = Stopwatch.StartNew();
        var prog = new Progress<ScanProgress>(p =>
        {
            Phase = p.Phase; Fraction = p.Fraction; FoundCount = p.Found;
            Elapsed = Fmt.Duration(sw.Elapsed);
        });
        List<FoundPartition> found;
        try
        {
            found = await Task.Run(() =>
            {
                using var dev = DiskSystem.OpenDisk(_disk, false);
                return PartitionFinder.Scan(dev, Deep, prog, _cts.Token);
            });
        }
        catch (OperationCanceledException) { Step = 0; return; }
        catch (Exception ex) { await Main.Alert("Não foi possível ler o disco", ex.Message, "danger"); Step = 0; return; }
        Found.Clear();
        foreach (var f in found) Found.Add(new FoundRow(f, this, _disk.SectorSize));
        StyleIndex = _disk.Table.Style == TableStyle.Mbr ? 1 : 0;
        ResultText = found.Count == 0 ? "Nenhuma partição encontrada" : $"{found.Count} partição(ões) encontrada(s)";
        Changed();
        Step = 2;
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();
    [RelayCommand] private void Back() { Step = 0; Picker.Load(Main.Disks); }

    partial void OnStyleIndexChanged(int value) => Changed();

    public void Changed()
    {
        if (_disk == null) return;
        var chosen = Found.Where(f => f.IsChecked).Select(f => f.F).ToList();
        RestoreBlock = _disk.IsSystem ? "Disco do Windows em uso: restaure a tabela pela mídia inicializável." : _disk.ReadOnly ? "Imagem anexada só para leitura — use Explorar para tirar os arquivos." : null;
        // prévia: como o disco ficará
        Preview.Clear();
        try
        {
            var t = PartitionFinder.BuildRestoredTable(_disk.Table, chosen, StyleIndex == 0 ? TableStyle.Gpt : TableStyle.Mbr, _disk.Size / _disk.SectorSize, _disk.SectorSize);
            var fake = new DiskInfo { Number = _disk.Number, Size = _disk.Size, SectorSize = _disk.SectorSize, Model = _disk.Model, IsImage = _disk.IsImage, ImagePath = _disk.ImagePath };
            var dvm = new DiskVM(fake);
            long cur = t.Style == TableStyle.Gpt ? PartitionCodec.GptFirstUsable(t) : 1;
            foreach (var e in t.Ordered)
            {
                if (e.StartLba - cur >= 2048) Preview.Add(new PartitionVM(new PartitionInfo { Disk = fake, Offset = cur * _disk.SectorSize, Length = (e.StartLba - cur) * _disk.SectorSize }, dvm));
                var f = chosen.FirstOrDefault(c => c.StartLba == e.StartLba);
                var pi = new PartitionInfo { Disk = fake, Entry = e, Offset = e.StartLba * _disk.SectorSize, Length = e.SectorCount * _disk.SectorSize, FileSystem = f?.FileSystem ?? FsKind.Unknown, Label = f?.Label };
                Preview.Add(new PartitionVM(pi, dvm));
                cur = e.EndLba + 1;
            }
            long last = _disk.Size / _disk.SectorSize;
            if (last - cur >= 2048) Preview.Add(new PartitionVM(new PartitionInfo { Disk = fake, Offset = cur * _disk.SectorSize, Length = (last - cur) * _disk.SectorSize }, dvm));
        }
        catch (Exception ex) { RestoreBlock ??= ex.Message; }
        OnPropertyChanged(nameof(CanRestore));
    }

    public void ExploreRow(FoundRow row)
    {
        if (_disk == null) return;
        var e = new PartitionEntry { StartLba = row.F.StartLba, SectorCount = row.F.SectorCount };
        var p = new PartitionInfo { Disk = _disk, Entry = e, Offset = e.StartLba * _disk.SectorSize, Length = e.SectorCount * _disk.SectorSize, FileSystem = row.F.FileSystem, Label = row.F.Label };
        Main.Navigate("recuperar", new RecoveryTarget(p, true));
    }

    [RelayCommand]
    private async Task Restore()
    {
        if (_disk == null) return;
        var chosen = Found.Where(f => f.IsChecked).Select(f => f.F).ToList();
        var style = StyleIndex == 0 ? TableStyle.Gpt : TableStyle.Mbr;
        var d = _disk;
        Main.Queue("Restaurar partições", $"{chosen.Count} partição(ões) gravadas na tabela {(style == TableStyle.Gpt ? "GPT" : "MBR")} de {d.Title} — só a tabela é escrita", "RecoverPartition",
            s => DiskOperations.RestoreFound(MainViewModel.Fresh(d), chosen, style, s), d);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RestoreBackup()
    {
        var d = Picker.Selected?.Disk ?? _disk;
        if (d == null) { Main.ShowToast("Escolha o disco primeiro."); return; }
        var file = await MainViewModel.PickFile("Backup de tabela", ("Backup do LibertyX Partition", ["*.lxbak"]));
        if (file == null) return;
        var info = TableBackup.Describe(file);
        Main.Queue("Restaurar backup de tabela", $"{d.Title}: tabela de {info.when:dd/MM/yyyy HH:mm} (antes de: {info.reason})", "Restore",
            s => DiskOperations.RestoreTableBackup(MainViewModel.Fresh(d), file, s), d, dangerous: true);
    }
}
