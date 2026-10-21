using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Linux;

namespace LibertyX.Partition.ViewModels;

// ───────────────────────── criptografia (Linux) ─────────────────────────

public sealed partial class CryptoRow(LinuxCrypto.Volume v, LinuxCryptoViewModel owner) : ObservableObject
{
    public LinuxCrypto.Volume V { get; } = v;
    public string Title => (V.Part.KernelName ?? V.Device) + (string.IsNullOrEmpty(V.Label) ? "" : $" · {V.Label}");
    public string Kind => V.Version ?? V.Kind;
    public bool IsLuks => V.Kind == "LUKS";
    public bool Open => V.IsOpen;
    public bool Closed => !V.IsOpen;
    public bool Mounted => V.MountedAt.Count > 0;
    public bool OpenNotMounted => V.IsOpen && !Mounted;
    public string StateText => V.IsOpen ? "Aberto" : "Trancado";
    public string Detail => $"{Fmt.Size(V.Part.Length)} · {V.Part.Disk.Model} ({V.Part.Disk.Title})" +
        (V.IsOpen ? $"\nAberto como /dev/mapper/{V.MapperName}" + (Mounted ? $" · montado em {string.Join(", ", V.MountedAt)}" : " · não montado") : "") +
        (V.Uuid != null ? $"\nUUID {V.Uuid}" : "");
    public IAsyncRelayCommand UnlockCmd => new AsyncRelayCommand(() => owner.Unlock(this));
    public IAsyncRelayCommand CloseCmd => new AsyncRelayCommand(() => owner.Close(this));
    public IAsyncRelayCommand MountCmd => new AsyncRelayCommand(() => owner.MountInner(this));
    public IRelayCommand BrowseCmd => new RelayCommand(() => { if (Mounted) LinuxSession.OpenAsUser(V.MountedAt[0]); });
    public IAsyncRelayCommand KeysCmd => new AsyncRelayCommand(() => owner.Keys(this));
    public IAsyncRelayCommand BackupCmd => new AsyncRelayCommand(() => owner.BackupHeader(this));
    public IAsyncRelayCommand RestoreCmd => new AsyncRelayCommand(() => owner.RestoreHeader(this));
    public IAsyncRelayCommand InfoCmd => new AsyncRelayCommand(() => owner.Info(this));
    public IRelayCommand RecoverCmd => new RelayCommand(() => owner.Recover(this));
}

public sealed partial class LinuxCryptoViewModel : PageViewModel
{
    public override string Title => "Criptografia";
    public override string IconKey => "BitLocker";
    public ObservableCollection<CryptoRow> Volumes { get; } = [];
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    public override void OnShown(object? arg) => Load();
    public override void OnDisksChanged() => Load();

    [RelayCommand]
    private void Load()
    {
        Volumes.Clear();
        foreach (var v in LinuxCrypto.List(Main.Disks)) Volumes.Add(new CryptoRow(v, this));
        Message = !LinuxTools.Has("cryptsetup") ? LinuxTools.Missing("cryptsetup")!
            : Volumes.Count == 0 ? "Nenhum volume LUKS ou BitLocker nos discos conectados. Para criar um, use Formatar ou Criar partição → “ext4 criptografado (LUKS2)”." : "";
    }

    [RelayCommand]
    private async Task Refresh() { await Main.Refresh(); Load(); }

    public async Task Unlock(CryptoRow r)
    {
        var f = new FormDialogViewModel { Title = $"Desbloquear {r.Title}", Subtitle = r.Kind, IconKey = "Unlock", ConfirmText = "Desbloquear" };
        var mode = f.Add(new ChoiceField { Label = "Com" });
        mode.Options.Add("Senha");
        mode.Options.Add(r.IsLuks ? "Arquivo-chave" : "Chave de recuperação (48 dígitos)");
        var pw = f.Add(new TextField { Label = "Senha", Password = true });
        var key = f.Add(new TextField { Label = "Chave de recuperação", Watermark = "000000-000000-000000-000000-000000-000000-000000-000000", IsVisible = false });
        var file = f.Add(new PathField { Label = "Arquivo-chave", Folder = false, PickerTitle = "Arquivo-chave", IsVisible = false });
        mode.Changed += i => { pw.IsVisible = i == 0; key.IsVisible = i == 1 && !r.IsLuks; file.IsVisible = i == 1 && r.IsLuks; };
        var ro = f.Add(new ToggleField { Label = "Só leitura", Hint = "Para resgatar arquivos sem arriscar mudar nada no volume." });
        var mount = f.Add(new ToggleField { Label = "Montar depois de abrir", Value = true });
        f.Note("Aberto, o conteúdo decifrado aparece como volume próprio na lista de discos — a Recuperação de dados lê ali dentro, inclusive arquivos apagados.", "gold");
        if (!await Main.ShowDialog(f)) return;
        string? secret = mode.SelectedIndex == 0 ? pw.Value : r.IsLuks ? null : key.Value.Trim();
        string? keyFile = mode.SelectedIndex == 1 && r.IsLuks ? file.Value : null;
        string? mp = null;
        bool ok = await Main.Run($"Desbloqueando {r.Title}", async s =>
        {
            string dev = await LinuxCrypto.Open(r.V, secret, keyFile, ro.Value, s);
            s.Log($"Aberto em {dev}.");
            if (mount.Value) mp = await MountMapped(dev, r.V.Label, ro.Value, s);
        }, success: "Desbloqueado");
        if (ok) { Load(); if (mp != null) Main.ShowToast($"Montado em {mp}"); }
    }

    private static async Task<string?> MountMapped(string dev, string? label, bool readOnly, OpSink s)
    {
        // dentro do LUKS pode haver um sistema de arquivos ou um grupo LVM
        using (var v = new LinuxDevice(dev, false))
        {
            var k = Core.FileSystems.FsDetect.Detect(v);
            if (k == Core.FileSystems.FsKind.LvmPv)
            {
                s.Log("Dentro há um grupo LVM: ativando os volumes lógicos (eles aparecem na lista de discos).");
                if (LinuxTools.Has("lvm")) await Proc.Run("lvm", ["pvscan", "--cache", "-aay", dev], s.Token);
                return null;
            }
            if (Core.FileSystems.FsDetect.IsContainer(k) || k is Core.FileSystems.FsKind.Unknown or Core.FileSystems.FsKind.Empty or Core.FileSystems.FsKind.LinuxSwap) return null;
        }
        var fake = new PartitionInfo { Disk = new DiskInfo { DevicePath = dev }, IsWholeDisk = true, Offset = 0, Length = 0, Label = label, DeviceNode = dev };
        return await LinuxFs.Mount(fake, null, readOnly, s);
    }

    public async Task MountInner(CryptoRow r)
    {
        string? mp = null;
        if (await Main.Run("Montando", async s => { mp = await MountMapped("/dev/mapper/" + r.V.MapperName, r.V.Label, false, s); }, success: "Montado"))
        { Load(); if (mp != null) Main.ShowToast($"Montado em {mp}"); }
    }

    public async Task Close(CryptoRow r)
    {
        if (await Main.Run($"Trancando {r.Title}", s => LinuxCrypto.Close(r.V, s), success: "Trancado")) Load();
    }

    public async Task Keys(CryptoRow r)
    {
        if (!r.IsLuks) { await Main.Alert("BitLocker", "As senhas do BitLocker são gerenciadas pelo Windows (no LibertyX Partition para Windows: Utilidades → BitLocker). Aqui ele é aberto para ler e recuperar."); return; }
        var f = new FormDialogViewModel { Title = $"Senhas de {r.Title}", IconKey = "Key", ConfirmText = "Aplicar" };
        var act = f.Add(new ChoiceField { Label = "O que fazer" });
        act.Options.Add("Adicionar outra senha (a atual continua valendo)");
        act.Options.Add("Trocar uma senha");
        act.Options.Add("Remover uma senha");
        var cur = f.Add(new TextField { Label = "Senha atual", Password = true });
        var nw = f.Add(new TextField { Label = "Senha nova", Password = true });
        var nw2 = f.Add(new TextField { Label = "Repita a senha nova", Password = true });
        act.Changed += i => { nw.IsVisible = nw2.IsVisible = i != 2; cur.Label = i == 2 ? "Senha a remover" : "Senha atual"; };
        f.Note("O LUKS guarda até 32 senhas (slots). Remover a última deixa o volume inacessível para sempre — o app não deixa.", "warn");
        f.Validate = () => cur.Value.Length == 0 ? "Digite a senha." : act.SelectedIndex != 2 && (nw.Value.Length < 8 || nw.Value != nw2.Value) ? (nw.Value.Length < 8 ? "Senha nova de pelo menos 8 caracteres." : "As senhas novas não conferem.") : null;
        if (!await Main.ShowDialog(f)) return;
        if (act.SelectedIndex == 2)
        {
            var (_, slots) = await LinuxCrypto.Dump(r.V);
            if (slots.Count <= 1) { await Main.Alert("Última senha", "Esta é a única senha do volume. Adicione outra antes de remover esta.", "danger"); return; }
        }
        await Main.Run("Alterando as senhas", s => act.SelectedIndex switch
        {
            0 => LinuxCrypto.AddKey(r.V, cur.Value, nw.Value, s),
            1 => LinuxCrypto.ChangeKey(r.V, cur.Value, nw.Value, s),
            _ => LinuxCrypto.RemoveKey(r.V, cur.Value, s),
        }, refresh: false, success: "Feito");
    }

    public async Task BackupHeader(CryptoRow r)
    {
        string folder = AppPaths.EnsureDocuments("Cabeçalhos LUKS");
        string? file = null;
        if (await Main.Run("Salvando o cabeçalho", async s => { file = await LinuxCrypto.BackupHeader(r.V, folder, s); s.Log($"Salvo em {file}"); s.Log("Guarde uma cópia fora deste disco. Quem tem este arquivo e a senha abre o volume — trate como a própria senha."); }, refresh: false, success: "Cabeçalho salvo") && file != null)
            MainViewModel.OpenInExplorer(file);
    }

    public async Task RestoreHeader(CryptoRow r)
    {
        var file = await MainViewModel.PickFile("Cópia do cabeçalho LUKS", ("Cabeçalho LUKS", ["*.img", "*.bin", "*"]));
        if (file == null) return;
        if (!await Main.Confirm("Restaurar cabeçalho", $"O cabeçalho de {r.Title} será substituído pela cópia. As senhas voltam a ser as que valiam quando a cópia foi feita.\n\nUse só se o cabeçalho atual estiver danificado (o volume não abre com a senha certa).", "Restaurar", true, phrase: "RESTAURAR")) return;
        await Main.Run("Restaurando o cabeçalho", s => LinuxCrypto.RestoreHeader(r.V, file, s), success: "Cabeçalho restaurado");
    }

    public async Task Info(CryptoRow r)
    {
        var (summary, slots) = await LinuxCrypto.Dump(r.V);
        var f = new FormDialogViewModel { Title = r.Title, Subtitle = r.Kind, IconKey = "Info", ConfirmText = "Fechar" };
        foreach (var line in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(r.IsLuks ? 1 : 40)) f.Note(line.TrimEnd(), "");
        foreach (var k in slots) f.Add(new InfoField { Label = $"Slot {k.Id}", Text = $"{k.Type}{(k.Kdf != null ? " · " + k.Kdf : "")}" });
        await Main.ShowDialog(f);
    }

    public void Recover(CryptoRow r)
    {
        if (!r.V.IsOpen) { Main.ShowToast("Desbloqueie primeiro: a recuperação lê o conteúdo decifrado."); return; }
        var mapped = Main.Disks.FirstOrDefault(d => d.IsVirtual && d.Model == r.V.MapperName);
        if (mapped != null) Main.Navigate("recuperar", mapped);
        else Main.Navigate("recuperar");
    }
}

// ───────────────────────── LVM e RAID ─────────────────────────

public sealed record VgRow(LinuxVolumes.Vg Vg, LinuxVolumesViewModel Owner)
{
    public string Title => Vg.Name;
    public string Detail => $"{Fmt.Size(Vg.Size)} · {Fmt.Size(Vg.Free)} livres · {Vg.Pvs} disco(s) físico(s) · {Vg.Lvs} volume(s)";
    public IAsyncRelayCommand On => new AsyncRelayCommand(() => Owner.SetActive(Vg.Name, true));
    public IAsyncRelayCommand Off => new AsyncRelayCommand(() => Owner.SetActive(Vg.Name, false));
}

public sealed record LvRow(LinuxVolumes.Lv Lv)
{
    public string Title => $"{Lv.Vg} / {Lv.Name}";
    public string Detail => $"{Fmt.Size(Lv.Size)} · {(Lv.Active ? "ativo" : "inativo")} · {Lv.Path}";
    public bool Active => Lv.Active;
}

public sealed record MdRow(LinuxVolumes.Md Md, LinuxVolumesViewModel Owner)
{
    public string Title => $"{Md.Name} · {Md.Level.ToUpperInvariant()}";
    public string Detail => $"{Md.State}\n{Md.Devices}" + (Md.Sync.Length > 0 ? $"\n{Md.Sync}" : "");
    public bool Degraded => Md.State.Contains('_');
    public IAsyncRelayCommand Stop => new AsyncRelayCommand(() => Owner.StopRaid(Md.Name));
}

public sealed partial class LinuxVolumesViewModel : PageViewModel
{
    public override string Title => "LVM e RAID";
    public override string IconKey => "Layers";
    public ObservableCollection<VgRow> Groups { get; } = [];
    public ObservableCollection<LvRow> Logical { get; } = [];
    public ObservableCollection<MdRow> Arrays { get; } = [];
    [ObservableProperty] private string _message = "";

    public override void OnShown(object? arg) => _ = Load();

    [RelayCommand]
    private async Task Load()
    {
        var (vgs, lvs, err) = await LinuxVolumes.Lvm();
        Groups.Clear(); Logical.Clear(); Arrays.Clear();
        foreach (var v in vgs) Groups.Add(new VgRow(v, this));
        foreach (var l in lvs) Logical.Add(new LvRow(l));
        foreach (var m in LinuxVolumes.Raid()) Arrays.Add(new MdRow(m, this));
        Message = err ?? (vgs.Count == 0 && Arrays.Count == 0 ? "Nenhum grupo LVM nem RAID por software encontrado. Volumes ativos aparecem como discos na tela inicial, prontos para recuperar, verificar e formatar." : "");
    }

    public async Task SetActive(string vg, bool on)
    {
        if (!on && !await Main.Confirm("Desativar grupo", $"Os volumes de {vg} somem do sistema até serem ativados de novo. Volumes montados precisam ser desmontados antes.", "Desativar")) return;
        var r = await LinuxVolumes.SetVgActive(vg, on);
        Main.ShowToast(r.Ok ? (on ? $"{vg} ativado." : $"{vg} desativado.") : r.Message);
        await Main.Refresh();
        await Load();
    }

    [RelayCommand]
    private async Task Scan()
    {
        if (!LinuxTools.Has("lvm") && !LinuxTools.Has("mdadm")) { await Main.Alert("Faltam ferramentas", LinuxTools.Missing("lvm", "mdadm")!); return; }
        await Main.Run("Procurando grupos e conjuntos", async s =>
        {
            if (LinuxTools.Has("lvm")) { var a = await LinuxVolumes.ScanAll(s.Token); s.Log(a.Message); var b = await Proc.Run("lvm", ["vgchange", "-ay"], s.Token); s.Log(b.Message); }
            if (LinuxTools.Has("mdadm")) { var c = await LinuxVolumes.AssembleRaid(s.Token); s.Log(c.Message.Length > 0 ? c.Message : "Nenhum RAID novo para montar."); }
        }, success: "Pronto");
        await Load();
    }

    public async Task StopRaid(string md)
    {
        if (!await Main.Confirm("Parar RAID", $"/dev/{md} será parado (os discos continuam intactos e podem ser montados de novo com Procurar).", "Parar")) return;
        var r = await LinuxVolumes.StopRaid(md);
        Main.ShowToast(r.Ok ? $"{md} parado." : r.Message);
        await Main.Refresh();
        await Load();
    }
}

// ───────────────────────── ferramentas do sistema ─────────────────────────

public sealed record ToolRow(LinuxTools.Tool T)
{
    public string Exe => T.Exe;
    public string Purpose => T.Purpose;
    public string Package => LinuxTools.PackageOf(T.Exe);
    public bool Installed => LinuxTools.Has(T.Exe);
    public bool Missing => !Installed;
    public string Where => LinuxTools.Find(T.Exe) ?? $"pacote {Package}";
}

public sealed record ToolGroup(string Title, List<ToolRow> Items)
{
    public string Summary => Items.All(i => i.Installed) ? "tudo instalado" : $"{Items.Count(i => i.Missing)} faltando";
    public bool Complete => Items.All(i => i.Installed);
}

public sealed partial class LinuxToolsViewModel : PageViewModel
{
    public override string Title => "Ferramentas do sistema";
    public override string IconKey => "Terminal";
    public ObservableCollection<ToolGroup> Groups { get; } = [];
    [ObservableProperty] private string _distro = "";
    [ObservableProperty] private string _installLine = "";
    [ObservableProperty] private bool _anyMissing;

    public override void OnShown(object? arg) => Load();

    [RelayCommand]
    private void Load()
    {
        LinuxTools.Forget();
        Groups.Clear();
        foreach (var g in LinuxTools.Catalog.GroupBy(t => t.Group)) Groups.Add(new ToolGroup(g.Key, g.Select(t => new ToolRow(t)).ToList()));
        var miss = LinuxTools.Catalog.Where(t => !LinuxTools.Has(t.Exe)).Select(t => t.Exe).ToList();
        AnyMissing = miss.Count > 0;
        Distro = $"{LinuxTools.Current.Name} · família {LinuxTools.Current.Family} · kernel {Environment.OSVersion.Version}";
        InstallLine = AnyMissing ? LinuxTools.InstallHint(miss) : "Todas as ferramentas estão instaladas.";
    }

    [RelayCommand]
    private async Task InstallMissing()
    {
        var miss = LinuxTools.Catalog.Where(t => !LinuxTools.Has(t.Exe)).Select(t => t.Exe).ToList();
        if (miss.Count == 0) return;
        if (!await Main.Confirm("Instalar ferramentas", $"Serão instalados pelo gerenciador de pacotes da distribuição:\n\n{string.Join(", ", miss.Select(LinuxTools.PackageOf).Distinct())}\n\nPrecisa de internet.", "Instalar")) return;
        await Main.Run("Instalando ferramentas", async s =>
        {
            var r = await LinuxTools.Install(miss, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
            if (!r.Ok) throw new InvalidOperationException("A instalação não terminou: " + r.Message.Split('\n').LastOrDefault());
        }, refresh: false, success: "Instalado");
        Load();
    }
}

// ───────────────────────── migrar o Linux para outro disco ─────────────────────────

public sealed partial class MigratePieceRow(LinuxMigrate.Piece p, LinuxMigrateViewModel owner) : ObservableObject
{
    public LinuxMigrate.Piece P { get; } = p;
    public string Title => P.Title;
    public string Detail => P.FsType == "swap" ? $"swap · {Fmt.Size(P.Size)} · {P.SourceDev}"
        : $"{P.FsType} · {Fmt.Size(P.Used)} usados de {Fmt.Size(P.Size)} · {P.SourceDev}" + (P.Subvols.Count > 1 ? $" · subvolumes {string.Join(", ", P.Subvols.Select(s => s.subvol))}" : "");
    public string TargetText => P.Include ? $"→ {Fmt.Size(P.TargetSize)}" : "fica no disco antigo";
    public bool Optional => !P.Required;
    public bool Include
    {
        get => P.Include;
        set { if (P.Include == value) return; P.Include = value; OnPropertyChanged(); owner.Replan(keepChoices: true); }
    }
}

public sealed partial class LinuxMigrateViewModel : PageViewModel
{
    public override string Title => "Migrar sistema para SSD/HD";
    public override string IconKey => "Migrate";
    public string Description => "Leva o Linux em uso — sistema, programas, configurações e arquivos — para um disco novo, pronto para dar boot. Cria as partições do zero (do tamanho certo para o disco novo), copia tudo preservando dono, permissões e atributos, ajusta fstab e GRUB e reinstala o boot.";
    public PickerViewModel Target { get; }
    public ObservableCollection<MigratePieceRow> Pieces { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private string _summary = "";
    private LinuxMigrate.Plan? _plan;

    public LinuxMigrateViewModel()
    {
        Target = new PickerViewModel
        {
            ShowPartitions = false,
            DiskFilter = d => d.IsSystem ? Ui.SystemDiskInUse : d.IsImage ? "Escolha um disco de verdade." : d.IsVirtual ? "Volume lógico: escolha um disco físico." : d.ReadOnly ? "Disco protegido contra gravação." : null,
        };
        Target.Changed += _ => Replan(false);
    }

    public override void OnDisksChanged() => Target.Load(Main.Disks);
    public override void OnShown(object? arg) { if (Target.Groups.Count == 0) Target.Load(Main.Disks); Replan(true); }

    public void Replan(bool keepChoices)
    {
        var t = Target.Selected?.Disk;
        var keep = keepChoices && _plan != null ? _plan.Pieces.ToDictionary(p => p.SourceDev + p.MountPoint, p => p.Include) : null;
        Pieces.Clear(); Notes.Clear(); Problem = null; CanRun = false; HasPlan = false;
        if (t == null) { Summary = "Escolha o disco novo."; return; }
        _plan = LinuxMigrate.Analyze(t);
        if (keep != null)
        {
            foreach (var p in _plan.Pieces) if (!p.Required && keep.TryGetValue(p.SourceDev + p.MountPoint, out var inc)) p.Include = inc;
            _plan = Recompute(_plan, t);
        }
        HasPlan = true;
        foreach (var p in _plan.Pieces) Pieces.Add(new MigratePieceRow(p, this));
        foreach (var n in _plan.Notes) Notes.Add(n);
        Notes.Add($"{(_plan.Uefi ? "UEFI" : "BIOS")} · carregador {_plan.Loader} · tabela GPT nova.");
        if (t.Partitions.Any(p => !p.IsUnallocated)) Notes.Add($"TUDO que existe hoje em {t.Title} ({t.Model}) será apagado.");
        Notes.Add("Feche os programas que estiverem gravando muito (navegador baixando, máquinas virtuais): o sistema é copiado com ele rodando, em duas passadas.");
        Problem = _plan.Problem;
        Summary = $"Copiar {Fmt.Size(_plan.Need)} para {t.Title} ({Fmt.Size(t.Size)})";
        CanRun = _plan.Problem == null;
    }

    private static LinuxMigrate.Plan Recompute(LinuxMigrate.Plan old, DiskInfo t)
    {
        var fresh = LinuxMigrate.Analyze(t);
        foreach (var p in fresh.Pieces) { var o = old.Pieces.FirstOrDefault(x => x.SourceDev == p.SourceDev && x.MountPoint == p.MountPoint); if (o != null && !p.Required) p.Include = o.Include; }
        return LinuxMigrate.Resize(fresh);
    }

    [RelayCommand]
    private async Task Run()
    {
        var plan = _plan; if (plan == null || plan.Problem != null) return;
        if (!await Main.RequirePremium("Migrar sistema", "Levar o Linux inteiro para outro SSD ou HD, pronto para dar boot, é do Premium.")) return;
        var t = plan.Target;
        if (!await Main.Confirm("Migrar o sistema", $"O Linux em uso vai para {t.Title} ({t.Model}, {Fmt.Size(t.Size)}).\n\nTUDO que existe hoje nesse disco será apagado. O disco atual não é alterado.", "Adicionar", true, phrase: "MIGRAR")) return;
        Main.Queue("Migrar sistema", $"Linux em uso → {t.Title} ({t.Model}) — apaga o conteúdo atual do destino", "Migrate", async s =>
        {
            var fresh = LinuxMigrate.Analyze(MainViewModel.Fresh(t));
            foreach (var p in fresh.Pieces) { var o = plan.Pieces.FirstOrDefault(x => x.SourceDev == p.SourceDev && x.MountPoint == p.MountPoint); if (o != null && !p.Required) p.Include = o.Include; }
            await LinuxMigrate.Run(LinuxMigrate.Resize(fresh), s);
        }, t, dangerous: true, canPause: true);
    }
}
