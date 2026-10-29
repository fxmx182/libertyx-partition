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
    public override string Title => L.T("Teste de superfície");
    public override string IconKey => "Surface";
    public PickerViewModel Picker { get; } = new() { AllowUnallocated = true };
    public const int CellCount = 2400;
    [ObservableProperty] private byte[] _cells = new byte[CellCount];
    [ObservableProperty] private int _version;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSaveReport))] private bool _running;
    [ObservableProperty] private bool _paused;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSaveReport))] private bool _hasRun;
    /// <summary>Uma propriedade só: IsVisible + classe "hidden" juntos não se combinam (o IsVisible vence).</summary>
    public bool CanSaveReport => HasRun && !Running;
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
            Elapsed = Fmt.Duration(sw.Elapsed) + (Fraction > 0.01 ? L.T($" · faltam ~{Fmt.Duration(TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - Fraction) / Fraction))}") : "");
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
            foreach (var b in res.BadSectors.Take(500)) BadSectors.Add(L.T($"setor {b:N0}"));
            Verdict = res.Bad > 0 ? L.T($"{res.BadSectors.Count} setores ilegíveis. Faça uma imagem deste disco (Backup e imagem) antes de qualquer outra coisa — e não confie mais nele.")
                    : res.VerySlow > 0 ? L.T($"Nenhum setor ilegível, mas {res.VerySlow} trechos muito lentos: sinal de setores fracos. Vale fazer backup.")
                    : L.T("Superfície saudável: todos os blocos leram sem erro e em tempo normal.");
            Elapsed = Fmt.Duration(res.Elapsed) + L.T($" · média {res.AvgMBps:0} MB/s");
        }
        catch (OperationCanceledException) { Verdict = L.T("Teste interrompido."); }
        catch (Exception ex) { Verdict = L.T("Erro: ") + ex.Message; }
        finally { Running = false; }
    }

    [RelayCommand] private void Stop() { _pause.Set(); _cts?.Cancel(); }
    [RelayCommand] private void TogglePause() { if (_pause.IsSet) _pause.Reset(); else _pause.Set(); Paused = !_pause.IsSet; }

    [RelayCommand]
    private async Task SaveReport()
    {
        var f = await MainViewModel.SaveFile(L.T("Salvar relatório"), L.T($"Teste de superfície {DateTime.Now:yyyy-MM-dd HH-mm}.txt"), "txt", L.T("Texto"));
        if (f == null) return;
        var sb = new StringBuilder();
        sb.AppendLine(L.T($"LibertyX Partition — teste de superfície — {DateTime.Now:dd/MM/yyyy HH:mm}"));
        sb.AppendLine(TargetText);
        sb.AppendLine(L.T($"Bons: {Good} · Lentos: {Slow} · Muito lentos: {VerySlow} · Ilegíveis: {Bad}"));
        sb.AppendLine(Verdict);
        foreach (var b in BadSectors) sb.AppendLine(b);
        await File.WriteAllTextAsync(f, sb.ToString(), Encoding.UTF8);
        Main.ShowToast(L.T("Relatório salvo."));
    }
}

// ───────────────────────── saúde ─────────────────────────

public sealed partial class HealthViewModel : PageViewModel
{
    public override string Title => L.T("Saúde e propriedades");
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
        Properties.Add(new(L.T("Modelo"), d.Model));
        if (!string.IsNullOrEmpty(d.Serial)) Properties.Add(new(L.T("Nº de série"), d.Serial));
        if (!string.IsNullOrEmpty(d.Firmware)) Properties.Add(new(L.T("Firmware"), d.Firmware));
        Properties.Add(new(L.T("Interface"), d.Bus + (d.Removable ? L.T(" · removível") : "")));
        Properties.Add(new(L.T("Tipo"), d.IsSsd == true ? L.T("SSD (sem partes móveis)") : d.IsSsd == false ? L.T("HD mecânico") : "—"));
        Properties.Add(new(L.T("Capacidade"), L.T($"{Fmt.Size(d.Size)} · {d.Size:N0} bytes")));
        Properties.Add(new(L.T("Setores"), L.T($"{d.Size / d.SectorSize:N0} de {d.SectorSize} B (físico {d.PhysicalSectorSize} B)")));
        Properties.Add(new(L.T("Tabela"), d.StyleText + (d.Table.Style == TableStyle.Gpt ? $" · GUID {d.Table.DiskGuid.ToString().ToUpperInvariant()}" : d.Table.Style == TableStyle.Mbr ? L.T($" · assinatura {d.Table.DiskSignature:X8}") : "")));
        Properties.Add(new(L.T("Partições"), $"{d.Partitions.Count(p => !p.IsUnallocated)}"));
        if (d.IsSystem) Properties.Add(new(L.T("Sistema"), Ui.SystemDiskInUse.TrimEnd('.')));
        foreach (var warn in d.Table.Warnings) Properties.Add(new(L.T("Aviso"), warn));
    }
}

// ───────────────────────── converter ─────────────────────────

public sealed partial class ConvertViewModel : PageViewModel
{
    public override string Title => L.T("Converter GPT ⇄ MBR");
    public override string IconKey => "Convert";
    public PickerViewModel Picker { get; } = new() { ShowPartitions = false, DiskFilter = d => d.Table.Style == TableStyle.None ? L.T("Disco sem tabela de partições.") : null };
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
        if (d.ReadOnly) Notes.Add(L.T("Imagem anexada só para leitura."));
        OfferMbr2Gpt = d.IsSystem && target == TableStyle.Gpt && OperatingSystem.IsWindows();
    }

    [RelayCommand]
    private async Task Convert()
    {
        var d = _d; if (d == null) return;
        // disco vazio converte de graça; levar as partições junto é do Premium
        if (d.Table.Entries.Count > 0 && !await Main.RequirePremium(L.T("Converter sem perder dados"),
            L.T("No Básico dá para converter um disco sem partições. Converter levando as partições e os dados junto é do Premium."))) return;
        var target = d.Table.Style == TableStyle.Gpt ? TableStyle.Mbr : TableStyle.Gpt;
        Main.Queue(L.T($"Converter para {To}"), L.T($"{d.Title} ({d.Model}): {From} → {To}, sem apagar partições"), "Convert", s => DiskOperations.ConvertStyle(MainViewModel.Fresh(d), target, s), d);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task Mbr2Gpt()
    {
        var d = _d; if (d == null) return;
        if (!await Main.RequirePremium(L.T("Converter o disco do Windows"), L.T("Converter o disco do sistema para GPT sem reinstalar o Windows é do Premium."))) return;
        if (!await Main.Confirm(L.T("Converter o disco do Windows com MBR2GPT"),
            L.T("O MBR2GPT é o conversor oficial da Microsoft: converte o disco do sistema sem perder dados e cria a partição EFI.\n\nDepois, é OBRIGATÓRIO entrar na configuração do firmware (BIOS) e trocar o modo de inicialização de Legado/CSM para UEFI, senão o Windows não inicia."), L.T("Converter"), true)) return;
        Main.Queue(L.T("Converter o disco do Windows (MBR2GPT)"), L.T($"{d.Title} ({d.Model}) → GPT; depois mude o firmware para UEFI"), "Convert", async s =>
        {
            s.Progress(-1, L.T("Validando e convertendo"));
            var r = await WinCommands.Mbr2Gpt(d.Number, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
            if (!r.Ok) throw new InvalidOperationException(L.T("O MBR2GPT recusou: ") + r.Message);
            s.Log(L.T("Pronto. Agora mude o firmware para UEFI antes de reiniciar."));
        }, d, dangerous: true);
    }
}

// ───────────────────────── alinhar ─────────────────────────

public sealed partial class AlignRow(PartitionInfo p) : ObservableObject
{
    public PartitionInfo P { get; } = p;
    public string Title => (P.Letter ?? P.Label ?? "*") + $" · {FsDetect.Name(P.FileSystem)} · {Fmt.Size(P.Length)}";
    public string Start => L.T($"setor {P.StartLba:N0} (deslocamento {Fmt.Size(P.Offset)})");
    public bool Aligned => DiskOperations.IsAligned(P);
    public bool Misaligned => !Aligned;
    public long? Target { get; } = DiskOperations.IsAligned(p) ? null : DiskOperations.AlignedTarget(p);
    public string Plan => Aligned ? L.T("Alinhada a 1 MB") : Target is { } t ? L.T($"Vai para o setor {t:N0}") : L.T("Sem espaço livre ao lado para alinhar");
    [ObservableProperty] private bool _isChecked = !DiskOperations.IsAligned(p) && !p.IsSystemCritical;
    public bool CanCheck => Misaligned && Target != null && !P.IsSystemCritical;
}

public sealed partial class AlignViewModel : PageViewModel
{
    public override string Title => L.T("Alinhar partições");
    public override string IconKey => "Align";
    public PickerViewModel Picker { get; } = new() { ShowPartitions = false };
    public ObservableCollection<AlignRow> Rows { get; } = [];
    [ObservableProperty] private string _summary = L.T("Escolha um disco à esquerda.");
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
        Summary = mis == 0 ? L.T("Todas as partições já estão alinhadas a 1 MB. Nada a fazer.") : L.T($"{mis} partição(ões) desalinhada(s). Em SSD e discos de setor 4K, cada escrita nelas toca dois blocos físicos: mais lentidão e mais desgaste.");
        CanAlign = Rows.Any(r => r.CanCheck) && d.CanModify;
    }

    [RelayCommand]
    private async Task Align()
    {
        var d = _d; if (d == null) return;
        var todo = Rows.Where(r => r.IsChecked && r.CanCheck).ToList();
        if (todo.Count == 0) return;
        Main.Queue(L.T("Alinhar partições"), L.T($"{todo.Count} partição(ões) de {d.Title} deslocadas até o limite de 1 MB (move os dados)"), "Align", s =>
        {
            foreach (var r in todo)
            {
                var fresh = DiskSystem.Enumerate().First(x => x.Number == d.Number && x.IsImage == d.IsImage);
                var p = fresh.Partitions.First(x => x.Offset == r.P.Offset);
                var target = DiskOperations.AlignedTarget(p);
                if (target == null) { s.Log(L.T($"{r.Title}: sem espaço — pulada.")); continue; }
                DiskOperations.MovePartition(p, target.Value, s);
            }
        }, d, canPause: true);
        await Task.CompletedTask;
    }
}

// ───────────────────────── limpar ─────────────────────────

public sealed partial class WipeViewModel : PageViewModel
{
    public override string Title => L.T("Limpar disco");
    public override string IconKey => "Wipe";
    public PickerViewModel Picker { get; } = new()
    {
        DiskFilter = d => d.IsSystem ? Ui.SystemDiskInUse : d.ReadOnly ? L.T("Imagem só para leitura.") : null,
        PartitionFilter = p => p.IsSystemCritical ? L.T("Partição do sistema em uso.") : p.Disk.ReadOnly ? L.T("Imagem só para leitura.") : null,
    };
    /// <summary>TRIM e o apagamento do fabricante só existem na camada do Linux.</summary>
    private static readonly List<WipeMethod> MethodValues = Enum.GetValues<WipeMethod>().Where(m => OperatingSystem.IsLinux() || m is not (WipeMethod.Discard or WipeMethod.Sanitize)).ToList();
    public List<string> Methods { get; } = MethodValues.Select(Wiper.Describe).ToList();
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
        var m = MethodValues[Math.Clamp(Method, 0, MethodValues.Count - 1)];
        if (m == WipeMethod.Sanitize && (sel.Partition != null || Core.Platform.Linux.LinuxErase.CannotSanitize(sel.Disk) is not null))
        {
            await Main.Alert(L.T("Apagamento do fabricante"), sel.Partition != null ? L.T("Esse apagamento é sempre do disco inteiro: escolha o disco, não uma partição.") : Core.Platform.Linux.LinuxErase.CannotSanitize(sel.Disk)!, "warn");
            return;
        }
        if (m == WipeMethod.Discard && sel.Disk.IsSsd == false) { await Main.Alert(L.T("HD mecânico"), L.T("TRIM só vale para SSD, cartões e pendrives. Num HD, use “Preencher com zeros”."), "warn"); return; }
        string what = sel.Partition == null ? L.T($"o disco {sel.Disk.Title} inteiro ({sel.Disk.Model}, {Fmt.Size(sel.Disk.Size)}), com todas as partições") : L.T($"a partição {sel.Title} ({Fmt.Size(sel.Partition.Length)})");
        var sd = sel.Disk; var sp = sel.Partition;
        Main.Queue(L.T("Limpar (sobrescrever)"), L.T($"{what} — {Wiper.Describe(m)}. Sem volta: nenhuma recuperação traz os dados depois."), "Wipe", s =>
        {
            var pq = sp == null ? null : MainViewModel.FreshPart(sp);
            DiskOperations.Wipe(pq?.Disk ?? MainViewModel.Fresh(sd), pq, m, s);
        }, sd, dangerous: true);
        await Task.CompletedTask;
    }
}

// ───────────────────────── detector de LUKS ─────────────────────────

public sealed class LuksRow(LuksFinding f, DiskInfo disk, LuksViewModel page)
{
    public LuksFinding F { get; } = f;
    public string Title => F.Title;
    public string StateText => F.StateText;
    public string Where => L.T("Onde: ") + F.Where;
    public string Explain => F.Explain();
    public string Details => L.T($"Posição: {Fmt.Size(F.Start)} ({F.Start / disk.SectorSize:N0} setores) · cópias do cabeçalho em ") + string.Join(", ", F.Headers.Keys.Select(h => h == F.Start ? "0" : "+" + Fmt.Size(h - F.Start)))
                             + (F.Uuid is { Length: > 0 } u ? $" · UUID {u}" : "");
    public bool IsActive => F.State == LuksState.Active;
    public bool IsOpenable => F.State == LuksState.Openable;
    public bool IsDead => F.State == LuksState.Dead;
    public bool IsInFile => F.State == LuksState.InFile;
    public bool CanErase => !IsInFile && !disk.ReadOnly;
    public string EraseText => IsActive ? L.T("Destruir este contêiner") : L.T("Apagar este resto");
    public IAsyncRelayCommand Erase => new AsyncRelayCommand(() => page.Erase(this));
}

public sealed partial class LuksViewModel : PageViewModel
{
    public override string Title => L.T("Detector de LUKS");
    public override string IconKey => "Lock";
    public PickerViewModel Picker { get; } = new() { AllowUnallocated = true };
    [ObservableProperty] private bool _deep = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStart), nameof(CanSaveReport))] private bool _running;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSaveReport))] private bool _hasRun;
    public bool CanSaveReport => HasRun && !Running;
    public bool CanStart => Picker.Selected != null && !Running;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _speed = "—";
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VerdictGood), nameof(VerdictDanger), nameof(VerdictGold))] private string _verdictClass = "gold";
    public bool VerdictGood => VerdictClass == "green";
    public bool VerdictDanger => VerdictClass == "danger";
    public bool VerdictGold => VerdictClass == "gold";
    [ObservableProperty] private int _found;
    public ObservableCollection<LuksRow> Results { get; } = [];
    private CancellationTokenSource? _cts;
    private PickItem? _last;
    private bool _recheck;

    public LuksViewModel() { Picker.Changed += _ => OnPropertyChanged(nameof(CanStart)); }
    public override void OnDisksChanged()
    {
        if (Running) return;
        Picker.Load(Main.Disks);
        // depois de apagar pela fila: confere de novo no mesmo lugar
        if (_recheck && _last != null)
        {
            _recheck = false;
            var d = Main.Disks.FirstOrDefault(x => x.Number == _last.Disk.Number && x.IsImage == _last.Disk.IsImage);
            if (d != null) { Picker.Select(d, _last.Partition == null ? null : d.Partitions.FirstOrDefault(p => p.Offset == _last.Partition.Offset)); _ = Start(); }
        }
    }
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
        _last = sel;
        _cts = new CancellationTokenSource();
        Results.Clear();
        Found = 0; Fraction = 0; Speed = "—"; Verdict = "";
        Running = true; HasRun = true;
        var mode = Deep ? LuksScanMode.Full : LuksScanMode.Quick;
        TargetText = (sel.Partition == null ? $"{sel.Disk.Title} · {sel.Disk.Model}" : $"{sel.Title} · {sel.Disk.Title}") + (Deep ? L.T(" · completa") : L.T(" · rápida"));
        var sw = Stopwatch.StartNew();
        var prog = new Progress<LuksProgress>(p =>
        {
            Fraction = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            Found = p.Found;
            if (p.BytesPerSecond > 0) Speed = Fmt.Speed(p.BytesPerSecond);
            Elapsed = Fmt.Duration(sw.Elapsed) + (Deep && Fraction is > 0.01 and < 1 ? L.T($" · faltam ~{Fmt.Duration(TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - Fraction) / Fraction))}") : "");
        });
        try
        {
            var disk = sel.Disk;
            var res = await Task.Run(() =>
            {
                using var dev = DiskSystem.OpenDisk(disk, false);
                var layout = LuksFinder.Layout(disk, dev.Length);
                long start = sel.Partition?.Offset ?? 0, len = sel.Partition?.Length ?? dev.Length;
                return LuksFinder.Find(dev, layout, start, len, mode, prog, _cts.Token, a => LuksFinder.UsedRanges(dev.Slice(a.Offset, a.Length)));
            });
            foreach (var f in res.OrderBy(f => f.Start)) Results.Add(new LuksRow(f, disk, this));
            Found = res.Count;
            Fraction = 1;
            Elapsed = Fmt.Duration(sw.Elapsed);
            int open = res.Count(f => f.State == LuksState.Openable), active = res.Count(f => f.State == LuksState.Active), files = res.Count(f => f.State == LuksState.InFile);
            (Verdict, VerdictClass) = res.Count == 0
                ? (Deep ? L.T("Nenhum LUKS aqui: nem cabeçalho, nem a cópia reserva, em nenhum setor.") : L.T("Nada no começo das partições e dos espaços livres. Para ter certeza de que não sobrou nenhum pedaço, faça a busca completa."), "green")
                : open > 0 ? (L.T($"{open} {(open == 1 ? L.T("resto ainda abre") : L.T("restos ainda abrem"))} com a senha: os dados antigos continuam acessíveis para quem a tiver. Apague se a ideia era sumir com eles."), "danger")
                : active > 0 && res.Count == active ? (L.T($"{(active == 1 ? L.T("Um contêiner LUKS em uso") : L.T($"{active} contêineres LUKS em uso"))} e nenhum resto solto."), "gold")
                : files > 0 && res.Count == files ? (L.T("Só há LUKS dentro de arquivos de hoje (contêineres ou backups de cabeçalho). Apague esses arquivos pelo sistema, se não precisar deles."), "gold")
                : (L.T("Sobraram restos de LUKS que não abrem mais (sem chave inteira). Não dão acesso aos dados, mas revelam que o disco foi criptografado — apague se quiser o disco limpo."), "gold");
        }
        catch (OperationCanceledException) { Verdict = L.T("Busca interrompida."); VerdictClass = "gold"; }
        catch (Exception ex) { Verdict = L.T("Erro: ") + ex.Message; VerdictClass = "danger"; }
        finally { Running = false; }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    internal async Task Erase(LuksRow r)
    {
        var d = Main.Disks.FirstOrDefault(x => x.Number == _last?.Disk.Number && x.IsImage == _last.Disk.IsImage) ?? _last?.Disk;
        if (d == null) return;
        if (r.F.Area is { Critical: true } && r.F.Area.Offset == r.F.Start) { await Main.Alert(L.T("Em uso pelo sistema"), L.T("Esse contêiner é o do sistema que está rodando. Para destruí-lo, inicie por um pendrive."), "warn"); return; }
        string detail = r.IsActive
            ? L.T($"{r.Title} em {r.F.Where}: o cabeçalho e as chaves são zerados — o volume e tudo o que há nele ficam ilegíveis para sempre, mesmo com a senha.")
            : L.T($"{r.Title} em {r.F.Where}: zera as cópias do cabeçalho e as áreas das chaves. Só escreve onde não há nada de hoje (espaço livre); o que estiver em uso fica como está.");
        var f = r.F;
        _recheck = true;
        Main.Queue(r.IsActive ? L.T("Destruir contêiner LUKS") : L.T("Apagar resto de LUKS"), detail, "Lock", s => DiskOperations.EraseLuks(MainViewModel.Fresh(d), f, s), d, dangerous: true);
        Main.ShowToast(L.T("Na fila. Aplique as alterações pendentes; depois a busca roda de novo sozinha para conferir."));
    }

    [RelayCommand]
    private async Task SaveReport()
    {
        var file = await MainViewModel.SaveFile(L.T("Salvar relatório"), L.T($"Detector de LUKS {DateTime.Now:yyyy-MM-dd HH-mm}.txt"), "txt", L.T("Texto"));
        if (file == null) return;
        var sb = new StringBuilder();
        sb.AppendLine(L.T($"LibertyX Partition — detector de LUKS — {DateTime.Now:dd/MM/yyyy HH:mm}"));
        sb.AppendLine(TargetText);
        sb.AppendLine(Verdict);
        foreach (var r in Results) { sb.AppendLine(); sb.AppendLine($"{r.Title} — {r.StateText}"); sb.AppendLine(r.Where); sb.AppendLine(r.Details); sb.AppendLine(r.Explain); }
        await File.WriteAllTextAsync(file, sb.ToString(), Encoding.UTF8);
        Main.ShowToast(L.T("Relatório salvo."));
    }
}

// ───────────────────────── excluir todas ─────────────────────────

public sealed partial class DeleteAllViewModel : PageViewModel
{
    public override string Title => L.T("Excluir todas as partições");
    public override string IconKey => "DeleteAll";
    public PickerViewModel Picker { get; } = new() { ShowPartitions = false, DiskFilter = d => d.IsSystem ? Ui.SystemDiskInUse : d.ReadOnly ? L.T("Imagem só para leitura.") : d.Table.Entries.Count == 0 ? L.T("Não há partições.") : null };
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
        Main.Queue(L.T("Excluir todas as partições"), L.T($"As {d.Table.Entries.Count} partições de {d.Title} ({d.Model}) saem da tabela (Recuperar partição traz de volta)"), "DeleteAll",
            s => DiskOperations.DeleteAllPartitions(MainViewModel.Fresh(d), s), d, dangerous: true);
        await Task.CompletedTask;
    }
}
