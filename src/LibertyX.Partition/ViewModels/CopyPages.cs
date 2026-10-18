using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;

namespace LibertyX.Partition.ViewModels;

// ───────────────────────── copiar partição ─────────────────────────

public sealed partial class CopyPartitionViewModel : PageViewModel
{
    public override string Title => "Copiar partição";
    public override string IconKey => "CopyPartition";
    public PickerViewModel Source { get; } = new() { AllowDisks = false };
    public PickerViewModel Target { get; } = new()
    {
        AllowDisks = false, AllowUnallocated = true,
        PartitionFilter = p => !p.IsUnallocated ? "Escolha um espaço não alocado." : p.Disk.ReadOnly ? "Imagem só para leitura." : p.Disk.Table.Style == TableStyle.None ? "Inicialize o disco primeiro." : null,
    };
    [ObservableProperty] private bool _usedOnly = true;
    [ObservableProperty] private bool _useVss = true;
    [ObservableProperty] private string? _problem;
    public bool CanCopy => Source.Selected?.Partition != null && Target.Selected?.Partition != null && Problem == null;

    public CopyPartitionViewModel()
    {
        Source.Changed += _ => Check();
        Target.Changed += _ => Check();
    }

    private void Check()
    {
        var s = Source.Selected?.Partition;
        var t = Target.Selected?.Partition;
        Problem = s == null || t == null ? null
            : t.Length < s.Length + 1024 * 1024 ? $"O espaço livre ({Fmt.Size(t.Length)}) é menor que a partição ({Fmt.Size(s.Length)})."
            : s.Disk.SectorSize != t.Disk.SectorSize ? "Discos com tamanhos de setor diferentes (512 × 4096)."
            : t.Disk.Table.Style == TableStyle.Mbr && t.Disk.Table.Entries.Count(e => !e.IsLogical) >= 4 ? "O disco de destino (MBR) já tem 4 partições primárias." : null;
        OnPropertyChanged(nameof(CanCopy));
    }

    public override void OnDisksChanged() { Source.Load(Main.Disks); Target.Load(Main.Disks); }
    public override void OnShown(object? arg)
    {
        if (Source.Groups.Count == 0) OnDisksChanged();
        if (arg is PartitionInfo p) Source.Select(p.Disk, p);
    }

    [RelayCommand]
    private async Task Copy()
    {
        var s = Source.Selected!.Partition!;
        var t = Target.Selected!.Partition!;
        long start = (t.Offset + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024);
        bool used = UsedOnly, vss = UseVss;
        Main.Queue("Copiar partição", $"{Source.Selected.Title} ({Fmt.Size(s.Length)}) → espaço livre de {t.Disk.Title}, em {Fmt.Size(start)}", "CopyPartition", async sink =>
        {
            var sq = MainViewModel.FreshPart(s);
            var tq = MainViewModel.FreshPart(t);
            await DiskOperations.CopyPartition(sq, tq.Disk, start / tq.Disk.SectorSize, used, vss, sink);
        }, t.Disk, canPause: true);
        await Task.CompletedTask;
    }
}

// ───────────────────────── clonar disco / migrar sistema ─────────────────────────

public sealed partial class CloneViewModel : PageViewModel
{
    private readonly bool _migrate;
    public CloneViewModel(bool migrate)
    {
        _migrate = migrate;
        Source = new PickerViewModel { ShowPartitions = false, DiskFilter = d => migrate && !d.IsSystem ? "Só o disco do Windows." : d.IsImage ? "Para imagem, use Restaurar imagem." : null };
        Target = new PickerViewModel
        {
            ShowPartitions = false,
            DiskFilter = d => d.IsSystem ? "Disco do Windows em uso." : d.ReadOnly ? "Imagem só para leitura." : Source?.Selected?.Disk.Number == d.Number && !d.IsImage ? "É o disco de origem." : null,
        };
        Source.Changed += _ => { Target.Load(Main.Disks); Replan(); };
        Target.Changed += _ => Replan();
        _systemOnly = migrate;
        _makeBootable = migrate;
    }
    public bool Migrate => _migrate;
    public override string Title => _migrate ? "Migrar sistema para SSD/HD" : "Copiar disco";
    public override string IconKey => _migrate ? "Migrate" : "CopyDisk";
    public string Description => _migrate
        ? "Leva o Windows, com programas e arquivos, para um disco novo — com o Windows rodando, via instantâneo VSS. Depois é só trocar o disco de inicialização."
        : "Cópia completa de um disco para outro, partição por partição, com as que estão em uso copiadas por instantâneo VSS.";

    public PickerViewModel Source { get; }
    public PickerViewModel Target { get; }
    public ObservableCollection<PartitionVM> SourceMap { get; } = [];
    public ObservableCollection<PartitionVM> TargetMap { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    [ObservableProperty] private bool _systemOnly;
    [ObservableProperty] private bool _fitToTarget = true;
    [ObservableProperty] private bool _usedOnly = true;
    [ObservableProperty] private bool _useVss = true;
    [ObservableProperty] private bool _makeBootable;
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private bool _canShrink;
    [ObservableProperty] private string _shrinkText = "";
    [ObservableProperty] private bool _hasPlan;
    private DiskOperations.ClonePlan? _plan;

    partial void OnSystemOnlyChanged(bool value) => Replan();
    partial void OnFitToTargetChanged(bool value) => Replan();

    public override void OnDisksChanged()
    {
        Source.Load(Main.Disks);
        if (_migrate && Source.Selected == null) { var sys = Main.Disks.FirstOrDefault(d => d.IsSystem); if (sys != null) Source.Select(sys, null); }
        Target.Load(Main.Disks);
    }
    public override void OnShown(object? arg)
    {
        if (Source.Groups.Count == 0) OnDisksChanged();
        if (arg is DiskInfo d && !_migrate) Source.Select(d, null);
    }

    private void Replan()
    {
        Notes.Clear(); SourceMap.Clear(); TargetMap.Clear();
        _plan = null; CanRun = false; CanShrink = false; HasPlan = false;
        var s = Source.Selected?.Disk; var t = Target.Selected?.Disk;
        if (s != null) { var dvm = new DiskVM(s); foreach (var p in dvm.Partitions) SourceMap.Add(p); }
        if (s == null || t == null) return;
        var plan = DiskOperations.PlanClone(s, t, SystemOnly, FitToTarget);
        _plan = plan;
        HasPlan = true;
        var fake = new DiskInfo { Number = t.Number, Size = t.Size, SectorSize = t.SectorSize, Model = t.Model };
        var fvm = new DiskVM(fake);
        int ss = s.SectorSize;
        long cur = 2048;
        foreach (var (src, start, sectors) in plan.Items)
        {
            if (start - cur >= 2048) TargetMap.Add(new PartitionVM(new PartitionInfo { Disk = fake, Offset = cur * ss, Length = (start - cur) * ss }, fvm));
            long len = sectors * ss + (src == plan.Grow ? plan.GrowBy : 0);
            TargetMap.Add(new PartitionVM(new PartitionInfo
            {
                Disk = fake, Entry = src.Entry, Offset = start * ss, Length = len, FileSystem = src.FileSystem, Label = src.Label, Letter = src.Letter,
                Used = src.Used, Free = src.Used != null ? len - src.Used : null,
            }, fvm));
            cur = start + len / ss;
        }
        if (t.Size / ss - cur > 2048) TargetMap.Add(new PartitionVM(new PartitionInfo { Disk = fake, Offset = cur * ss, Length = t.Size - cur * ss }, fvm));

        long need = plan.Items.Sum(i => i.src.Length);
        Notes.Add($"Serão copiadas {plan.Items.Count} partições ({Fmt.Size(need)}); em uso: {Fmt.Size(plan.Items.Sum(i => i.src.Used ?? i.src.Length))}.");
        if (plan.Grow != null) Notes.Add($"{plan.Grow.Letter ?? plan.Grow.Label} vai crescer {Fmt.Size(plan.GrowBy)} para usar o disco novo inteiro.");
        if (t.Partitions.Any(p => !p.IsUnallocated)) Notes.Add($"TUDO que existe hoje em {t.Title} ({t.Model}) será apagado.");
        if (t.SectorSize != s.SectorSize) { Notes.Add("Os discos têm setores de tamanhos diferentes (512 × 4096): a cópia setor a setor não é possível."); return; }
        if (plan.Deficit > 0)
        {
            Notes.Add($"Não cabe: faltam {Fmt.Size(plan.Deficit)} no disco de destino.");
            if (plan.ShrinkCandidate is { } sc && OperatingSystem.IsWindows() && sc.Letter != null)
            {
                CanShrink = true;
                ShrinkText = $"Reduzir {sc.Letter} em {Fmt.Size(plan.Deficit + (1L << 30))} (pelo próprio Windows, sem perder dados)";
            }
            return;
        }
        CanRun = true;
    }

    [RelayCommand]
    private async Task Shrink()
    {
        var sc = _plan?.ShrinkCandidate; if (sc == null || _plan == null) return;
        long newSize = (sc.Length - _plan.Deficit - (1L << 30)) / (1024 * 1024) * (1024 * 1024);
        var sup = await WinCommands.SupportedSize(sc.Disk.Number, sc.Offset);
        if (sup != null && newSize < sup.Value.min)
        {
            await Main.Alert("Não dá para reduzir tanto", $"O Windows consegue reduzir {sc.Letter} só até {Fmt.Size(sup.Value.min)} (há arquivos fixos no fim do volume). Libere espaço ou escolha um disco maior.", "warn");
            return;
        }
        Main.Queue("Reduzir " + sc.Letter, $"{sc.Letter}: {Fmt.Size(sc.Length)} → {Fmt.Size(newSize)} (Diminuir volume do Windows, sem perder dados)", "Resize", async s =>
        {
            var q = MainViewModel.FreshPart(sc);
            var r = await WinCommands.Resize(q.Disk.Number, q.Offset, newSize, s.Token);
            if (!r.Ok) throw new InvalidOperationException(r.Message);
        }, sc.Disk);
    }

    [RelayCommand]
    private async Task Run()
    {
        var plan = _plan; if (plan == null) return;
        if (!await Main.RequirePremium(_migrate ? "Migrar sistema" : "Copiar disco",
            _migrate ? "Levar o Windows inteiro para outro SSD ou HD, pronto para dar boot, é do Premium."
                     : "Clonar um disco inteiro para outro é do Premium. Copiar uma partição continua grátis.")) return;
        bool used = UsedOnly, vss = UseVss, boot = MakeBootable, sysOnly = SystemOnly, fit = FitToTarget;
        var src = plan.Source; var dst = plan.Target;
        Main.Queue(_migrate ? "Migrar sistema" : "Copiar disco", $"{src.Title} ({src.Model}) → {dst.Title} ({dst.Model}) — apaga o conteúdo atual do destino", IconKey, async s =>
        {
            var fresh = DiskOperations.PlanClone(MainViewModel.Fresh(src), MainViewModel.Fresh(dst), sysOnly, fit);
            await DiskOperations.Clone(fresh, used, vss, boot, s);
        }, dst, dangerous: true, canPause: true);
        await Task.CompletedTask;
    }
}

// ───────────────────────── backup e imagem ─────────────────────────

public sealed partial class BackupViewModel : PageViewModel
{
    public override string Title => "Backup e imagem";
    public override string IconKey => "Backup";
    [ObservableProperty] private int _tab;

    // criar imagem
    public PickerViewModel ImageSource { get; } = new();
    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private bool _hash = true;
    [ObservableProperty] private bool _useVss = true;
    [ObservableProperty] private int _retries = 1;
    public bool CanImage => ImageSource.Selected != null && ImagePath.Length > 0;
    partial void OnImagePathChanged(string value) => OnPropertyChanged(nameof(CanImage));

    // restaurar
    [ObservableProperty] private string _restoreFile = "";
    [ObservableProperty] private string _restoreInfo = "";
    public PickerViewModel RestoreTarget { get; } = new()
    {
        DiskFilter = d => d.IsSystem ? "Disco do Windows em uso." : d.ReadOnly ? "Imagem só para leitura." : null,
        PartitionFilter = p => p.IsSystemCritical ? "Partição do sistema em uso." : p.IsUnallocated ? null : p.Disk.ReadOnly ? "Imagem só para leitura." : null,
        AllowUnallocated = false,
    };
    public bool CanRestore => RestoreFile.Length > 0 && RestoreTarget.Selected != null;
    partial void OnRestoreFileChanged(string value) => OnPropertyChanged(nameof(CanRestore));

    // pastas
    public ObservableCollection<string> Folders { get; } = [];
    [ObservableProperty] private string _zipPath = "";
    public bool CanZip => Folders.Count > 0 && ZipPath.Length > 0;
    partial void OnZipPathChanged(string value) => OnPropertyChanged(nameof(CanZip));

    public BackupViewModel()
    {
        ImageSource.Changed += it =>
        {
            OnPropertyChanged(nameof(CanImage));
            if (it != null && ImagePath.Length == 0) { }
        };
        RestoreTarget.Changed += _ => OnPropertyChanged(nameof(CanRestore));
        Folders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanZip));
    }

    public override void OnDisksChanged() { ImageSource.Load(Main.Disks); RestoreTarget.Load(Main.Disks); }
    public override void OnShown(object? arg)
    {
        if (ImageSource.Groups.Count == 0) OnDisksChanged();
        if (arg is DiskInfo d) { Tab = 0; ImageSource.Select(d, null); }
        if (arg is PartitionInfo p) { Tab = 0; ImageSource.Select(p.Disk, p); }
    }

    [RelayCommand]
    private async Task PickImagePath()
    {
        var sel = ImageSource.Selected;
        string name = sel == null ? "imagem" : RecoveryNames.Safe($"{sel.Disk.Model} {(sel.Partition != null ? sel.Title : "disco")} {DateTime.Now:yyyy-MM-dd}");
        var f = await MainViewModel.SaveFile("Salvar imagem como", name + ".img", "img", "Imagem de disco");
        if (f != null) ImagePath = f;
    }

    [RelayCommand]
    private async Task CreateImage()
    {
        var sel = ImageSource.Selected; if (sel == null) return;
        long size = sel.Partition?.Length ?? sel.Disk.Size;
        string? root = Path.GetPathRoot(ImagePath)?.TrimEnd('\\');
        if (OperatingSystem.IsWindows() && !sel.Disk.IsImage && sel.Disk.Partitions.Any(p => string.Equals(p.Letter, root, StringComparison.OrdinalIgnoreCase)))
        {
            if (!await Main.Confirm("Destino dentro da origem", $"A imagem seria gravada em {root}, que fica no próprio disco que está sendo copiado. Num disco com defeito isso acelera a perda — use outro disco.", "Continuar mesmo assim", true)) return;
        }
        string path = ImagePath; bool hash = Hash, vss = UseVss; int retries = Retries;
        await Main.Run("Criando imagem", s => DiskOperations.CreateImage(sel.Disk, sel.Partition, path, vss, hash, retries, s), refresh: false, canPause: true, success: $"Imagem salva em {path}");
    }

    [RelayCommand]
    private async Task PickRestoreFile()
    {
        var f = await MainViewModel.PickFile("Imagem para restaurar", ("Imagens de disco", ["*.img", "*.dd", "*.raw", "*.bin", "*.iso"]));
        if (f == null) return;
        RestoreFile = f;
        string info = $"{Fmt.Size(new FileInfo(f).Length)}";
        try
        {
            var meta = JsonSerializer.Deserialize<DiskOperations.ImageMeta>(await File.ReadAllTextAsync(f + ".json"));
            if (meta != null) info = $"{meta.Source} · {meta.Model} · {Fmt.Size(meta.Size)} · criada em {meta.Created:dd/MM/yyyy HH:mm}" + (meta.BadSectors > 0 ? $" · {meta.BadSectors} setores ilegíveis" : "");
        }
        catch { }
        RestoreInfo = info;
    }

    [RelayCommand]
    private async Task Restore()
    {
        var t = RestoreTarget.Selected; if (t == null) return;
        if (!await Main.RequirePremium("Restaurar imagem", "Criar imagens de disco é grátis; gravar uma imagem de volta num disco ou partição é do Premium.")) return;
        string what = t.Partition == null ? $"o disco {t.Disk.Title} ({t.Disk.Model}) inteiro" : $"a partição {t.Title}";
        string file = RestoreFile;
        var td = t.Disk; var tp = t.Partition;
        Main.Queue("Restaurar imagem", $"{Path.GetFileName(file)} sobrescreve {what}", "Restore", s =>
        {
            var pq = tp == null ? null : MainViewModel.FreshPart(tp);
            DiskOperations.RestoreImage(file, pq?.Disk ?? MainViewModel.Fresh(td), pq, s);
        }, td, dangerous: true, canPause: true);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var f = await MainViewModel.PickFolder("Pasta para o backup");
        if (f != null && !Folders.Contains(f)) Folders.Add(f);
    }

    [RelayCommand] private void RemoveFolder(string f) => Folders.Remove(f);

    [RelayCommand]
    private async Task PickZip()
    {
        var f = await MainViewModel.SaveFile("Salvar backup como", $"Backup {DateTime.Now:yyyy-MM-dd}.zip", "zip", "Arquivo ZIP");
        if (f != null) ZipPath = f;
    }

    [RelayCommand]
    private async Task Zip()
    {
        var folders = Folders.ToList();
        string zip = ZipPath;
        await Main.Run("Backup de pastas", s =>
        {
            s.Progress(-1, "Listando arquivos");
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var files = folders.SelectMany(f => new DirectoryInfo(f).EnumerateFiles("*", opts).Select(x => (root: f, file: x))).ToList();
            long total = files.Sum(f => f.file.Length), done = 0;
            int skipped = 0;
            using (var fs = new FileStream(zip, FileMode.Create))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
            var buf = new byte[1 << 20];
            foreach (var (root, file) in files)
            {
                s.Token.ThrowIfCancellationRequested();
                string rel = Path.Combine(Path.GetFileName(root.TrimEnd('\\', '/')), Path.GetRelativePath(root, file.FullName)).Replace('\\', '/');
                try
                {
                    using var src = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var e = za.CreateEntry(rel, CompressionLevel.Fastest);
                    e.LastWriteTime = file.LastWriteTime;
                    using var dst = e.Open();
                    int n;
                    while ((n = src.Read(buf)) > 0) { dst.Write(buf, 0, n); done += n; s.Progress(total == 0 ? 1 : (double)done / total, $"{files.Count:N0} arquivos", rel); }
                }
                catch (IOException ex) { skipped++; s.Log($"Pulado (em uso): {file.FullName} — {ex.Message}"); }
                catch (UnauthorizedAccessException) { skipped++; s.Log($"Pulado (sem permissão): {file.FullName}"); }
            }
            }
            s.Log($"{files.Count - skipped:N0} arquivos em {zip} ({Fmt.Size(new FileInfo(zip).Length)} compactado).");
        }, refresh: false);
    }
}
