using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;
using LibertyX.Partition.Core.Tools;

namespace LibertyX.Partition.ViewModels;

// ───────────────────────── teste de superfície ─────────────────────────

public sealed partial class SurfaceViewModel : PageViewModel
{
    public override string Title => "Teste de superfície";
    public override string IconKey => "Surface";
    public PickerViewModel Picker { get; } = new() { AllowUnallocated = true };
    public const int CellCount = 2400;
    [ObservableProperty] private byte[] _cells = new byte[CellCount];
    [ObservableProperty] private int _version;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _speed = "—";
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private int _good, _slow, _verySlow, _bad;
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _verdict = "";
    public ObservableCollection<string> BadSectors { get; } = [];
    public bool CanStart => Picker.Selected != null && !Running;
    private CancellationTokenSource? _cts;
    private readonly ManualResetEventSlim _pause = new(true);

    public SurfaceViewModel() { Picker.Changed += _ => OnPropertyChanged(nameof(CanStart)); }
    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CanStart));
    public override void OnDisksChanged() { if (!Running) Picker.Load(Main.Disks); }
    public override void OnShown(object? arg)
    {
        if (Picker.Groups.Count == 0) Picker.Load(Main.Disks);
        if (arg is DiskInfo d) Picker.Select(d, null);
        if (arg is PartitionInfo p) Picker.Select(p.Disk, p);
    }

    [RelayCommand]
    private async Task Start()
    {
        var sel = Picker.Selected;
        if (sel == null) return;
        _cts = new CancellationTokenSource();
        Cells = new byte[CellCount];
        Good = Slow = VerySlow = Bad = 0;
        BadSectors.Clear();
        Running = true; HasRun = true; Paused = false; _pause.Set();
        TargetText = sel.Partition == null ? $"{sel.Disk.Title} · {sel.Disk.Model}" : $"{sel.Title} · {sel.Disk.Title}";
        Verdict = "";
        var sw = Stopwatch.StartNew();
        DateTime last = default;
        var cells = Cells;
        var prog = new Progress<SurfaceProgress>(p =>
        {
            if (p.BlockIndex < cells.Length) cells[p.BlockIndex] = (byte)p.Health;
            switch (p.Health) { case BlockHealth.Good: Good++; break; case BlockHealth.Slow: Slow++; break; case BlockHealth.VerySlow: VerySlow++; break; case BlockHealth.Bad: Bad++; break; }
            if ((DateTime.UtcNow - last).TotalMilliseconds < 120) return;
            last = DateTime.UtcNow;
            Version++;
            Fraction = (double)p.Done / p.Total;
            Speed = Fmt.Speed(p.BytesPerSecond);
            Elapsed = Fmt.Duration(sw.Elapsed) + (Fraction > 0.01 ? $" · faltam ~{Fmt.Duration(TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - Fraction) / Fraction))}" : "");
        });
        try
        {
            var res = await Task.Run(() =>
            {
                using var disk = DiskSystem.OpenDisk(sel.Disk, false);
                long start = sel.Partition?.Offset ?? 0, len = sel.Partition?.Length ?? disk.Length;
                return SurfaceTest.Run(disk, start, len, CellCount, prog, _cts.Token, _pause);
            });
            Version++;
            Fraction = 1;
            foreach (var b in res.BadSectors.Take(500)) BadSectors.Add($"setor {b:N0}");
            Verdict = res.Bad > 0 ? $"{res.BadSectors.Count} setores ilegíveis. Faça uma imagem deste disco (Backup e imagem) antes de qualquer outra coisa — e não confie mais nele."
                    : res.VerySlow > 0 ? $"Nenhum setor ilegível, mas {res.VerySlow} trechos muito lentos: sinal de setores fracos. Vale fazer backup."
                    : "Superfície saudável: todos os blocos leram sem erro e em tempo normal.";
            Elapsed = Fmt.Duration(res.Elapsed) + $" · média {res.AvgMBps:0} MB/s";
        }
        catch (OperationCanceledException) { Verdict = "Teste interrompido."; }
        catch (Exception ex) { Verdict = "Erro: " + ex.Message; }
        finally { Running = false; }
    }

    [RelayCommand] private void Stop() { _pause.Set(); _cts?.Cancel(); }
    [RelayCommand] private void TogglePause() { if (_pause.IsSet) _pause.Reset(); else _pause.Set(); Paused = !_pause.IsSet; }

    [RelayCommand]
    private async Task SaveReport()
    {
        var f = await MainViewModel.SaveFile("Salvar relatório", $"Teste de superfície {DateTime.Now:yyyy-MM-dd HH-mm}.txt", "txt", "Texto");
        if (f == null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"LibertyX Partition — teste de superfície — {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine(TargetText);
        sb.AppendLine($"Bons: {Good} · Lentos: {Slow} · Muito lentos: {VerySlow} · Ilegíveis: {Bad}");
        sb.AppendLine(Verdict);
        foreach (var b in BadSectors) sb.AppendLine(b);
        await File.WriteAllTextAsync(f, sb.ToString(), Encoding.UTF8);
        Main.ShowToast("Relatório salvo.");
    }
}

// ───────────────────────── saúde ─────────────────────────

public sealed partial class HealthViewModel : PageViewModel
{
    public override string Title => "Saúde e propriedades";
    public override string IconKey => "Health";
    public PickerViewModel Picker { get; } = new() { ShowPartitions = false };
    public ObservableCollection<SmartAttribute> Attributes { get; } = [];
    public ObservableCollection<InfoRow> Properties { get; } = [];
    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private string _healthText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private Color _healthColor = Color.Parse("#77767F");
    [ObservableProperty] private double _healthValue;
    [ObservableProperty] private string _temp = "—", _hours = "—", _cycles = "—", _life = "—", _written = "—", _read = "—";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private bool _busy;

    public HealthViewModel() { Picker.Changed += it => { if (it != null) _ = Load(it.Disk); }; }
    public override void OnDisksChanged() => Picker.Load(Main.Disks);
    public override void OnShown(object? arg)
    {
        if (Picker.Groups.Count == 0) Picker.Load(Main.Disks);
        if (arg is DiskInfo d) Picker.Select(d, null);
        else if (Picker.Selected == null && Picker.Groups.Count > 0) Picker.Select(Picker.Groups[0].Header);
    }

    private async Task Load(DiskInfo d)
    {
        Busy = true;
        var s = d.Smart ?? await Task.Run(() => { try { return DiskSystem.Native.ReadSmart(d); } catch { return new SmartReport(); } });
        d.Smart = s;
        Busy = false;
        Loaded = true;
        HealthText = Ui.HealthText(s.Health);
        HealthColor = s.Health switch { HealthLevel.Good => Color.Parse("#35D0BE"), HealthLevel.Caution => Color.Parse("#FFA24D"), HealthLevel.Bad => Color.Parse("#FF5A5A"), _ => Color.Parse("#77767F") };
        HealthValue = s.Health switch { HealthLevel.Good => 1, HealthLevel.Caution => 0.6, HealthLevel.Bad => 0.25, _ => 0 };
        Summary = s.Summary;
        Source = s.Source;
        Temp = s.TemperatureC is { } t ? $"{t} °C" : "—";
        Hours = s.PowerOnHours is { } h ? $"{h:N0} h" : "—";
        Cycles = s.PowerCycles is { } c ? $"{c:N0}" : "—";
        Life = s.LifeRemainingPercent is { } l ? $"{l}%" : "—";
        Written = s.HostBytesWritten is { } w ? Fmt.Size(w) : "—";
        Read = s.HostBytesRead is { } r ? Fmt.Size(r) : "—";
        Attributes.Clear();
        foreach (var a in s.Attributes) Attributes.Add(a);
        Properties.Clear();
        Properties.Add(new("Modelo", d.Model));
        if (!string.IsNullOrEmpty(d.Serial)) Properties.Add(new("Nº de série", d.Serial));
        if (!string.IsNullOrEmpty(d.Firmware)) Properties.Add(new("Firmware", d.Firmware));
        Properties.Add(new("Interface", d.Bus + (d.Removable ? " · removível" : "")));
        Properties.Add(new("Tipo", d.IsSsd == true ? "SSD (sem partes móveis)" : d.IsSsd == false ? "HD mecânico" : "—"));
        Properties.Add(new("Capacidade", $"{Fmt.Size(d.Size)} · {d.Size:N0} bytes"));
        Properties.Add(new("Setores", $"{d.Size / d.SectorSize:N0} de {d.SectorSize} B (físico {d.PhysicalSectorSize} B)"));
        Properties.Add(new("Tabela", d.StyleText + (d.Table.Style == TableStyle.Gpt ? $" · GUID {d.Table.DiskGuid.ToString().ToUpperInvariant()}" : d.Table.Style == TableStyle.Mbr ? $" · assinatura {d.Table.DiskSignature:X8}" : "")));
        Properties.Add(new("Partições", $"{d.Partitions.Count(p => !p.IsUnallocated)}"));
        if (d.IsSystem) Properties.Add(new("Sistema", "Disco do Windows em uso"));
        foreach (var warn in d.Table.Warnings) Properties.Add(new("Aviso", warn));
    }
}

// ───────────────────────── converter ─────────────────────────

public sealed partial class ConvertViewModel : PageViewModel
{
    public override string Title => "Converter GPT ⇄ MBR";
    public override string IconKey => "Convert";
    public PickerViewModel Picker { get; } = new() { ShowPartitions = false, DiskFilter = d => d.Table.Style == TableStyle.None ? "Disco sem tabela de partições." : null };
    public ObservableCollection<string> Notes { get; } = [];
    [ObservableProperty] private string _from = "", _to = "";
    [ObservableProperty] private bool _ok;
    [ObservableProperty] private bool _offerMbr2Gpt;
    [ObservableProperty] private bool _hasDisk;
    private DiskInfo? _d;

    public ConvertViewModel() { Picker.Changed += it => Analyze(it?.Disk); }
    public override void OnDisksChanged() => Picker.Load(Main.Disks);
    public override void OnShown(object? arg) { if (Picker.Groups.Count == 0) Picker.Load(Main.Disks); if (arg is DiskInfo d) Picker.Select(d, null); }

    private void Analyze(DiskInfo? d)
    {
        _d = d; HasDisk = d != null; Notes.Clear();
        if (d == null) return;
        var target = d.Table.Style == TableStyle.Gpt ? TableStyle.Mbr : TableStyle.Gpt;
        From = d.StyleText; To = target == TableStyle.Gpt ? "GPT" : "MBR";
        var (ok, notes) = DiskOperations.AnalyzeConversion(d, target);
        foreach (var n in notes) Notes.Add(n);
        Ok = ok && !d.ReadOnly;
        if (d.ReadOnly) Notes.Add("Imagem anexada só para leitura.");
        OfferMbr2Gpt = d.IsSystem && target == TableStyle.Gpt && OperatingSystem.IsWindows();
    }

    [RelayCommand]
    private async Task Convert()
    {
        var d = _d; if (d == null) return;
        // disco vazio converte de graça; levar as partições junto é do Premium
        if (d.Table.Entries.Count > 0 && !await Main.RequirePremium("Converter sem perder dados",
            "No Básico dá para converter um disco sem partições. Converter levando as partições e os dados junto é do Premium.")) return;
        var target = d.Table.Style == TableStyle.Gpt ? TableStyle.Mbr : TableStyle.Gpt;
        Main.Queue($"Converter para {To}", $"{d.Title} ({d.Model}): {From} → {To}, sem apagar partições", "Convert", s => DiskOperations.ConvertStyle(MainViewModel.Fresh(d), target, s), d);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task Mbr2Gpt()
    {
        var d = _d; if (d == null) return;
        if (!await Main.RequirePremium("Converter o disco do Windows", "Converter o disco do sistema para GPT sem reinstalar o Windows é do Premium.")) return;
        if (!await Main.Confirm("Converter o disco do Windows com MBR2GPT",
            "O MBR2GPT é o conversor oficial da Microsoft: converte o disco do sistema sem perder dados e cria a partição EFI.\n\nDepois, é OBRIGATÓRIO entrar na configuração do firmware (BIOS) e trocar o modo de inicialização de Legado/CSM para UEFI, senão o Windows não inicia.", "Converter", true)) return;
        Main.Queue("Converter o disco do Windows (MBR2GPT)", $"{d.Title} ({d.Model}) → GPT; depois mude o firmware para UEFI", "Convert", async s =>
        {
            s.Progress(-1, "Validando e convertendo");
            var r = await WinCommands.Mbr2Gpt(d.Number, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
            if (!r.Ok) throw new InvalidOperationException("O MBR2GPT recusou: " + r.Message);
            s.Log("Pronto. Agora mude o firmware para UEFI antes de reiniciar.");
        }, d, dangerous: true);
    }
}

// ───────────────────────── alinhar ─────────────────────────

public sealed partial class AlignRow(PartitionInfo p) : ObservableObject
{
    public PartitionInfo P { get; } = p;
    public string Title => (P.Letter ?? P.Label ?? "*") + $" · {FsDetect.Name(P.FileSystem)} · {Fmt.Size(P.Length)}";
    public string Start => $"setor {P.StartLba:N0} (deslocamento {Fmt.Size(P.Offset)})";
    public bool Aligned => DiskOperations.IsAligned(P);
    public bool Misaligned => !Aligned;
    public long? Target { get; } = DiskOperations.IsAligned(p) ? null : DiskOperations.AlignedTarget(p);
    public string Plan => Aligned ? "Alinhada a 1 MB" : Target is { } t ? $"Vai para o setor {t:N0}" : "Sem espaço livre ao lado para alinhar";
    [ObservableProperty] private bool _isChecked = !DiskOperations.IsAligned(p) && !p.IsSystemCritical;
    public bool CanCheck => Misaligned && Target != null && !P.IsSystemCritical;
}

public sealed partial class AlignViewModel : PageViewModel
{
    public override string Title => "Alinhar partições";
    public override string IconKey => "Align";
    public PickerViewModel Picker { get; } = new() { ShowPartitions = false };
    public ObservableCollection<AlignRow> Rows { get; } = [];
    [ObservableProperty] private string _summary = "Escolha um disco à esquerda.";
    [ObservableProperty] private bool _canAlign;
    private DiskInfo? _d;
    public AlignViewModel() { Picker.Changed += it => Load(it?.Disk); }
    public override void OnDisksChanged() => Picker.Load(Main.Disks);
    public override void OnShown(object? arg) { if (Picker.Groups.Count == 0) Picker.Load(Main.Disks); if (arg is DiskInfo d) Picker.Select(d, null); }

    private void Load(DiskInfo? d)
    {
        _d = d; Rows.Clear();
        if (d == null) return;
        foreach (var p in d.Partitions.Where(p => p.Entry != null)) Rows.Add(new AlignRow(p));
        int mis = Rows.Count(r => r.Misaligned);
        Summary = mis == 0 ? "Todas as partições já estão alinhadas a 1 MB. Nada a fazer." : $"{mis} partição(ões) desalinhada(s). Em SSD e discos de setor 4K, cada escrita nelas toca dois blocos físicos: mais lentidão e mais desgaste.";
        CanAlign = Rows.Any(r => r.CanCheck) && d.CanModify;
    }

    [RelayCommand]
    private async Task Align()
    {
        var d = _d; if (d == null) return;
        var todo = Rows.Where(r => r.IsChecked && r.CanCheck).ToList();
        if (todo.Count == 0) return;
        Main.Queue("Alinhar partições", $"{todo.Count} partição(ões) de {d.Title} deslocadas até o limite de 1 MB (move os dados)", "Align", s =>
        {
            foreach (var r in todo)
            {
                var fresh = DiskSystem.Enumerate().First(x => x.Number == d.Number && x.IsImage == d.IsImage);
                var p = fresh.Partitions.First(x => x.Offset == r.P.Offset);
                var target = DiskOperations.AlignedTarget(p);
                if (target == null) { s.Log($"{r.Title}: sem espaço — pulada."); continue; }
                DiskOperations.MovePartition(p, target.Value, s);
            }
        }, d, canPause: true);
        await Task.CompletedTask;
    }
}

// ───────────────────────── limpar ─────────────────────────

public sealed partial class WipeViewModel : PageViewModel
{
    public override string Title => "Limpar disco";
    public override string IconKey => "Wipe";
    public PickerViewModel Picker { get; } = new()
    {
        DiskFilter = d => d.IsSystem ? "Disco do Windows em uso." : d.ReadOnly ? "Imagem só para leitura." : null,
        PartitionFilter = p => p.IsSystemCritical ? "Partição do sistema em uso." : p.Disk.ReadOnly ? "Imagem só para leitura." : null,
    };
    public List<string> Methods { get; } = Enum.GetValues<WipeMethod>().Select(Wiper.Describe).ToList();
    [ObservableProperty] private int _method;
    public bool CanWipe => Picker.Selected != null;
    public WipeViewModel() { Picker.Changed += _ => OnPropertyChanged(nameof(CanWipe)); }
    public override void OnDisksChanged() => Picker.Load(Main.Disks);
    public override void OnShown(object? arg)
    {
        if (Picker.Groups.Count == 0) Picker.Load(Main.Disks);
        if (arg is DiskInfo d) Picker.Select(d, null);
        if (arg is PartitionInfo p) Picker.Select(p.Disk, p);
    }

    [RelayCommand]
    private async Task Wipe()
    {
        var sel = Picker.Selected; if (sel == null) return;
        var m = (WipeMethod)Method;
        string what = sel.Partition == null ? $"o disco {sel.Disk.Title} inteiro ({sel.Disk.Model}, {Fmt.Size(sel.Disk.Size)}), com todas as partições" : $"a partição {sel.Title} ({Fmt.Size(sel.Partition.Length)})";
        var sd = sel.Disk; var sp = sel.Partition;
        Main.Queue("Limpar (sobrescrever)", $"{what} — {Wiper.Describe(m)}. Sem volta: nenhuma recuperação traz os dados depois.", "Wipe", s =>
        {
            var pq = sp == null ? null : MainViewModel.FreshPart(sp);
            DiskOperations.Wipe(pq?.Disk ?? MainViewModel.Fresh(sd), pq, m, s);
        }, sd, dangerous: true);
        await Task.CompletedTask;
    }
}

// ───────────────────────── excluir todas ─────────────────────────

public sealed partial class DeleteAllViewModel : PageViewModel
{
    public override string Title => "Excluir todas as partições";
    public override string IconKey => "DeleteAll";
    public PickerViewModel Picker { get; } = new() { ShowPartitions = false, DiskFilter = d => d.IsSystem ? "Disco do Windows em uso." : d.ReadOnly ? "Imagem só para leitura." : d.Table.Entries.Count == 0 ? "Não há partições." : null };
    public ObservableCollection<PartitionVM> Parts { get; } = [];
    public bool CanDelete => Picker.Selected != null;
    public DeleteAllViewModel()
    {
        Picker.Changed += it =>
        {
            Parts.Clear();
            if (it != null) { var dvm = new DiskVM(it.Disk); foreach (var p in dvm.Partitions) Parts.Add(p); }
            OnPropertyChanged(nameof(CanDelete));
        };
    }
    public override void OnDisksChanged() => Picker.Load(Main.Disks);
    public override void OnShown(object? arg) { if (Picker.Groups.Count == 0) Picker.Load(Main.Disks); if (arg is DiskInfo d) Picker.Select(d, null); }

    [RelayCommand]
    private async Task Delete()
    {
        var d = Picker.Selected?.Disk; if (d == null) return;
        Main.Queue("Excluir todas as partições", $"As {d.Table.Entries.Count} partições de {d.Title} ({d.Model}) saem da tabela (Recuperar partição traz de volta)", "DeleteAll",
            s => DiskOperations.DeleteAllPartitions(MainViewModel.Fresh(d), s), d, dangerous: true);
        await Task.CompletedTask;
    }
}
