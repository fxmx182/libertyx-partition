using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Platform;

namespace LibertyX.Partition.ViewModels;

public sealed partial class NavItem(string key, string title, string icon, bool premium = false) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public Avalonia.Media.Geometry? Icon { get; } = Ui.Icon(icon);
    /// <summary>A ação principal da tela é do Premium (a tela abre no Básico, para a pessoa ver o que faz).</summary>
    public bool Premium { get; } = premium;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _locked;
    public IRelayCommand Go => new RelayCommand(() => MainViewModel.Instance.Navigate(Key));
}

public sealed class NavGroup(string title, params NavItem[] items)
{
    public string Title { get; } = title.ToUpperInvariant();
    public List<NavItem> Items { get; } = [.. items];
}

public sealed partial class MainViewModel : ViewModelBase
{
    public static MainViewModel Instance { get; private set; } = null!;

    public List<NavGroup> Nav { get; }
    private readonly Dictionary<string, Func<PageViewModel>> _factories;
    private readonly Dictionary<string, PageViewModel> _pages = [];

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private OperationViewModel? _operation;
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _loadingText = "Lendo discos…";

    public List<DiskInfo> Disks { get; private set; } = [];
    public bool IsAdmin { get; } = DiskSystem.Native.IsElevated;
    public bool IsWindows { get; } = OperatingSystem.IsWindows();
    /// <summary>Versão + commit do build (o CI carimba): sem isso não dá para saber se o .exe aberto é o novo.</summary>
    public string Version
    {
        get
        {
            var info = typeof(MainViewModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "1.0.0";
            var parts = info.Split('+');
            return "v" + parts[0] + (parts.Length > 1 && parts[1].Length >= 7 ? " · " + parts[1][..7] : "");
        }
    }
    public string AdminText => IsAdmin ? (OperatingSystem.IsWindows() ? "Administrador" : "Administrador (root)") : "Sem privilégio de administrador";
    /// <summary>Linux sem root: só imagens anexadas e leitura do que der — com o botão para pedir a senha.</summary>
    public bool CanElevate => !IsAdmin && OperatingSystem.IsLinux();

    [RelayCommand]
    private async Task Elevate()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (HasPending) { await Alert("Alterações pendentes", "Confirme ou cancele as alterações da fila antes."); return; }
        ShowToast("Digite a senha de administrador na janela do sistema…");
        var args = Environment.GetCommandLineArgs().Skip(1).Concat(DiskSystem.AttachedImages).Distinct().ToArray();
        bool ok = await Task.Run(() => Core.Platform.Linux.LinuxSession.RelaunchAsRoot(args));
        if (ok) { if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l) l.Shutdown(); }
        else await Alert("Sem permissão de administrador", "A senha foi cancelada ou o sistema não tem o pkexec (polkit). Também dá para abrir pelo terminal:\n\nsudo libertyx-partition", "warn");
    }

    // ───────── licença ─────────

    public LibertyX.Partition.Core.Licensing.LicenseManager License { get; } = OpenLicense();
    [ObservableProperty] private bool _isPremium;
    public string EditionText => IsPremium ? "PREMIUM" : "BÁSICO";

    private static LibertyX.Partition.Core.Licensing.LicenseManager OpenLicense()
    {
        try { return new(); }
        catch { return new(Path.Combine(Path.GetTempPath(), "libertyx-partition")); }
    }

    /// <summary>A licença mudou (ativou, desativou, gastou cota): atualiza selo, cadeados e a tela do Premium.</summary>
    public void LicenseChanged()
    {
        IsPremium = License.IsPremium;
        OnPropertyChanged(nameof(EditionText));
        foreach (var g in Nav) foreach (var i in g.Items) i.Locked = i.Premium && !IsPremium;
        if (_pages.TryGetValue("premium", out var p) && p is PremiumViewModel pv) pv.Refresh();
    }

    // ───────── atualização ─────────

    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateText = "";
    private Updater.Info? _update;

    private bool _askedUpdate;

    /// <summary>Confere o release publicado. Na abertura do app, se houver versão nova, pergunta na hora.</summary>
    private async Task CheckUpdate(bool ask)
    {
        var i = await Updater.Check();
        if (i == null || !Updater.IsNewer(i)) return;
        _update = i;
        UpdateText = $"Versão {i.Version} disponível";
        UpdateAvailable = true;
        if (!ask || _askedUpdate) return;
        _askedUpdate = true;
        // espera a tela assentar e não atropela um diálogo ou uma operação em andamento
        for (int k = 0; k < 60 && (Dialog != null || Operation != null || Loading); k++) await Task.Delay(1000);
        if (Dialog != null || Operation != null) return;
        var f = new FormDialogViewModel
        {
            Title = "Atualização disponível", IconKey = "Download", ConfirmText = "Atualizar agora", CancelText = "Depois",
            Subtitle = $"Versão {i.Version} · você está na 1.0.{Updater.CurrentBuild}",
        };
        if (i.Notes.Length > 0) f.Note("O que mudou: " + i.Notes, "gold");
        f.Note(Updater.IsInstalled
            ? "O app baixa a versão nova, atualiza por cima e abre de novo sozinho. Licença, backups e configurações continuam."
            : OperatingSystem.IsLinux() ? "Esta é a versão em pasta (tar.gz): o navegador baixa a nova para você substituir esta." : "Esta é a versão portátil: o navegador baixa o .exe novo para você substituir este.", "");
        if (await ShowDialog(f)) await UpdateNow();
    }

    [RelayCommand]
    private async Task UpdateNow()
    {
        var i = _update; if (i == null) return;
        if (!Updater.IsInstalled)
        {
            // versão portátil: não há o que atualizar no lugar — baixa o .exe novo pelo navegador
            string url = i.PortableUrl.Length > 0 ? i.PortableUrl : $"https://github.com/{Updater.Repo}/releases/{Updater.Tag}";
            if (OperatingSystem.IsLinux()) Core.Platform.Linux.LinuxSession.OpenAsUser(url);
            else try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            return;
        }
        if (HasPending) { await Alert("Alterações pendentes", "Confirme ou cancele as alterações da fila antes de atualizar."); return; }
        string? setup = null;
        bool ok = await Run($"Baixando a versão {i.Version}", async s =>
        {
            setup = await Updater.Download(i, new Progress<double>(x => s.Progress(x, "Baixando o instalador")), s.Token);
            if (OperatingSystem.IsLinux())
            {
                s.Progress(-1, "Instalando");
                await Updater.InstallLinux(setup, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); }, s.Token);
                s.Log("Instalado. O app vai abrir de novo na versão nova.");
            }
            else s.Log("Download conferido. O instalador vai fechar o app, atualizar e abrir de novo.");
        }, refresh: false, success: "Pronto para atualizar");
        if (!ok || setup == null) return;
        Operation = null;
        Updater.LaunchSetup(setup);
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l) l.Shutdown();
    }

    /// <summary>Barra a ação no Básico com uma explicação e o caminho para o Premium.</summary>
    public async Task<bool> RequirePremium(string feature, string why)
    {
        if (License.IsPremium) return true;
        if (await Confirm($"{feature} é Premium", why + "\n\nO Premium é vitalício: paga uma vez, usa para sempre, com as atualizações.", "Conhecer o Premium", icon: "Crown"))
            Navigate("premium");
        return false;
    }

    public MainViewModel()
    {
        Instance = this;
        HookPending();
        Nav =
        [
            new("Painel", new NavItem("discos", "Discos e partições", "Dashboard"), new NavItem("premium", "LibertyX Premium", "Crown")),
            new("Resgate",
                new NavItem("recuperar", "Recuperar dados", "RecoverFiles"),
                new NavItem("particoes", "Recuperar partição", "RecoverPartition"),
                new NavItem("imagem", "Backup e imagem", "Backup"),
                new NavItem("superficie", "Teste de superfície", "Surface"),
                new NavItem("saude", "Saúde e propriedades", "Health")),
            new("Disco",
                new NavItem("migrar", "Migrar sistema para SSD", "Migrate", premium: true),
                new NavItem("copiar-disco", "Copiar disco", "CopyDisk", premium: true),
                new NavItem("copiar-particao", "Copiar partição", "CopyPartition"),
                new NavItem("converter", "Converter GPT ⇄ MBR", "Convert"),
                new NavItem("alinhar", "Alinhar partições", "Align"),
                new NavItem("limpar", "Limpar disco", "Wipe"),
                new NavItem("excluir-todas", "Excluir todas as partições", "DeleteAll")),
            new("Utilidades",
                OperatingSystem.IsWindows() ? new NavItem("bitlocker", "BitLocker", "BitLocker") : new NavItem("bitlocker", "Criptografia (LUKS, BitLocker)", "BitLocker"),
                new NavItem("benchmark", "Benchmark de disco", "Benchmark"),
                new NavItem("espaco", "Analisador de espaço", "Space"),
                new NavItem("duplicados", "Limpador de duplicados", "Duplicates"),
                new NavItem("boot", "Pendrive de boot e imagens", "Boot", premium: true)),
        ];
        if (OperatingSystem.IsLinux())
            Nav.Add(new("Linux",
                new NavItem("lvm", "LVM e RAID", "Layers"),
                new NavItem("ferramentas", "Ferramentas do sistema", "Terminal")));
        _factories = new()
        {
            ["discos"] = () => new DisksPageViewModel(),
            ["recuperar"] = () => new RecoveryViewModel(),
            ["particoes"] = () => new PartitionRecoveryViewModel(),
            ["imagem"] = () => new BackupViewModel(),
            ["superficie"] = () => new SurfaceViewModel(),
            ["saude"] = () => new HealthViewModel(),
            ["migrar"] = () => new CloneViewModel(migrate: true),
            ["copiar-disco"] = () => new CloneViewModel(migrate: false),
            ["copiar-particao"] = () => new CopyPartitionViewModel(),
            ["converter"] = () => new ConvertViewModel(),
            ["alinhar"] = () => new AlignViewModel(),
            ["limpar"] = () => new WipeViewModel(),
            ["excluir-todas"] = () => new DeleteAllViewModel(),
            ["bitlocker"] = () => OperatingSystem.IsWindows() ? new BitLockerViewModel() : new LinuxCryptoViewModel(),
            ["lvm"] = () => new LinuxVolumesViewModel(),
            ["ferramentas"] = () => new LinuxToolsViewModel(),
            ["benchmark"] = () => new BenchmarkViewModel(),
            ["espaco"] = () => new SpaceViewModel(),
            ["duplicados"] = () => new DuplicatesViewModel(),
            ["boot"] = () => new BootMediaViewModel(),
            ["premium"] = () => new PremiumViewModel(),
        };
        LicenseChanged();
    }

    public void Start(string[] args)
    {
        foreach (var a in args.Where(File.Exists)) DiskSystem.AttachedImages.Add(Path.GetFullPath(a));
        Navigate("discos");
        _ = Refresh();
        _ = CheckUpdate(ask: true);
        var ut = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        ut.Tick += async (_, _) => await CheckUpdate(ask: false);
        ut.Start();
        // chave da loja: reconfere de vez em quando, sem atrapalhar (sem internet, nada muda)
        _ = Task.Run(async () => { try { if (await License.RevalidateIfDue()) Ui.Post(() => { LicenseChanged(); ShowToast("A licença da loja não vale mais (reembolso ou cancelamento) — voltou ao Básico."); }); } catch { } });
        StartHotplug();
    }

    // ───────── plug and play ─────────

    private string? _signature;
    private bool _checking;

    /// <summary>A cada 2 s compara a impressão digital dos discos; se mudou, relê e avisa o que entrou/saiu.</summary>
    private void StartHotplug()
    {
        _ = Task.Run(() => _signature = SafeSignature());
        var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += async (_, _) =>
        {
            if (_checking || Loading || Operation != null) return;
            _checking = true;
            try
            {
                var sig = await Task.Run(SafeSignature);
                if (sig == null || sig == _signature) return;
                bool first = _signature == null;
                _signature = sig;
                if (first) return;
                var before = Disks.ToDictionary(d => (d.Number, d.IsImage), d => d);
                await Refresh();
                var after = Disks.ToDictionary(d => (d.Number, d.IsImage), d => d);
                var added = after.Where(kv => !before.ContainsKey(kv.Key) || before[kv.Key].Size != kv.Value.Size).Select(kv => kv.Value).ToList();
                var removed = before.Where(kv => !after.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                if (added.Count > 0) ShowToast($"Conectado: {string.Join(", ", added.Select(d => $"{d.Model} ({Fmt.Size(d.Size)})"))}");
                else if (removed.Count > 0) ShowToast($"Removido: {string.Join(", ", removed.Select(d => d.Model))}" +
                    (Pending.Any(o => removed.Any(r => r.Number == o.DiskNumber)) ? " — havia alterações pendentes para ele" : ""));
                else ShowToast("Volumes mudaram — lista atualizada.");
            }
            finally { _checking = false; }
        };
        t.Start();
    }

    // com limite: um leitor travado não pode congelar o plug and play
    private static string? SafeSignature() { try { return Bounded.Run(DiskSystem.Signature, TimeSpan.FromSeconds(6), null, out _); } catch { return null; } }

    public void Navigate(string key, object? arg = null)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = _factories[key]();
            _pages[key] = page;
        }
        foreach (var g in Nav) foreach (var i in g.Items) i.IsActive = i.Key == key;
        CurrentPage = page;
        page.OnShown(arg);
    }

    public T Page<T>(string key) where T : PageViewModel
    {
        if (!_pages.TryGetValue(key, out var p)) { p = _factories[key](); _pages[key] = p; }
        return (T)p;
    }

    [RelayCommand]
    public async Task Refresh()
    {
        Loading = true;
        LoadingText = "Lendo discos…";
        try
        {
            Disks = await Task.Run(DiskSystem.Enumerate);
            foreach (var p in _pages.Values) p.OnDisksChanged();
            // partes lentas depois: S.M.A.R.T. e uso de partições sem letra
            _ = Task.Run(() =>
            {
                foreach (var d in Disks.ToList())
                {
                    try { if (!d.IsImage) d.Smart = DiskSystem.Native.ReadSmart(d); } catch { }
                    foreach (var p in d.Partitions.Where(p => p.Used == null && !p.IsUnallocated && Core.FileSystems.FsDetect.CanRead(p.FileSystem) && !p.Disk.IsImage))
                    {
                        try
                        {
                            using var v = DiskSystem.OpenPartition(p);
                            var u = UsageCalculator.Compute(v, p.FileSystem);
                            if (u != null) { p.Used = u.Value.used; p.Free = u.Value.free; p.Label ??= u.Value.label; }
                        }
                        catch { }
                    }
                }
                Ui.Post(() => { foreach (var p in _pages.Values) p.OnDisksChanged(); });
            });
        }
        catch (Exception ex) { ShowToast("Erro ao ler discos: " + ex.Message); }
        finally { Loading = false; }
    }

    // ───────── diálogos ─────────

    public async Task<bool> ShowDialog(DialogViewModel d)
    {
        Dialog = d;
        try { return await d.Result; }
        finally { if (Dialog == d) Dialog = null; }
    }

    public Task<bool> Confirm(string title, string text, string confirm = "Continuar", bool danger = false, string? phrase = null, string icon = "Alert")
    {
        var f = new FormDialogViewModel { Title = title, ConfirmText = confirm, Danger = danger, ConfirmPhrase = phrase, IconKey = icon };
        f.Note(text, danger ? "danger" : "");
        return ShowDialog(f);
    }

    public Task Alert(string title, string text, string kind = "") 
    {
        var f = new FormDialogViewModel { Title = title, ConfirmText = "Entendi", IconKey = kind == "danger" ? "Alert" : "Info" };
        f.Note(text, kind);
        return ShowDialog(f);
    }

    // ───────── operações ─────────

    /// <summary>Roda uma operação longa com a janela de progresso. Relê os discos no fim se pedido.</summary>
    public async Task<bool> Run(string title, Func<OpSink, Task> work, bool refresh = true, bool canPause = false, string? success = null)
    {
        var op = new OperationViewModel { Title = title, CanPause = canPause };
        Operation = op;
        bool ok;
        try
        {
            await Task.Run(() => work(op.Sink));
            op.Finish(true, success);
            ok = true;
        }
        catch (OperationCanceledException) { op.Finish(false, "Cancelado pelo usuário."); ok = false; }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            op.Finish(false, ex.Message);
            ok = false;
        }
        if (refresh) await Refresh();
        return ok;
    }

    public Task<bool> Run(string title, Action<OpSink> work, bool refresh = true, bool canPause = false, string? success = null)
        => Run(title, s => { work(s); return Task.CompletedTask; }, refresh, canPause, success);

    public void ShowToast(string text)
    {
        Toast = text;
        var mine = text;
        _ = Task.Delay(4500).ContinueWith(_ => Ui.Post(() => { if (Toast == mine) Toast = null; }));
    }

    // ───────── arquivos ─────────

    public static TopLevel? Top => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static async Task<string?> PickFolder(string title)
    {
        if (Top == null) return null;
        var r = await Top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> PickFile(string title, params (string name, string[] patterns)[] types)
    {
        if (Top == null) return null;
        var r = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = types.Select(t => new FilePickerFileType(t.name) { Patterns = t.patterns }).Append(FilePickerFileTypes.All).ToList(),
        });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> SaveFile(string title, string suggested, string ext, string typeName)
    {
        if (Top == null) return null;
        var r = await Top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = suggested, DefaultExtension = ext,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [$"*.{ext}"] }],
        });
        return r?.TryGetLocalPath();
    }

    [RelayCommand]
    public async Task OpenImage()
    {
        var f = await PickFile("Abrir imagem de disco", ("Imagens de disco", ["*.img", "*.dd", "*.raw", "*.bin", "*.iso", "*.001"]));
        if (f == null) return;
        if (!DiskSystem.AttachedImages.Contains(f)) DiskSystem.AttachedImages.Add(f);
        await Refresh();
        ShowToast("Imagem anexada como disco (somente leitura).");
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Core.Platform.Linux.LinuxSession.OpenAsUser(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", Directory.Exists(path) ? path : Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch { }
    }
}
