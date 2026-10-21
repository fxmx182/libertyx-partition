using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Recovery;

namespace LibertyX.Partition.ViewModels;

public sealed record RecoveryTarget(PartitionInfo Partition, bool Explore);

public sealed partial class TypeChip(string ext, string desc) : ObservableObject
{
    public string Ext { get; } = ext;
    public string Desc { get; } = desc;
    [ObservableProperty] private bool _isChecked = true;
}

public sealed partial class FolderNode : ObservableObject
{
    public RecoveredItem? Item { get; init; }
    public string Name { get; init; } = "";
    public ObservableCollection<FolderNode> Children { get; } = [];
    public List<RecoveredItem>? VirtualFiles { get; init; }
    public long Count { get; set; }
    public string CountText => Count > 0 ? Fmt.Num(Count) : "";
    public bool Deleted => Item != null && Item.Origin != ItemOrigin.Live;
    public Geometry? Icon { get; init; }
    public RecoveryViewModel Owner { get; init; } = null!;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool value) => Owner.CheckFolder(this, value);

    public IEnumerable<RecoveredItem> AllFiles()
    {
        if (VirtualFiles != null) { foreach (var f in VirtualFiles) yield return f; foreach (var c in Children) foreach (var f in c.AllFiles()) yield return f; yield break; }
        if (Item == null) yield break;
        var st = new Stack<RecoveredItem>();
        st.Push(Item);
        while (st.Count > 0)
        {
            var x = st.Pop();
            if (x.Children == null) continue;
            foreach (var c in x.Children) { if (c.IsDirectory) st.Push(c); else yield return c; }
        }
    }
}

public sealed partial class FileRow : ObservableObject
{
    public RecoveredItem Item { get; }
    private readonly RecoveryViewModel _owner;
    public FileRow(RecoveredItem item, RecoveryViewModel owner) { Item = item; _owner = owner; }
    public string Name => Item.Name;
    public string SizeText => Fmt.Size(Item.Size);
    public string DateText => Fmt.Date(Item.Modified);
    public string Folder => Item.Parent?.FullPath ?? "";
    public Geometry? Icon => Ui.Icon(Item.Category switch
    {
        FileCategory.Image => "Image", FileCategory.Video => "Video", FileCategory.Audio => "Audio", FileCategory.Document => "Document",
        FileCategory.Archive => "Archive", FileCategory.Code => "Code", FileCategory.Executable => "App", FileCategory.Database => "Database", _ => "File",
    });
    public string ConditionText => Item.Origin == ItemOrigin.Live ? "Existente" : Item.Condition switch
    {
        Condition.Excellent => "Excelente", Condition.Good => "Boa", Condition.Partial => "Parcial", Condition.Overwritten => "Sobrescrito", _ => Item.Origin == ItemOrigin.Carved ? "Escavado" : "Desconhecida",
    };
    public bool CondGood => Item.Origin != ItemOrigin.Live && Item.Condition is Condition.Excellent or Condition.Good;
    public bool CondPartial => Item.Condition == Condition.Partial;
    public bool CondBad => Item.Condition == Condition.Overwritten;
    public bool CondOther => !CondGood && !CondPartial && !CondBad;
    public bool IsDeleted => Item.Origin != ItemOrigin.Live;
    public bool IsChecked
    {
        get => _owner.IsChecked(Item);
        set { _owner.SetChecked(Item, value); OnPropertyChanged(); }
    }
    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>
/// Recuperar dados: escolher onde procurar → varrer (estrutura + assinaturas) → navegar,
/// pré-visualizar e copiar o que achou para outro disco.
/// </summary>
public sealed partial class RecoveryViewModel : PageViewModel
{
    public override string Title => "Recuperar dados";
    public override string IconKey => "RecoverFiles";

    public PickerViewModel Picker { get; } = new() { AllowUnallocated = true };
    public ObservableCollection<TypeChip> Types { get; } = [.. Carver.SupportedTypes.Select(t => new TypeChip(t.ext, t.desc))];

    [ObservableProperty] private int _step;
    [ObservableProperty] private bool _deepScan;
    [ObservableProperty] private bool _includeLive;
    [ObservableProperty] private bool _freeOnly = true;
    [ObservableProperty] private string _sourceText = "";

    [ObservableProperty] private string _scanPhase = "";
    [ObservableProperty] private double _scanFraction;
    [ObservableProperty] private long _scanFound;
    [ObservableProperty] private string _scanElapsed = "";
    [ObservableProperty] private bool _scanIndeterminate;

    public ObservableCollection<FolderNode> Tree { get; } = [];
    public ObservableCollection<FileRow> Files { get; } = [];
    public ObservableCollection<InfoRow> FileDetails { get; } = [];
    public ObservableCollection<string> ScanLog { get; } = [];
    [ObservableProperty] private FolderNode? _selectedFolder;
    [ObservableProperty] private FileRow? _selectedFile;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _originFilter;
    [ObservableProperty] private bool _hideOverwritten;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultSubtitle = "";
    [ObservableProperty] private string _selectionText = "Nada selecionado";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string? _previewText;
    [ObservableProperty] private string? _previewHex;
    [ObservableProperty] private string _listCaption = "";
    [ObservableProperty] private int _sortMode = 1;

    public bool IsChoosing => Step == 0;
    public bool IsScanning => Step == 1;
    public bool IsResults => Step == 2;
    public bool CanStart => Picker.Selected != null;
    partial void OnStepChanged(int value) { OnPropertyChanged(nameof(IsChoosing)); OnPropertyChanged(nameof(IsScanning)); OnPropertyChanged(nameof(IsResults)); }

    private ScanResult? _result;
    private IBlockDevice? _dev;
    private CancellationTokenSource? _cts;
    private readonly HashSet<RecoveredItem> _checked = [];
    private CancellationTokenSource? _previewCts;

    public RecoveryViewModel()
    {
        Picker.Changed += it =>
        {
            OnPropertyChanged(nameof(CanStart));
            if (it?.Partition is { IsUnallocated: true } || (it != null && it.Partition == null)) DeepScan = true;
            if (it?.Partition is { } p && !FsDetect.CanRead(p.FileSystem) && !p.IsUnallocated) DeepScan = true;
            // o Windows não abre ext, XFS, Btrfs, HFS+ etc.: o que se quer é copiar tudo para fora, não só os apagados
            if (it?.Partition is { FileSystem: FsKind.Ext or FsKind.HfsPlus or FsKind.Apfs or FsKind.Xfs or FsKind.Btrfs } && OperatingSystem.IsWindows()) IncludeLive = true;
        };
    }

    public override void OnDisksChanged() { if (Step == 0) Picker.Load(Main.Disks); }

    public override void OnShown(object? arg)
    {
        if (Picker.Groups.Count == 0) Picker.Load(Main.Disks);
        switch (arg)
        {
            case RecoveryTarget t:
                Picker.Select(t.Partition.Disk, t.Partition);
                if (t.Explore) { IncludeLive = true; DeepScan = false; Step = 0; _ = Start(); }
                break;
            case PartitionInfo p: Step = 0; Picker.Select(p.Disk, p); break;
            case DiskInfo d: Step = 0; Picker.Select(d, null); break;
        }
    }

    // ───────────── varredura ─────────────

    [RelayCommand]
    private async Task Start()
    {
        var sel = Picker.Selected;
        if (sel == null) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var opt = new ScanOptions
        {
            IncludeLive = IncludeLive, DeepScan = DeepScan, CarveFreeSpaceOnly = FreeOnly,
            CarveTypes = Types.All(t => t.IsChecked) ? null : Types.Where(t => t.IsChecked).Select(t => t.Ext).ToHashSet(),
        };
        SourceText = sel.Partition == null ? $"{sel.Disk.Title} · disco inteiro" : $"{sel.Title} · {sel.Disk.Title}";
        ScanLog.Clear();
        ScanFound = 0; ScanFraction = 0; ScanPhase = "Abrindo…"; ScanIndeterminate = true;
        Step = 1;
        _dev?.Dispose(); _dev = null;
        var sw = Stopwatch.StartNew();
        DateTime last = default;
        var progress = new Progress<ScanProgress>(p =>
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 100 && p.Fraction < 1) return;
            last = DateTime.UtcNow;
            ScanPhase = p.Phase; ScanFraction = p.Fraction; ScanFound = p.Found; ScanIndeterminate = false;
            ScanElapsed = Fmt.Duration(sw.Elapsed) + (p.Fraction > 0.02 && p.Fraction < 1 ? $" · faltam ~{Fmt.Duration(TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - p.Fraction) / p.Fraction))}" : "");
        });
        ScanResult? res = null;
        var holder = new ScanResult[1];
        try
        {
            res = await Task.Run(() => DoScan(sel.Disk, sel.Partition, opt, progress, ct, holder), ct);
        }
        catch (OperationCanceledException) { res = holder[0]; if (res != null) res.Log.Add("Varredura interrompida: mostrando o que foi achado até aqui."); }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            await Main.Alert("Não foi possível varrer", ex.Message, "danger");
            Step = 0;
            return;
        }
        if (res == null) { Step = 0; return; }
        BuildResults(res);
        Step = 2;
    }

    private ScanResult DoScan(DiskInfo d, PartitionInfo? p, ScanOptions opt, IProgress<ScanProgress> progress, CancellationToken ct, ScanResult[] holder)
    {
        if (p != null && !p.IsUnallocated)
        {
            _dev = DiskSystem.OpenPartition(p);
            var res = FileSystemScanner.Scan(_dev, opt, progress, ct);
            holder[0] = res;
            if (opt.DeepScan)
            {
                var ranges = opt.CarveFreeSpaceOnly && res.Allocation != null && !opt.IncludeLive ? res.Allocation.FreeRanges(_dev.Length).ToList() : [(0L, _dev.Length)];
                long? ntfsCluster = res.FileSystem == "NTFS" ? res.ClusterSize : res.Allocation == null ? 4096 : null;
                Carver.Carve(_dev, ranges, res, opt, progress, ct, ntfsCluster);
            }
            if (res.Root.Name is "Raiz" or "Volume") res.Root.Name = p.Letter ?? p.Label ?? "Partição";
            return res;
        }
        var disk = DiskSystem.OpenDisk(d, false);
        _dev = disk;
        var res2 = new ScanResult { Root = new RecoveredItem { Id = 0, Name = d.Title, IsDirectory = true, Origin = ItemOrigin.Live } };
        holder[0] = res2;
        if (p != null)
        {
            return DiskScanner.Scan(disk, d.Table, p.Offset, p.Length, "Espaço não alocado", opt, progress, ct, res2);
        }
        return DiskScanner.Scan(disk, d.Table, 0, disk.Length, d.Title, opt, progress, ct, res2);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void NewScan()
    {
        _cts?.Cancel();
        Step = 0;
        Picker.Load(Main.Disks);
    }

    // ───────────── resultados ─────────────

    private void BuildResults(ScanResult res)
    {
        _result = res;
        _checked.Clear();
        Tree.Clear();
        var files = res.Items.Where(i => !i.IsDirectory).ToList();
        long deleted = files.Count(f => f.Origin != ItemOrigin.Live);
        ResultTitle = $"{Fmt.Num(files.Count)} arquivos encontrados";
        ResultSubtitle = $"{SourceText} · {res.FileSystem} · {Fmt.Num(deleted)} apagados ou perdidos · {Fmt.Size(files.Sum(f => f.Size))}";
        foreach (var l in res.Log) ScanLog.Add(l);

        var root = MakeNode(res.Root);
        root.IsExpanded = true;
        Tree.Add(root);
        // vista por tipo
        var byType = new FolderNode { Name = "Por tipo de arquivo", Owner = this, Icon = Ui.Icon("Filter"), VirtualFiles = [], IsExpanded = true };
        foreach (var g in files.GroupBy(f => f.Category).OrderBy(g => g.Key))
        {
            var cat = new FolderNode { Name = FileTypes.CategoryName(g.Key), Owner = this, Icon = Ui.Icon("Folder"), VirtualFiles = [], Count = g.Count() };
            foreach (var e in g.GroupBy(f => f.Extension.Length > 0 ? f.Extension : "(sem extensão)").OrderByDescending(e => e.Count()))
                cat.Children.Add(new FolderNode { Name = "." + e.Key, Owner = this, Icon = Ui.Icon("File"), VirtualFiles = e.ToList(), Count = e.Count() });
            byType.Children.Add(cat);
        }
        byType.Count = files.Count;
        Tree.Add(byType);
        SelectedFolder = root;
        UpdateSelection();
    }

    private FolderNode MakeNode(RecoveredItem dir)
    {
        var n = new FolderNode { Item = dir, Name = dir.Name, Owner = this, Icon = Ui.Icon(dir.Origin == ItemOrigin.Live ? "Folder" : "FolderOpen") };
        long count = 0;
        if (dir.Children != null)
        {
            foreach (var c in dir.Children.Where(c => c.IsDirectory).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var child = MakeNode(c);
                count += child.Count;
                n.Children.Add(child);
            }
            count += dir.Children.Count(c => !c.IsDirectory);
        }
        n.Count = count;
        return n;
    }

    partial void OnSelectedFolderChanged(FolderNode? value) => RefreshList();
    partial void OnSearchChanged(string value) => RefreshList();
    partial void OnOriginFilterChanged(int value) => RefreshList();
    partial void OnHideOverwrittenChanged(bool value) => RefreshList();
    partial void OnSortModeChanged(int value) => RefreshList();

    [RelayCommand] private void SortBy(string mode) => SortMode = mode switch { "nome" => 0, "tamanho" => 1, "data" => 2, _ => 3 };

    private void RefreshList()
    {
        Files.Clear();
        if (_result == null) return;
        IEnumerable<RecoveredItem> src;
        if (!string.IsNullOrWhiteSpace(Search))
            src = _result.Items.Where(i => !i.IsDirectory && i.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase));
        else if (SelectedFolder?.VirtualFiles != null) src = SelectedFolder.AllFiles();
        else src = SelectedFolder?.Item?.Children?.Where(c => !c.IsDirectory) ?? [];
        src = OriginFilter switch
        {
            1 => src.Where(f => f.Origin != ItemOrigin.Live),
            2 => src.Where(f => f.Origin == ItemOrigin.Live),
            3 => src.Where(f => f.Origin == ItemOrigin.Carved || f.Origin == ItemOrigin.Orphan),
            _ => src,
        };
        if (HideOverwritten) src = src.Where(f => f.Condition != Condition.Overwritten);
        src = SortMode switch
        {
            0 => src.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
            1 => src.OrderByDescending(f => f.Size),
            2 => src.OrderByDescending(f => f.Modified ?? DateTime.MinValue),
            _ => src.OrderBy(f => f.Condition),
        };
        var list = src.Take(50_000).ToList();
        foreach (var f in list) Files.Add(new FileRow(f, this));
        ListCaption = list.Count == 50_000 ? "Mostrando os primeiros 50.000 — refine a busca" : $"{Fmt.Num(list.Count)} arquivo(s) aqui";
    }

    public bool IsChecked(RecoveredItem i) => _checked.Contains(i);

    public void SetChecked(RecoveredItem i, bool v)
    {
        if (v) _checked.Add(i); else _checked.Remove(i);
        UpdateSelection();
    }

    public void CheckFolder(FolderNode n, bool v)
    {
        foreach (var f in n.AllFiles()) { if (v) _checked.Add(f); else _checked.Remove(f); }
        foreach (var r in Files) r.Refresh();
        UpdateSelection();
    }

    [RelayCommand]
    private void CheckVisible()
    {
        bool all = Files.All(f => f.IsChecked);
        foreach (var f in Files) { if (all) _checked.Remove(f.Item); else _checked.Add(f.Item); f.Refresh(); }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        HasSelection = _checked.Count > 0;
        SelectionText = _checked.Count == 0 ? "Marque os arquivos que quer de volta" : $"{Fmt.Num(_checked.Count)} selecionado(s) · {Fmt.Size(_checked.Sum(f => f.Size))}";
    }

    // ───────────── prévia ─────────────

    partial void OnSelectedFileChanged(FileRow? value)
    {
        _previewCts?.Cancel();
        PreviewImage = null; PreviewText = null; PreviewHex = null;
        FileDetails.Clear();
        if (value == null) return;
        var it = value.Item;
        FileDetails.Add(new("Nome", it.Name));
        FileDetails.Add(new("Pasta", it.Parent?.FullPath ?? "—"));
        FileDetails.Add(new("Tamanho", $"{Fmt.Size(it.Size)} ({it.Size:N0} bytes)"));
        FileDetails.Add(new("Modificado", Fmt.Date(it.Modified)));
        FileDetails.Add(new("Criado", Fmt.Date(it.Created)));
        FileDetails.Add(new("Estado", value.ConditionText + (it.Origin == ItemOrigin.Carved ? " (por assinatura)" : it.Origin == ItemOrigin.Orphan ? " (registro MFT avulso)" : "")));
        if (it.Note != null) FileDetails.Add(new("Observação", it.Note));
        if (it.Data is ExtentSource es && es.Extents.Count > 0 && !es.Extents[0].Sparse)
            FileDetails.Add(new("Posição", $"byte {es.Extents[0].DeviceOffset:N0} · {es.Extents.Count} fragmento(s)"));
        var cts = _previewCts = new CancellationTokenSource();
        _ = Task.Run(() => LoadPreview(it, cts.Token));
    }

    private void LoadPreview(RecoveredItem it, CancellationToken ct)
    {
        if (it.Data == null || it.Size == 0) return;
        try
        {
            if (it.Category == FileCategory.Image && it.Size < 64L << 20 && it.Extension is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "ico")
            {
                var buf = new byte[it.Size];
                it.Data.Read(0, buf);
                if (ct.IsCancellationRequested) return;
                using var ms = new MemoryStream(buf);
                var bmp = Bitmap.DecodeToWidth(ms, 640);
                Ui.Post(() => { if (!ct.IsCancellationRequested) PreviewImage = bmp; });
                return;
            }
            var head = new byte[Math.Min(it.Size, 64 * 1024)];
            int n = it.Data.Read(0, head);
            if (ct.IsCancellationRequested) return;
            int printable = head.Take(n).Count(b => b >= 32 || b is 9 or 10 or 13);
            if (n > 0 && printable > n * 0.92)
            {
                string text = Encoding.UTF8.GetString(head, 0, n);
                Ui.Post(() => { if (!ct.IsCancellationRequested) PreviewText = text.Length > 6000 ? text[..6000] + "\n…" : text; });
                return;
            }
            var sb = new StringBuilder();
            for (int o = 0; o < Math.Min(n, 1024); o += 16)
            {
                sb.Append($"{o:X6}  ");
                for (int i = 0; i < 16; i++) sb.Append(o + i < n ? $"{head[o + i]:X2} " : "   ");
                sb.Append(' ');
                for (int i = 0; i < 16 && o + i < n; i++) { byte b = head[o + i]; sb.Append(b is >= 32 and < 127 ? (char)b : '·'); }
                sb.Append('\n');
            }
            Ui.Post(() => { if (!ct.IsCancellationRequested) PreviewHex = sb.ToString(); });
        }
        catch (Exception ex)
        {
            Ui.Post(() => { if (!ct.IsCancellationRequested) PreviewText = "Prévia indisponível: " + ex.Message; });
        }
    }

    // ───────────── exportar ─────────────

    [RelayCommand]
    private async Task Recover()
    {
        var files = _checked.ToList();
        if (files.Count == 0) return;
        var lic = Main.License;
        long want = files.Sum(f => f.Size);
        if (!lic.IsPremium && want > lic.FreeRemaining)
        {
            if (await Main.Confirm("Limite do Básico",
                $"O Básico recupera até {Fmt.Size(LibertyX.Partition.Core.Licensing.LicenseManager.FreeLimit)} no total, somando todas as recuperações. Você marcou {Fmt.Size(want)} e restam {Fmt.Size(lic.FreeRemaining)}.\n\nDesmarque alguns arquivos, ou ative o Premium para recuperar sem limite: os arquivos que você está vendo continuam aqui.",
                "Conhecer o Premium", icon: "Crown"))
                Main.Navigate("premium");
            return;
        }
        var dest = await MainViewModel.PickFolder("Onde salvar os arquivos recuperados");
        if (dest == null) return;
        var src = Picker.Selected;
        if (src != null && !src.Disk.IsImage && OperatingSystem.IsLinux())
        {
            // o disco físico de onde vem o destino (atravessando LVM/LUKS) é o mesmo que está sendo recuperado?
            string? mp = ActionsBuilder.MountOnDisk(dest, src.Disk);
            bool same = mp != null;
            if (same && !await Main.Confirm("Mesmo disco da origem",
                    $"Você vai gravar os arquivos recuperados em {mp}, que fica no MESMO disco que está sendo recuperado.\n\nCada arquivo gravado pode cair exatamente em cima de outro que ainda não foi recuperado — e esse se perde para sempre. Use outro disco, um pendrive ou um HD externo.",
                    "Gravar mesmo assim", true)) return;
        }
        if (src != null && !src.Disk.IsImage && OperatingSystem.IsWindows())
        {
            string root = Path.GetPathRoot(dest)?.TrimEnd('\\') ?? "";
            var destDisk = Main.Disks.FirstOrDefault(d => d.Partitions.Any(p => string.Equals(p.Letter, root, StringComparison.OrdinalIgnoreCase)));
            if (destDisk != null && destDisk.Number == src.Disk.Number && !destDisk.IsImage)
            {
                if (!await Main.Confirm("Mesmo disco da origem",
                    $"Você vai gravar os arquivos recuperados em {root}, que fica no MESMO disco que está sendo recuperado.\n\nCada arquivo gravado pode cair exatamente em cima de outro que ainda não foi recuperado — e esse se perde para sempre. Use outro disco, um pendrive ou um HD externo.",
                    "Gravar mesmo assim", true)) return;
            }
        }
        long total = files.Sum(f => f.Size);
        string? report = null;
        long saved = 0;
        bool ok = await Main.Run("Recuperando arquivos", s =>
        {
            var rep = RecoveryExporter.Export(files, dest, true, new Progress<ExportProgress>(p =>
                s.Progress(p.BytesTotal == 0 ? (double)p.FilesDone / Math.Max(1, p.FilesTotal) : (double)p.BytesDone / p.BytesTotal,
                    $"{Fmt.Num(p.FilesDone)} de {Fmt.Num(p.FilesTotal)} arquivos", p.Current)), s.Token);
            s.Log($"{rep.Saved} arquivo(s) salvos ({Fmt.Size(rep.Bytes)}).");
            saved = rep.Bytes;
            if (rep.WithBadSectors > 0) s.Log($"{rep.WithBadSectors} arquivo(s) tinham setores ilegíveis (preenchidos com zeros).");
            if (rep.Failed > 0) s.Log($"{rep.Failed} arquivo(s) não puderam ser gravados — veja o relatório.");
            s.Log($"Relatório: {rep.ReportPath}");
            report = rep.ReportPath;
            // no Linux o app roda como root: os arquivos voltam a ser de quem pediu a recuperação
            if (OperatingSystem.IsLinux()) Core.Platform.Linux.LinuxSession.GiveBack(dest);
        }, refresh: false, success: $"Arquivos salvos em {dest}");
        if (saved > 0 && !lic.IsPremium) { lic.AddFreeRecovered(saved); Main.LicenseChanged(); }
        if (ok && Main.Operation != null) Main.Operation.AfterClose = () => { MainViewModel.OpenInExplorer(dest); return Task.CompletedTask; };
    }

    [RelayCommand] private Task OpenImage() => Main.OpenImageCommand.ExecuteAsync(null);
}
