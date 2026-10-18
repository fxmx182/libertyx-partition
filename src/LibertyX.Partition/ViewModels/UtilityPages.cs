using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;
using LibertyX.Partition.Core.Recovery;
using LibertyX.Partition.Core.Tools;

namespace LibertyX.Partition.ViewModels;

// ───────────────────────── BitLocker ─────────────────────────

public sealed partial class BitLockerRow(WinCommands.BitLockerInfo i, BitLockerViewModel owner) : ObservableObject
{
    public WinCommands.BitLockerInfo I { get; } = i;
    public string Mount => I.Mount;
    public bool Locked => I.Lock.Contains("Locked", StringComparison.OrdinalIgnoreCase);
    public bool Unlocked => !Locked;
    public bool Encrypted => !I.Status.Contains("FullyDecrypted", StringComparison.OrdinalIgnoreCase);
    public bool Decrypted => !Encrypted;
    public bool CanLock => Unlocked && Encrypted;
    public string StatusText => I.Status switch
    {
        "FullyEncrypted" => "Criptografado", "FullyDecrypted" => "Sem BitLocker", "EncryptionInProgress" => $"Criptografando… {I.Percent}%",
        "DecryptionInProgress" => $"Descriptografando… {100 - I.Percent}%", "EncryptionPaused" => "Criptografia pausada", _ => I.Status,
    };
    public string LockText => Locked ? "Bloqueado" : "Desbloqueado";
    public string Detail => Encrypted ? $"{I.Method} · protetores: {(I.Protectors.Length > 0 ? I.Protectors : "nenhum")} · proteção {(I.Protection == "On" ? "ativa" : "suspensa")}" : "Volume sem criptografia.";
    public double Percent => I.Percent / 100.0;
    public IAsyncRelayCommand Unlock => new AsyncRelayCommand(() => owner.Unlock(this));
    public IAsyncRelayCommand LockCmd => new AsyncRelayCommand(() => owner.Lock(this));
    public IAsyncRelayCommand Disable => new AsyncRelayCommand(() => owner.Disable(this));
    public IAsyncRelayCommand Enable => new AsyncRelayCommand(() => owner.Enable(this));
    public IAsyncRelayCommand Keys => new AsyncRelayCommand(() => owner.ShowKeys(this));
}

public sealed partial class BitLockerViewModel : PageViewModel
{
    public override string Title => "BitLocker";
    public override string IconKey => "BitLocker";
    public ObservableCollection<BitLockerRow> Volumes { get; } = [];
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    public override void OnShown(object? arg) => _ = Load();

    [RelayCommand]
    private async Task Load()
    {
        Volumes.Clear();
        if (!OperatingSystem.IsWindows()) { Message = "O BitLocker só é gerenciado no Windows."; return; }
        Busy = true;
        var list = await WinCommands.BitLockerStatus();
        Busy = false;
        foreach (var v in list) Volumes.Add(new BitLockerRow(v, this));
        Message = list.Count == 0 ? "Não foi possível consultar o BitLocker (no Windows Home, só a “Criptografia do dispositivo” existe)." : "";
    }

    public async Task Unlock(BitLockerRow r)
    {
        var f = new FormDialogViewModel { Title = $"Desbloquear {r.Mount}", IconKey = "Unlock", ConfirmText = "Desbloquear" };
        var mode = f.Add(new ChoiceField { Label = "Com" });
        mode.Options.Add("Senha"); mode.Options.Add("Chave de recuperação (48 dígitos)");
        var pw = f.Add(new TextField { Label = "Senha", Password = true });
        var key = f.Add(new TextField { Label = "Chave de recuperação", Watermark = "000000-000000-000000-000000-000000-000000-000000-000000", IsVisible = false });
        mode.Changed += i => { pw.IsVisible = i == 0; key.IsVisible = i == 1; };
        f.Note("Desbloqueado, o volume pode ser lido normalmente — inclusive pela Recuperação de dados, que passa a enxergar o conteúdo decifrado.", "gold");
        if (!await Main.ShowDialog(f)) return;
        var res = mode.SelectedIndex == 0 ? await WinCommands.BitLockerUnlockPassword(r.Mount, pw.Value) : await WinCommands.BitLockerUnlockRecovery(r.Mount, key.Value.Trim());
        Main.ShowToast(res.Ok ? $"{r.Mount} desbloqueado." : "Não desbloqueou: " + res.Message);
        await Load();
        if (res.Ok) await Main.Refresh();
    }

    public async Task Lock(BitLockerRow r)
    {
        var res = await WinCommands.BitLockerLock(r.Mount);
        Main.ShowToast(res.Ok ? $"{r.Mount} bloqueado." : res.Message);
        await Load();
    }

    public async Task Disable(BitLockerRow r)
    {
        if (!await Main.Confirm("Desativar BitLocker", $"{r.Mount} será descriptografado em segundo plano (pode levar horas; o computador pode ser usado normalmente).", "Desativar")) return;
        var res = await WinCommands.BitLockerDisable(r.Mount);
        Main.ShowToast(res.Ok ? "Descriptografia iniciada." : res.Message);
        await Load();
    }

    public async Task Enable(BitLockerRow r)
    {
        var f = new FormDialogViewModel { Title = $"Ativar BitLocker em {r.Mount}", IconKey = "Lock", ConfirmText = "Ativar" };
        var pw = f.Add(new TextField { Label = "Senha (mínimo 8 caracteres)", Password = true });
        var pw2 = f.Add(new TextField { Label = "Repita a senha", Password = true });
        f.Note("Uma chave de recuperação também é criada. Guarde-a (botão “Chave de recuperação”) — sem ela e sem a senha, os dados ficam inacessíveis para sempre.", "warn");
        f.Validate = () => pw.Value.Length < 8 ? "A senha precisa de 8 caracteres ou mais." : pw.Value != pw2.Value ? "As senhas não conferem." : null;
        if (!await Main.ShowDialog(f)) return;
        var res = await WinCommands.BitLockerEnablePassword(r.Mount, pw.Value);
        Main.ShowToast(res.Ok ? "Criptografia iniciada." : res.Message);
        await Load();
    }

    public async Task ShowKeys(BitLockerRow r)
    {
        var res = await WinCommands.BitLockerRecoveryKeys(r.Mount);
        var f = new FormDialogViewModel { Title = $"Chaves de recuperação de {r.Mount}", IconKey = "Key", ConfirmText = "Fechar" };
        var lines = res.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) f.Note(res.Ok ? "Nenhuma chave de recuperação cadastrada neste volume." : res.Message, "warn");
        foreach (var l in lines)
        {
            var parts = l.Split(' ', 2);
            f.Add(new InfoField { Label = parts[0], Text = parts.Length > 1 ? parts[1] : "" });
        }
        f.Note("Guarde a chave fora deste computador (papel, outro dispositivo). Selecione o texto para copiar.", "gold");
        await Main.ShowDialog(f);
    }
}

// ───────────────────────── benchmark ─────────────────────────

public sealed partial class BenchRow : ObservableObject
{
    public string Label { get; init; } = "";
    [ObservableProperty] private string _read = "—";
    [ObservableProperty] private string _write = "—";
    [ObservableProperty] private string _readDetail = "";
    [ObservableProperty] private string _writeDetail = "";
    [ObservableProperty] private double _readBar;
    [ObservableProperty] private double _writeBar;
}

public sealed partial class BenchmarkViewModel : PageViewModel
{
    public override string Title => "Benchmark de disco";
    public override string IconKey => "Benchmark";
    public ObservableCollection<string> Drives { get; } = [];
    [ObservableProperty] private int _driveIndex;
    [ObservableProperty] private int _sizeIndex = 1;
    [ObservableProperty] private int _durationIndex = 1;
    [ObservableProperty] private bool _includeWrite = true;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private double _fraction;
    public ObservableCollection<BenchRow> Rows { get; } = [];
    public ObservableCollection<string> Disks { get; } = [];
    [ObservableProperty] private int _diskIndex;
    [ObservableProperty] private List<double>? _curve;
    [ObservableProperty] private int _curveVersion;
    [ObservableProperty] private string _curveText = "";
    private List<DiskInfo> _disks = [];
    private CancellationTokenSource? _cts;
    public List<string> Sizes { get; } = ["256 MB", "1 GB", "4 GB"];
    public List<string> Durations { get; } = ["3 s por teste", "5 s por teste", "10 s por teste"];

    public BenchmarkViewModel()
    {
        foreach (var l in new[] { "Sequencial 1 MiB · fila 8", "Sequencial 1 MiB · fila 1", "Aleatório 4 KiB · fila 32", "Aleatório 4 KiB · fila 1" })
            Rows.Add(new BenchRow { Label = l });
    }

    public override void OnShown(object? arg) { _ = LoadDrives(); OnDisksChanged(); }

    public override void OnDisksChanged()
    {
        _disks = Main.Disks.ToList();
        Disks.Clear();
        foreach (var d in _disks) Disks.Add($"{d.Title} · {d.Model} · {Fmt.Size(d.Size)}");
    }

    private List<Bounded.DriveEntry> _drives = [];

    // fora da tela e com limite por unidade: um cartão com defeito travava a consulta e o app junto
    private async Task LoadDrives()
    {
        var list = await Task.Run(() => Bounded.ReadyDrives(TimeSpan.FromSeconds(4), DriveType.Fixed, DriveType.Removable));
        _drives = list;
        Drives.Clear();
        foreach (var d in list)
            Drives.Add(OperatingSystem.IsWindows() ? $"{d.Root.TrimEnd('\\')} {d.Label} · {Fmt.Size(d.Free)} livres{(d.Type == DriveType.Removable ? " · removível" : "")}" : d.Root);
        DriveIndex = Drives.Count > 0 ? 0 : -1;
    }

    // cartão e pendrive: 256 MB (1 GB num cartão lento só prepara o arquivo por minutos)
    partial void OnDriveIndexChanged(int value)
    {
        if (value >= 0 && value < _drives.Count && _drives[value].Type == DriveType.Removable && !Running) SizeIndex = 0;
    }

    [RelayCommand]
    private async Task Run()
    {
        if (DriveIndex < 0 || DriveIndex >= _drives.Count) return;
        var drive = _drives[DriveIndex];
        long size = SizeIndex switch { 0 => 256L << 20, 2 => 4L << 30, _ => 1L << 30 };
        if (drive.Free < size + (256L << 20)) { await Main.Alert("Pouco espaço", $"O teste precisa de {Fmt.Size(size)} livres em {drive.Root}."); return; }
        var dur = TimeSpan.FromSeconds(DurationIndex switch { 0 => 3, 2 => 10, _ => 5 });
        foreach (var r in Rows) { r.Read = r.Write = "—"; r.ReadBar = r.WriteBar = 0; r.ReadDetail = r.WriteDetail = ""; }
        _cts = new CancellationTokenSource();
        Running = true;
        double max = 1;
        var prog = new Progress<BenchProgress>(p =>
        {
            Phase = p.Phase; Fraction = p.Fraction;
            if (p.Partial is { } res)
            {
                int idx = res.Test.Id switch { "SEQ1M-Q8" => 0, "SEQ1M-Q1" => 1, "RND4K-Q32" => 2, _ => 3 };
                var row = Rows[idx];
                string val = res.MBps >= 100 ? $"{res.MBps:0}" : $"{res.MBps:0.0}";
                string det = res.Test.Sequential ? $"{res.AvgLatencyUs / 1000:0.0} ms" : $"{res.Iops:N0} IOPS · {res.AvgLatencyUs:0} µs";
                if (res.Test.Write) { row.Write = val; row.WriteDetail = det; } else { row.Read = val; row.ReadDetail = det; }
                max = Math.Max(max, res.MBps);
                foreach (var r in Rows)
                {
                    r.ReadBar = double.TryParse(r.Read, out var a) ? a / max : 0;
                    r.WriteBar = double.TryParse(r.Write, out var b) ? b / max : 0;
                }
            }
        });
        // em segundo plano: as conclusões de E/S (dezenas de milhares por segundo na fila 32)
        // não passam mais pela linha da tela — que travava e ainda inflava a latência medida
        var token = _cts.Token;
        bool write = IncludeWrite;
        try { await Task.Run(() => DiskBenchmark.RunAsync(drive.Root, size, dur, write, prog, token)); Phase = "Concluído"; }
        catch (OperationCanceledException) { Phase = "Cancelado"; }
        catch (Exception ex) { Phase = "Erro: " + ex.Message; }
        finally { Running = false; Fraction = 1; }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private async Task RunCurve()
    {
        if (_disks.Count == 0) return;
        var d = _disks[DiskIndex];
        Running = true;
        CurveText = "Lendo 100 pontos ao longo do disco…";
        _cts = new CancellationTokenSource();
        try
        {
            var pts = await Task.Run(() =>
            {
                using var dev = DiskSystem.OpenDisk(d, false);
                return DiskBenchmark.ReadCurve(dev, 100, new Progress<double>(f => Fraction = f), _cts.Token);
            });
            Curve = pts.Select(p => p.mbps).ToList();
            CurveVersion++;
            var v = Curve.Where(x => x > 0).ToList();
            CurveText = v.Count == 0 ? "Sem leituras." : $"Início {v.First():0} MB/s · fim {v.Last():0} MB/s · mínima {v.Min():0} MB/s · acesso médio {pts.Average(p => p.accessMs):0.0} ms" +
                (pts.Any(p => p.mbps == 0) ? " · ATENÇÃO: trechos ilegíveis" : "");
        }
        catch (Exception ex) { CurveText = "Erro: " + ex.Message; }
        finally { Running = false; }
    }
}

// ───────────────────────── analisador de espaço ─────────────────────────

public sealed partial class SpaceViewModel : PageViewModel
{
    public override string Title => "Analisador de espaço";
    public override string IconKey => "Space";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private SpaceNode? _current;
    [ObservableProperty] private SpaceNode? _selected;
    [ObservableProperty] private List<SpaceNode>? _items;
    [ObservableProperty] private int _version;
    public ObservableCollection<SpaceNode> Crumbs { get; } = [];
    public ObservableCollection<InfoRow> Largest { get; } = [];
    public ObservableCollection<InfoRow> ByType { get; } = [];
    public ObservableCollection<string> Roots { get; } = [];
    private CancellationTokenSource? _cts;
    private SpaceNode? _root;

    public override void OnShown(object? arg) => _ = LoadRoots();

    // fora da tela e com limite por unidade (unidade de rede caída ou cartão travado congelavam o app)
    private async Task LoadRoots()
    {
        var list = await Task.Run(() => Bounded.ReadyDrives(TimeSpan.FromSeconds(4), DriveType.Fixed, DriveType.Removable, DriveType.Network));
        Roots.Clear();
        foreach (var d in list) Roots.Add(d.Root);
        if (Path.Length == 0 && Roots.Count > 0) Path = Roots[0];
    }

    [RelayCommand] private void UseRoot(string r) => Path = r;

    [RelayCommand]
    private async Task Browse() { var f = await MainViewModel.PickFolder("Pasta para analisar"); if (f != null) Path = f; }

    [RelayCommand]
    private async Task Analyze()
    {
        if (!Directory.Exists(Path)) { Main.ShowToast("Pasta não encontrada."); return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Busy = true;
        var prog = new Progress<SpaceProgress>(p => Status = $"{p.Files:N0} arquivos · {Fmt.Size(p.Bytes)} · {p.Current}");
        try
        {
            var path = Path;
            _root = await Task.Run(() => SpaceAnalyzer.Analyze(path, prog, _cts.Token));
            Status = $"{_root.Files:N0} arquivos · {Fmt.Size(_root.Size)}";
            Open(_root);
            Largest.Clear();
            foreach (var f in SpaceAnalyzer.AllFiles(_root).OrderByDescending(f => f.Size).Take(12)) Largest.Add(new(Fmt.Size(f.Size), f.FullPath));
            ByType.Clear();
            foreach (var g in SpaceAnalyzer.AllFiles(_root).GroupBy(f => FileTypes.CategoryName(FileTypes.Categorize(f.Extension))).Select(g => (g.Key, sum: g.Sum(x => x.Size), n: g.Count())).OrderByDescending(g => g.sum))
                ByType.Add(new(g.Key, $"{Fmt.Size(g.sum)} · {g.n:N0} arquivos"));
        }
        catch (OperationCanceledException) { Status = "Cancelado."; }
        finally { Busy = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    public void Open(SpaceNode n)
    {
        Current = n;
        Items = n.Children?.ToList();
        Version++;
        Crumbs.Clear();
        var st = new Stack<SpaceNode>();
        for (var x = n; x != null; x = x.Parent) st.Push(x);
        foreach (var x in st) Crumbs.Add(x);
    }

    [RelayCommand] private void Crumb(SpaceNode n) => Open(n);
    [RelayCommand] private void Up() { if (Current?.Parent != null) Open(Current.Parent); }
    [RelayCommand] private void OpenSelected() { if (Selected is { IsDir: true } s) Open(s); }
    [RelayCommand] private void Reveal() { if (Selected != null) MainViewModel.OpenInExplorer(Selected.FullPath); }

    [RelayCommand]
    private async Task Recycle()
    {
        var s = Selected;
        if (s == null || !OperatingSystem.IsWindows()) return;
        if (!await Main.Confirm("Mandar para a Lixeira", $"{s.FullPath}\n{Fmt.Size(s.Size)}", "Mover para a Lixeira", true)) return;
        if (WinShell.Recycle([s.FullPath])) { SpaceAnalyzer.Remove(s); Selected = null; Open(Current!); Main.ShowToast("Movido para a Lixeira."); }
        else Main.ShowToast("Não foi possível mover (arquivo em uso?).");
    }
}

// ───────────────────────── duplicados ─────────────────────────

public sealed partial class DupFileRow(DupFile f, DuplicatesViewModel owner) : ObservableObject
{
    public DupFile F { get; } = f;
    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => owner.Recount();
    public string Path => F.Path;
    public string Date => F.Modified.ToString("dd/MM/yyyy HH:mm");
}

public sealed class DupGroupRow
{
    public required DupGroup G { get; init; }
    public List<DupFileRow> Files { get; } = [];
    public string Header => $"{G.Files.Count} cópias de {System.IO.Path.GetFileName(G.Files[0].Path)} · {Fmt.Size(G.Size)} cada";
    public string Waste => $"desperdício {Fmt.Size(G.Wasted)}";
}

public sealed partial class DuplicatesViewModel : PageViewModel
{
    public override string Title => "Limpador de duplicados";
    public override string IconKey => "Duplicates";
    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<DupGroupRow> Groups { get; } = [];
    public List<string> MinSizes { get; } = ["Qualquer tamanho", "Acima de 100 KB", "Acima de 1 MB", "Acima de 10 MB", "Acima de 100 MB"];
    public List<string> Kinds { get; } = ["Todos os tipos", "Imagens", "Vídeos", "Áudio", "Documentos", "Compactados"];
    public List<string> Rules { get; } = ["Manter o mais antigo", "Manter o mais novo", "Manter o de caminho mais curto"];
    [ObservableProperty] private int _minIndex = 2;
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private int _ruleIndex;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _selection = "";
    [ObservableProperty] private bool _hasSelection;
    private CancellationTokenSource? _cts;

    [RelayCommand] private async Task AddFolder() { var f = await MainViewModel.PickFolder("Pasta para procurar"); if (f != null && !Folders.Contains(f)) Folders.Add(f); }
    [RelayCommand] private void RemoveFolder(string f) => Folders.Remove(f);

    [RelayCommand]
    private async Task Find()
    {
        if (Folders.Count == 0) { Main.ShowToast("Adicione ao menos uma pasta."); return; }
        long min = MinIndex switch { 0 => 1, 1 => 100 << 10, 2 => 1 << 20, 3 => 10 << 20, _ => 100 << 20 };
        FileCategory? cat = KindIndex switch { 1 => FileCategory.Image, 2 => FileCategory.Video, 3 => FileCategory.Audio, 4 => FileCategory.Document, 5 => FileCategory.Archive, _ => null };
        Func<string, bool>? filter = cat == null ? null : p => FileTypes.Categorize(System.IO.Path.GetExtension(p).TrimStart('.')) == cat;
        _cts = new CancellationTokenSource();
        Busy = true; Groups.Clear();
        var prog = new Progress<DupProgress>(p => Status = p.Total > 0 ? $"{p.Phase}: {p.Done:N0}/{p.Total:N0} · {p.Groups:N0} grupos · {Fmt.Size(p.WastedBytes)} repetidos" : $"{p.Phase}: {p.Done:N0}");
        try
        {
            var folders = Folders.ToList();
            var res = await Task.Run(() => DuplicateFinder.Find(folders, min, filter, prog, _cts.Token));
            foreach (var g in res.Take(2000))
            {
                var row = new DupGroupRow { G = g };
                foreach (var f in g.Files) row.Files.Add(new DupFileRow(f, this));
                Groups.Add(row);
            }
            Status = res.Count == 0 ? "Nenhum duplicado encontrado." : $"{res.Count:N0} grupos · {Fmt.Size(res.Sum(g => g.Wasted))} ocupados por cópias";
            ApplyRule();
        }
        catch (OperationCanceledException) { Status = "Cancelado."; }
        finally { Busy = false; }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    partial void OnRuleIndexChanged(int value) => ApplyRule();

    [RelayCommand]
    private void ApplyRule()
    {
        var rule = (DuplicateFinder.KeepRule)RuleIndex;
        foreach (var g in Groups)
        {
            var remove = DuplicateFinder.SelectForRemoval(g.G, rule).ToHashSet();
            foreach (var f in g.Files) f.IsChecked = remove.Contains(f.F);
        }
        Recount();
    }

    public void Recount()
    {
        var sel = Groups.SelectMany(g => g.Files).Where(f => f.IsChecked).ToList();
        HasSelection = sel.Count > 0;
        Selection = sel.Count == 0 ? "Nada marcado" : $"{sel.Count:N0} arquivos marcados · libera {Fmt.Size(sel.Sum(f => f.F.Size))}";
    }

    [RelayCommand]
    private async Task Remove()
    {
        var sel = Groups.SelectMany(g => g.Files).Where(f => f.IsChecked).ToList();
        if (sel.Count == 0) return;
        if (Groups.Any(g => g.Files.All(f => f.IsChecked)))
        {
            await Main.Alert("Grupo inteiro marcado", "Em algum grupo TODAS as cópias estão marcadas — assim não sobraria nenhuma. Desmarque ao menos uma por grupo.", "danger");
            return;
        }
        if (!await Main.Confirm("Mover duplicados para a Lixeira", $"{sel.Count:N0} arquivos ({Fmt.Size(sel.Sum(f => f.F.Size))}) vão para a Lixeira — dá para restaurar de lá.", "Mover para a Lixeira")) return;
        bool ok = OperatingSystem.IsWindows() ? WinShell.Recycle(sel.Select(s => s.F.Path)) : false;
        if (!ok && !OperatingSystem.IsWindows()) foreach (var s in sel) { try { File.Delete(s.F.Path); ok = true; } catch { } }
        Main.ShowToast(ok ? "Duplicados movidos para a Lixeira." : "Alguns arquivos não puderam ser movidos.");
        foreach (var g in Groups.ToList())
        {
            g.Files.RemoveAll(f => f.IsChecked && !File.Exists(f.Path));
            if (g.Files.Count < 2) Groups.Remove(g);
        }
        Recount();
    }
}

// ───────────────────────── mídia inicializável ─────────────────────────

public sealed partial class BootMediaViewModel : PageViewModel
{
    public override string Title => "Mídia inicializável";
    public override string IconKey => "Boot";
    public PickerViewModel Target { get; } = new()
    {
        ShowPartitions = false,
        DiskFilter = d => d.IsSystem ? "Disco do Windows." : d.IsImage ? "Escolha um pendrive." : !d.Removable && d.Bus is not ("USB" or "SD" or "MMC") ? "Por segurança, só pendrives e cartões. (Discos internos ficam de fora.)" : null,
    };
    [ObservableProperty] private string _iso = "";
    [ObservableProperty] private int _mode;
    [ObservableProperty] private string _isoInfo = "";
    [ObservableProperty] private string _label = "INSTALAR";
    public List<string> Modes { get; } = ["Automático (detecta pela ISO)", "Instalação do Windows (FAT32, UEFI + BIOS)", "Gravação direta — modo DD (Linux, ferramentas)"];
    public bool CanWrite => Iso.Length > 0 && Target.Selected != null;
    partial void OnIsoChanged(string value) => OnPropertyChanged(nameof(CanWrite));
    public BootMediaViewModel() { Target.Changed += _ => OnPropertyChanged(nameof(CanWrite)); }
    public override void OnDisksChanged() => Target.Load(Main.Disks);
    public override void OnShown(object? arg) { if (Target.Groups.Count == 0) Target.Load(Main.Disks); }

    [RelayCommand]
    private async Task PickIso()
    {
        var f = await MainViewModel.PickFile("Imagem ISO", ("Imagens ISO e IMG", ["*.iso", "*.img"]));
        if (f == null) return;
        Iso = f;
        bool hybrid = false;
        try { hybrid = BootMedia.IsHybrid(f); } catch { }
        IsoInfo = $"{Fmt.Size(new FileInfo(f).Length)} · " + (hybrid ? "imagem híbrida (inicializável por gravação direta)" : "ISO tradicional (provavelmente de instalação do Windows)");
    }

    [RelayCommand]
    private async Task Write()
    {
        var d = Target.Selected?.Disk; if (d == null) return;
        if (!await Main.RequirePremium("Mídia inicializável", "Criar pendrive de instalação do Windows ou gravar ISO é do Premium.")) return;
        bool hybrid = false;
        try { hybrid = BootMedia.IsHybrid(Iso); } catch { }
        int mode = Mode == 0 ? (hybrid ? 2 : 1) : Mode;
        if (mode == 1 && !OperatingSystem.IsWindows()) { await Main.Alert("Só no Windows", "Montar a ISO do Windows e dividir o install.wim exigem o Windows."); return; }
        string iso = Iso, label = Label;
        Main.Queue("Criar mídia inicializável", $"{Path.GetFileName(iso)} → {d.Title} ({d.Model}, {Fmt.Size(d.Size)}), modo {(mode == 1 ? "instalação do Windows" : "gravação direta (DD)")} — apaga o pendrive", "Boot", s =>
        {
            var dq = MainViewModel.Fresh(d);
            return mode == 1 ? BootMedia.WriteWindows(iso, dq, label, s) : Task.Run(() => BootMedia.WriteRaw(iso, dq, s));
        }, d, dangerous: true);
    }
}

public static class WinShell
{
    public static bool Recycle(IEnumerable<string> paths)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return Core.Platform.Windows.Shell.Recycle(paths);
    }
}
