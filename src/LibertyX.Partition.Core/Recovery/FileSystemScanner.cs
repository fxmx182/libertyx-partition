using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.Recovery;

/// <summary>
/// Varredura rápida: lê as estruturas do sistema de arquivos (MFT, diretórios FAT/exFAT)
/// e devolve a árvore de arquivos — os apagados com nome, pasta e datas originais.
/// </summary>
public static partial class FileSystemScanner
{
    public static ScanResult Scan(IBlockDevice vol, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var kind = FsDetect.Detect(vol);
        ScanResult? r = null;
        string? err = null;
        try
        {
            switch (kind)
            {
                case FsKind.Ntfs:
                    var n = NtfsVolume.Open(vol, out err);
                    if (n != null) r = ScanNtfs(n, opt, progress, ct);
                    break;
                case FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32:
                    var f = FatVolume.Open(vol, out err);
                    if (f != null) r = ScanFat(f, opt, progress, ct);
                    break;
                case FsKind.ExFat:
                    var x = ExFatVolume.Open(vol, out err);
                    if (x != null) r = ScanExFat(x, opt, progress, ct);
                    break;
                case FsKind.Ext:
                    var e = ExtVolume.Open(vol, out err);
                    if (e != null) r = ScanExt(e, opt, progress, ct);
                    break;
                case FsKind.HfsPlus:
                    var h = HfsVolume.Open(vol, out err);
                    if (h != null) r = ScanHfs(h, opt, progress, ct);
                    break;
                case FsKind.Apfs:
                    var ap = ApfsContainer.Open(vol, out err);
                    if (ap != null) r = ScanApfs(ap, opt, progress, ct);
                    break;
                case FsKind.Xfs:
                    var xf = XfsVolume.Open(vol, out err);
                    if (xf != null) r = ScanXfs(xf, opt, progress, ct);
                    break;
                case FsKind.Btrfs:
                    var bt = BtrfsVolume.Open(vol, out err);
                    if (bt != null) { r = ScanBtrfs(bt, opt, progress, ct); if (err != null) r.Log.Insert(0, err); }
                    break;
                default:
                    // Boot destruído? Tenta as cópias de reserva de cada formato.
                    var n2 = NtfsVolume.Open(vol, out _);
                    if (n2 != null) { r = ScanNtfs(n2, opt, progress, ct); r.Log.Add(L.T("Setor de boot destruído — usada a cópia de reserva do NTFS.")); break; }
                    var f2 = FatVolume.Open(vol, out _);
                    if (f2 != null) { r = ScanFat(f2, opt, progress, ct); r.Log.Add(L.T("Setor de boot destruído — usada a cópia de reserva do FAT32.")); break; }
                    var x2 = ExFatVolume.Open(vol, out _);
                    if (x2 != null) { r = ScanExFat(x2, opt, progress, ct); r.Log.Add(L.T("Setor de boot destruído — usada a cópia de reserva do exFAT.")); break; }
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { err = ex.Message; r = null; }

        if (r == null)
        {
            r = new ScanResult { Root = new RecoveredItem { Id = 0, Name = L.T("Volume"), IsDirectory = true, Origin = ItemOrigin.Live } };
            r.FileSystem = FsDetect.Name(kind);
            r.Log.Add(err ?? L.T($"Sistema de arquivos {FsDetect.Name(kind)} não é lido pela varredura rápida — use a varredura profunda."));
        }
        return r;
    }

    private static Condition Evaluate(long allocated, long total)
    {
        if (total == 0) return Condition.Excellent;
        if (allocated == 0) return Condition.Good;
        return allocated >= total ? Condition.Overwritten : Condition.Partial;
    }

    // ───────────────────────────── NTFS ─────────────────────────────

    private sealed class Node
    {
        public long Index;
        public ushort Seq;
        public bool InUse, IsDir;
        public long ParentIndex;
        public ushort ParentSeq;
        public string Name = "";
        public DateTime? Created, Modified;
        public NtfsAttr? Data;
        public List<NtfsAttr>? Ext;
        public RecoveredItem? Item;
    }

    public static ScanResult ScanNtfs(NtfsVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        string label = v.ReadLabel() ?? "";
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 5, Name = label.Length > 0 ? label : L.T("Raiz"), IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = "NTFS", Label = label, ClusterSize = v.ClusterSize,
        };
        if (v.UsedBackupBoot) res.Log.Add(L.T("Setor de boot principal danificado — usada a cópia do fim da partição."));

        progress?.Report(new(L.T("Lendo o mapa de clusters"), 0, 0, 0, 1));
        byte[]? bmp = null;
        try { bmp = v.LoadBitmap(); } catch (Exception ex) { res.Log.Add(L.T("Mapa de clusters ($Bitmap) ilegível: ") + ex.Message); }
        if (bmp != null)
        {
            var b = bmp;
            res.Allocation = new AllocationMap(0, v.ClusterSize, v.TotalClusters, c => NtfsVolume.IsSet(b, c));
        }

        var nodes = new Dictionary<long, Node>();
        var ext = new Dictionary<long, List<NtfsAttr>>();
        long total = v.RecordCount;
        int rs = v.RecordSize;
        int perChunk = Math.Max(1, (4 << 20) / rs);
        var buf = new byte[perChunk * rs];
        long index = 0, bad = 0;
        long vcnBytes = 0;
        foreach (var run in v.MftRuns)
        {
            long runBytes = run.Length * v.ClusterSize;
            for (long off = 0; off < runBytes && index < total; off += buf.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, runBytes - off);
                n = (int)Math.Min(n, (total - index) * rs);
                if (run.Sparse) { index += n / rs; continue; }
                int got;
                try { got = v.Dev.ReadBytes(run.Lcn * v.ClusterSize + off, buf.AsSpan(0, n)); }
                catch (MediaReadException) { got = 0; bad += n / rs; res.Log.Add(L.T($"Setores ilegíveis no MFT (registros {index}–{index + n / rs}).")); }
                for (int p = 0; p + rs <= got; p += rs, index++)
                {
                    var rec = NtfsVolume.ParseRecordStatic(buf.AsSpan(p, rs), index);
                    if (rec == null) continue;
                    if (rec.BaseIndex != 0 && rec.BaseIndex != index)
                    {
                        foreach (var a in rec.Attrs.Where(a => a.Type == 0x80 && a.Name.Length == 0 && a.NonResident))
                        {
                            if (!ext.TryGetValue(rec.BaseIndex, out var l)) ext[rec.BaseIndex] = l = [];
                            l.Add(a);
                        }
                        continue;
                    }
                    var fn = rec.BestName;
                    if (fn == null || index < 16) continue;
                    if (!rec.IsDir && rec.InUse && !opt.IncludeLive) continue;
                    var data = rec.UnnamedData;
                    nodes[index] = new Node
                    {
                        Index = index, Seq = rec.Seq, InUse = rec.InUse, IsDir = rec.IsDir,
                        ParentIndex = fn.ParentIndex, ParentSeq = fn.ParentSeq, Name = fn.Name,
                        Created = rec.Created ?? fn.Created, Modified = rec.Modified ?? fn.Modified, Data = data,
                    };
                }
                if (got < n) index += (n - got) / rs;
                vcnBytes += n;
                progress?.Report(new(L.T("Lendo a tabela de arquivos (MFT)"), (double)index / total, nodes.Count, index, total));
            }
        }

        // Monta as pastas e os itens.
        var lost = new RecoveredItem { Id = -2, Name = L.T("$Sem pasta (órfãos)"), IsDirectory = true, Origin = ItemOrigin.Orphan, Note = L.T("pasta original não existe mais"), Condition = Condition.Unknown };
        bool lostUsed = false;
        foreach (var n in nodes.Values)
        {
            ext.TryGetValue(n.Index, out n.Ext);
            var item = new RecoveredItem
            {
                Id = n.Index, Name = n.Name, IsDirectory = n.IsDir,
                Origin = n.InUse ? ItemOrigin.Live : ItemOrigin.Deleted,
                Created = n.Created, Modified = n.Modified,
            };
            if (!n.IsDir && n.Data != null)
            {
                var src = v.DataSourceFor(n.Data, n.Ext);
                item.Data = src;
                item.Size = src.Length;
                if (n.Data.Encrypted) item.Note = L.T("criptografado (EFS) — sem a chave o conteúdo sai ilegível");
                else if (n.Data.Compressed) item.Note = L.T("comprimido pelo NTFS");
                item.Condition = n.InUse ? Condition.Excellent : !n.Data.NonResident ? Condition.Excellent : ConditionNtfs(n, bmp);
            }
            else item.Condition = n.InUse ? Condition.Excellent : Condition.Good;
            n.Item = item;
        }

        // Liga filhos a pais, conferindo o número de sequência (registro reaproveitado = pai errado).
        foreach (var n in nodes.Values)
        {
            RecoveredItem parentItem;
            if (n.ParentIndex == 5) parentItem = res.Root;
            else if (nodes.TryGetValue(n.ParentIndex, out var p) && p.IsDir && p.Item != null &&
                     (p.Seq == n.ParentSeq || (!p.InUse && (ushort)(n.ParentSeq + 1) == p.Seq)))
                parentItem = p.Item;
            else { parentItem = lost; lostUsed = true; }
            parentItem.Children ??= [];
            parentItem.Children.Add(n.Item!);
            n.Item!.Parent = parentItem;
            n.Item.ParentId = parentItem.Id;
        }
        if (lostUsed) { res.Root.Children ??= []; res.Root.Children.Add(lost); lost.Parent = res.Root; lost.ParentId = res.Root.Id; }

        // Sem os vivos, poda as pastas vivas que não levam a nada apagado.
        if (!opt.IncludeLive) Prune(res.Root);
        Flatten(res.Root, res.Items);
        // Lixeira: $I guarda o nome original do $R.
        RecycleBin.Annotate(res.Root);
        if (bad > 0) res.Log.Add(L.T($"{bad} registros MFT ficaram em setores ilegíveis."));
        res.Log.Add(L.T($"MFT: {total:N0} registros lidos, {res.Items.Count(i => !i.IsDirectory):N0} arquivos listados."));
        return res;
    }

    private static Condition ConditionNtfs(Node n, byte[]? bmp)
    {
        if (bmp == null) return Condition.Unknown;
        long alloc = 0, total = 0;
        var pieces = new List<NtfsAttr> { n.Data! };
        if (n.Ext != null) pieces.AddRange(n.Ext);
        foreach (var p in pieces)
            foreach (var r in p.Runs ?? [])
            {
                if (r.Sparse) continue;
                // amostra até 4096 clusters por trecho: suficiente para a estimativa e não trava em arquivos enormes
                long step = Math.Max(1, r.Length / 4096);
                for (long c = 0; c < r.Length; c += step) { total++; if (NtfsVolume.IsSet(bmp, r.Lcn + c)) alloc++; }
            }
        return Evaluate(alloc, total);
    }

    private static bool Prune(RecoveredItem dir)
    {
        if (dir.Children == null) return dir.Origin != ItemOrigin.Live;
        dir.Children.RemoveAll(c => c.IsDirectory ? !Prune(c) : c.Origin == ItemOrigin.Live);
        return dir.Children.Count > 0 || dir.Origin != ItemOrigin.Live;
    }

    private static void Flatten(RecoveredItem dir, List<RecoveredItem> into)
    {
        if (dir.Children == null) return;
        foreach (var c in dir.Children)
        {
            into.Add(c);
            if (c.IsDirectory) Flatten(c, into);
        }
    }

    // ───────────────────────────── FAT ─────────────────────────────

    public static ScanResult ScanFat(FatVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 0, Name = L.T("Raiz"), IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = FsDetect.Name(v.Kind), ClusterSize = v.ClusterSize,
        };
        if (v.UsedBackupBoot) res.Log.Add(L.T("Setor de boot principal danificado — usada a cópia do setor 6."));
        res.Allocation = new AllocationMap(v.DataOffset, v.ClusterSize, v.ClusterCount, c => v.IsAllocated(c + 2));

        long nextId = 1;
        var visited = new HashSet<long>();
        var queue = new Queue<(long cluster, bool deleted, RecoveredItem parent)>();
        queue.Enqueue((v.Kind == FsKind.Fat32 ? v.RootCluster : 0, false, res.Root));
        long dirsDone = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (cl, delDir, parent) = queue.Dequeue();
            if (cl != 0 && !visited.Add(cl)) continue;
            byte[] raw;
            try { raw = v.ReadDirectory(cl, delDir); }
            catch (MediaReadException) { res.Log.Add(L.T($"Pasta ilegível: {parent.FullPath}")); continue; }
            foreach (var e in v.ParseDirectory(raw))
            {
                bool deleted = e.Deleted || delDir;
                if (!deleted && !e.IsDir && !opt.IncludeLive) continue;
                var item = new RecoveredItem
                {
                    Id = nextId++, Name = e.Name, IsDirectory = e.IsDir,
                    Origin = deleted ? ItemOrigin.Deleted : ItemOrigin.Live,
                    Created = e.Created, Modified = e.Modified,
                    Note = e.NameGuessed ? L.T("1ª letra do nome perdida") : null,
                };
                if (e.IsDir)
                {
                    if (v.IsValidCluster(e.FirstCluster) && (!deleted || v.LooksLikeDirectory(e.FirstCluster)))
                        queue.Enqueue((e.FirstCluster, deleted, item));
                    else if (deleted) item.Note = L.T("conteúdo da pasta já sobrescrito");
                    item.Condition = deleted ? Condition.Good : Condition.Excellent;
                }
                else
                {
                    item.Size = e.Size;
                    long need = (e.Size + v.ClusterSize - 1) / v.ClusterSize;
                    List<long> clusters;
                    if (!deleted) clusters = v.Chain(e.FirstCluster, need);
                    else
                    {
                        // A FAT zerou a cadeia ao apagar: o melhor palpite é a sequência contígua.
                        clusters = [];
                        for (long c = 0; c < need && v.IsValidCluster(e.FirstCluster + c); c++) clusters.Add(e.FirstCluster + c);
                    }
                    if (e.Size > 0 && clusters.Count == 0) item.Condition = Condition.Overwritten;
                    else
                    {
                        item.Data = new ExtentSource(v.Dev, v.ClustersToExtents(clusters), e.Size);
                        if (!deleted) item.Condition = Condition.Excellent;
                        else
                        {
                            long alloc = clusters.Count(c => v.IsAllocated(c));
                            item.Condition = Evaluate(alloc, clusters.Count);
                            if (clusters.Count < need) item.Condition = Condition.Partial;
                        }
                    }
                }
                res.Attach(parent, item);
            }
            dirsDone++;
            progress?.Report(new(L.T("Lendo pastas"), Math.Min(0.99, dirsDone / (double)(dirsDone + queue.Count + 1)), res.Items.Count, dirsDone, dirsDone + queue.Count));
        }
        res.Label = v.Label;
        if (!string.IsNullOrEmpty(v.Label)) res.Root.Name = v.Label!;
        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        RecycleBin.Annotate(res.Root);
        return res;
    }

    // ───────────────────────────── ext2/3/4 ─────────────────────────────

    public static ScanResult ScanExt(ExtVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        string label = v.Info.Label;
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 2, Name = label.Length > 0 ? label : L.T("Raiz"), IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = v.Info.Variant, Label = label, ClusterSize = v.BlockSize,
        };
        if (v.UsedBackupSuperblock) res.Log.Add(L.T("Superbloco principal destruído — usada a cópia de reserva do grupo 1."));
        progress?.Report(new(L.T("Lendo o mapa de blocos"), 0, 0, 0, 1));
        byte[]? bmp = null;
        try { bmp = v.LoadBlockBitmap(); var b = bmp; res.Allocation = new AllocationMap(0, v.BlockSize, v.Info.Blocks, c => NtfsVolume.IsSet(b, c)); }
        catch (Exception ex) { res.Log.Add(L.T("Mapa de blocos ilegível: ") + ex.Message); }
        bool journalUsed = false;
        int fromJournal = 0, lost = 0;

        Condition Eval(ExtInode ino)
        {
            if (bmp == null) return Condition.Unknown;
            long alloc = 0, total = 0;
            foreach (var r in v.Map(ino))
            {
                long step = Math.Max(1, r.Length / 2048);
                for (long c = 0; c < r.Length; c += step) { total++; if (NtfsVolume.IsSet(bmp, r.Physical + c)) alloc++; }
            }
            return total == 0 ? Condition.Excellent : alloc == 0 ? Condition.Good : alloc >= total ? Condition.Overwritten : Condition.Partial;
        }

        var visited = new HashSet<uint> { 2 };
        var queue = new Queue<(ExtInode dir, RecoveredItem item, bool deleted)>();
        var rootIno = v.ReadInode(2);
        if (rootIno == null) { res.Log.Add(L.T("Inode raiz ilegível.")); return res; }
        queue.Enqueue((rootIno, res.Root, false));
        long dirs = 0, nextId = 3_000_000_000;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, parent, delDir) = queue.Dequeue();
            List<ExtVolume.DirEntry> entries;
            try { entries = v.ReadDirectory(dir, history: true); }
            catch (MediaReadException) { res.Log.Add(L.T($"Pasta ilegível: {parent.FullPath}")); continue; }
            foreach (var e in entries)
            {
                bool deleted = e.Deleted || delDir;
                var ino = v.ReadInode(e.Inode);
                if (ino == null) continue;
                bool reused = deleted && !ino.Deleted;     // o número do inode já é de outro arquivo
                bool isDir = e.Type == 2 || (e.Type == 0 && ino.IsDir);
                if (!deleted && !isDir && !opt.IncludeLive) continue;
                if (!deleted && ino.Deleted) continue;   // entrada viva apontando para inode morto: lixo
                if (!deleted && !(ino.IsDir || ino.IsFile)) continue;
                var item = new RecoveredItem { Id = nextId++, Name = e.Name, IsDirectory = isDir, Origin = deleted ? ItemOrigin.Deleted : ItemOrigin.Live };
                ExtInode? use = reused ? null : ino;
                if (deleted && (use == null || !use.HasMapping))
                {
                    // ext3/ext4 zeram o mapa ao apagar: a cópia antiga do inode pode estar no journal
                    var hist = v.HistoricInode(e.Inode, (ushort)(isDir ? 0x4000 : 0x8000));
                    journalUsed = true;
                    if (hist != null) { use = hist; fromJournal++; item.Note = L.T("mapa de blocos recuperado do journal"); }
                }
                if (use != null)
                {
                    item.Modified = ExtVolume.Time(use.Mtime);
                    item.Created = ExtVolume.Time(use.Crtime != 0 ? use.Crtime : use.Ctime);
                }
                if (isDir)
                {
                    item.Condition = deleted ? Condition.Good : Condition.Excellent;
                    if (use != null && use.HasMapping && visited.Add(e.Inode)) queue.Enqueue((use, item, deleted));
                    else if (deleted) item.Note = L.T("conteúdo da pasta perdido");
                }
                else if (use != null && use.HasMapping && use.IsFile)
                {
                    item.Data = v.DataSource(use);
                    item.Size = use.Size;
                    item.Condition = !deleted ? Condition.Excellent : Eval(use);
                }
                else
                {
                    if (!deleted) continue;
                    item.Size = use?.Size ?? 0;
                    item.Condition = Condition.Overwritten;
                    item.Note = reused ? L.T("inode já reaproveitado por outro arquivo") : L.T("mapa de blocos apagado e sem cópia no journal — tente a varredura profunda");
                    lost++;
                }
                res.Attach(parent, item);
            }
            dirs++;
            progress?.Report(new(L.T("Lendo pastas"), Math.Min(0.99, dirs / (double)(dirs + queue.Count + 1)), res.Items.Count, dirs, dirs + queue.Count));
        }
        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        if (!v.HasJournal && v.Info.Variant == "ext2") res.Log.Add(L.T("ext2 não tem journal: no Linux atual, nome e mapa de um arquivo apagado somem do disco — só a varredura profunda (por assinatura) acha o conteúdo."));
        if (journalUsed) res.Log.Add(L.T($"Journal: {v.JournalCopies:N0} cópias de blocos; {fromJournal} arquivo(s)/pasta(s) apagado(s) reconstruído(s) a partir dele."));
        if (lost > 0) res.Log.Add(L.T($"{lost} apagado(s) sem mapa de blocos recuperável (ext3/ext4 zeram o mapa; o journal já tinha sido reciclado)."));
        return res;
    }

    // ───────────────────────────── HFS+ ─────────────────────────────

    public static ScanResult ScanHfs(HfsVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 2, Name = v.Label.Length > 0 ? v.Label : L.T("Raiz"), IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = v.Variant, Label = v.Label, ClusterSize = v.BlockSize,
        };
        if (v.UsedAlternateHeader) res.Log.Add(L.T("Cabeçalho principal destruído — usada a cópia do fim do volume."));
        progress?.Report(new(L.T("Lendo o mapa de alocação"), 0, 0, 0, 1));
        byte[]? bmp = null;
        try { bmp = v.LoadAllocation(); var b = bmp; res.Allocation = new AllocationMap(0, v.BlockSize, v.TotalBlocks, c => HfsVolume.IsAllocated(b, c)); }
        catch (Exception ex) { res.Log.Add(L.T("Mapa de alocação ilegível: ") + ex.Message); }

        Condition Eval(List<Extent> ext)
        {
            if (bmp == null) return Condition.Unknown;
            long alloc = 0, total = 0;
            foreach (var e in ext)
            {
                long b0 = e.DeviceOffset / v.BlockSize, n = e.Length / v.BlockSize, step = Math.Max(1, n / 2048);
                for (long c = 0; c < n; c += step) { total++; if (HfsVolume.IsAllocated(bmp, b0 + c)) alloc++; }
            }
            return Evaluate(alloc, total);
        }

        // 1) a árvore viva, lida do catálogo inteiro de uma vez (as folhas em ordem)
        progress?.Report(new(L.T("Lendo o catálogo"), 0.05, 0, 0, 1));
        var live = new List<HfsRecord>();
        foreach (var r in v.LiveRecords()) { ct.ThrowIfCancellationRequested(); live.Add(r); }
        var folders = new Dictionary<uint, RecoveredItem> { [2] = res.Root };
        var liveFolderRecs = live.Where(r => r.IsFolder && r.Id != 2).ToDictionary(r => r.Id, r => r);
        var liveIds = new HashSet<uint>(live.Where(r => !r.IsThread).Select(r => r.Id));
        uint privateDir = 0;
        foreach (var r in liveFolderRecs.Values)
            if (r.ParentId == 2 && r.Name.StartsWith("\0\0\0\0HFS+ Private Data", StringComparison.Ordinal)) privateDir = r.Id;
        var inodes = live.Where(r => r.IsFile && r.ParentId == privateDir && privateDir != 0 && r.Name.StartsWith("iNode", StringComparison.Ordinal))
                         .ToDictionary(r => r.Name, r => r);
        static bool Hidden(HfsRecord r) => r.ParentId == 2 && (r.Name.StartsWith('\0') || r.Name.StartsWith(".HFS+ Private Directory Data", StringComparison.Ordinal)
                                                               || r.Name is ".journal" or ".journal_info_block");

        RecoveredItem LiveFolder(uint id, int depth = 0)
        {
            if (folders.TryGetValue(id, out var f)) return f;
            if (depth > 256 || !liveFolderRecs.TryGetValue(id, out var r)) return res.Root;
            var parent = LiveFolder(r.ParentId, depth + 1);
            f = new RecoveredItem { Id = r.Id, Name = r.Name, IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent,
                                    Created = HfsVolume.Time(r.Created), Modified = HfsVolume.Time(r.Modified) };
            folders[id] = f;
            res.Attach(parent, f);
            return f;
        }
        foreach (var r in liveFolderRecs.Values) if (!Hidden(r) && r.Id != privateDir) LiveFolder(r.Id);
        foreach (var r in live.Where(r => r.IsFile))
        {
            if (Hidden(r) || r.ParentId == privateDir || r.IsSymlink) continue;
            if (!opt.IncludeLive) break;
            var src = r;
            if (r.IsHardLink && inodes.TryGetValue("iNode" + r.Special, out var target)) src = target;
            var item = new RecoveredItem { Id = r.Id, Name = r.Name, Origin = ItemOrigin.Live, Condition = Condition.Excellent,
                                           Created = HfsVolume.Time(r.Created), Modified = HfsVolume.Time(src.Modified) };
            item.Data = v.DataSource(src, out var note, out var size);
            item.Size = size;
            item.Note = note;
            if (item.Data == null) item.Condition = Condition.Unknown;
            res.Attach(folders.TryGetValue(r.ParentId, out var pf) ? pf : res.Root, item);
        }

        // 2) os apagados: restos de registros nos nós livres, na sobra dos nós e no journal
        progress?.Report(new(L.T("Procurando registros apagados"), 0.4, res.Items.Count, 0, 1));
        var staleFiles = new Dictionary<uint, (HfsRecord rec, string where)>();
        var staleFolders = new Dictionary<uint, HfsRecord>();
        var threads = new Dictionary<uint, HfsRecord>();
        int fromJournal = 0;
        foreach (var (r, where) in v.StaleRecords(ct))
        {
            if (liveIds.Contains(r.Id)) continue;
            if (r.IsThread) { threads.TryAdd(r.Id, r); continue; }
            if (r.IsFolder) { staleFolders.TryAdd(r.Id, r); continue; }
            if (r.IsSymlink || r.ParentId == privateDir) continue;
            // a mesma versão pode aparecer várias vezes: fica a que ainda tem pedaços e é mais nova
            if (!staleFiles.TryGetValue(r.Id, out var cur)
                || (cur.rec.Data!.Extents.Length == 0 && r.Data!.Extents.Length > 0)
                || (r.Data!.Extents.Length > 0 && r.Modified > cur.rec.Modified))
                staleFiles[r.Id] = (r, where);
        }

        var deletedFolders = new Dictionary<uint, RecoveredItem>();
        RecoveredItem? lostBucket = null;
        RecoveredItem DeletedFolder(uint id, int depth = 0)
        {
            if (folders.TryGetValue(id, out var f) || deletedFolders.TryGetValue(id, out f)) return f;
            string? name = null; uint parent = 0; HfsRecord? fr = null;
            if (staleFolders.TryGetValue(id, out fr)) { name = fr.Name; parent = fr.ParentId; }
            else if (threads.TryGetValue(id, out var th)) { name = th.Name; parent = th.ParentId; }
            if (name == null || depth > 256)
            {
                lostBucket ??= res.GetOrAddFolder(res.Root, L.T("Pastas perdidas"), L.T("pastas apagadas cujo nome não sobrou no catálogo"));
                f = new RecoveredItem { Id = res.NewId(), Name = L.T($"Pasta {id}"), IsDirectory = true, Origin = ItemOrigin.Deleted, Condition = Condition.Good, Note = L.T("nome original perdido") };
                deletedFolders[id] = f;
                res.Attach(lostBucket, f);
                return f;
            }
            var p = DeletedFolder(parent, depth + 1);
            f = new RecoveredItem { Id = res.NewId(), Name = name, IsDirectory = true, Origin = ItemOrigin.Deleted, Condition = Condition.Good,
                                    Created = fr == null ? null : HfsVolume.Time(fr.Created), Modified = fr == null ? null : HfsVolume.Time(fr.Modified) };
            deletedFolders[id] = f;
            res.Attach(p, f);
            return f;
        }

        int lost = 0;
        foreach (var (r, where) in staleFiles.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (r.ParentId == 2 && r.Name.StartsWith('\0')) continue;
            var parent = DeletedFolder(r.ParentId);
            var item = new RecoveredItem { Id = res.NewId(), Name = r.Name, Origin = ItemOrigin.Deleted,
                                           Created = HfsVolume.Time(r.Created), Modified = HfsVolume.Time(r.Modified), Size = r.Data!.LogicalSize };
            if (where == "journal") fromJournal++;
            var ext = v.Map(r.Id, r.Data, 0);
            long have = ext.Sum(e => e.Length);
            if (r.Data.LogicalSize == 0 && r.Data.TotalBlocks == 0 && !r.Compressed)
            {
                // só sobrou o registro de quando o arquivo foi criado, antes de receber os dados
                item.Condition = Condition.Overwritten;
                item.Note = L.T("só sobrou o registro de antes da gravação (tamanho desconhecido) — tente a varredura profunda");
                lost++;
            }
            else if (r.Data.LogicalSize > 0 && ext.Count == 0)
            {
                item.Condition = Condition.Overwritten;
                item.Note = L.T("sem a lista de pedaços — tente a varredura profunda");
                lost++;
            }
            else
            {
                item.Data = new ExtentSource(v.Dev, ext, r.Data.LogicalSize);
                item.Condition = Eval(ext);
                if (have < r.Data.LogicalSize) { item.Condition = Condition.Partial; item.Note = L.T("só os 8 primeiros pedaços sobreviveram (o resto estava no arquivo de extents)"); }
                else if (r.Compressed) item.Note = L.T("estava comprimido pelo macOS — o conteúdo pode sair comprimido");
            }
            res.Attach(parent, item);
        }
        foreach (var id in staleFolders.Keys) if (!folders.ContainsKey(id)) DeletedFolder(id);

        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        if (fromJournal > 0) res.Log.Add(L.T($"Journal: {fromJournal} arquivo(s) apagado(s) reconstruído(s) a partir das cópias antigas do catálogo."));
        if (!v.Journaled) res.Log.Add(L.T("Volume sem journal: só sobram registros apagados que ficaram na sobra dos nós do catálogo — a varredura profunda acha o resto pelo conteúdo."));
        if (lost > 0) res.Log.Add(L.T($"{lost} apagado(s) sem lista de pedaços."));
        return res;
    }

    // ───────────────────────────── APFS ─────────────────────────────

    public static ScanResult ScanApfs(ApfsContainer c, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var usable = c.Volumes.Where(v => !v.Sealed && !v.Encrypted).ToList();
        bool many = c.Volumes.Count > 1;
        string label = string.Join(" + ", c.Volumes.Where(v => v.Role is 0 or 0x2 or 0x40).Select(v => v.Name).DefaultIfEmpty(c.Volumes[0].Name));
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 2, Name = many ? L.T("Contêiner APFS") : (c.Volumes[0].Name.Length > 0 ? c.Volumes[0].Name : L.T("Raiz")), IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = "APFS", Label = label, ClusterSize = c.BlockSize,
        };
        if (c.UsedOlderSuperblock) res.Log.Add(L.T("O ponto de verificação mais novo estava incompleto — usado o anterior."));
        foreach (var v in c.Volumes.Where(v => v.Encrypted))
            res.Log.Add(L.T($"Volume \"{v.Name}\" é criptografado (FileVault / APFS criptografado): sem a senha os arquivos não podem ser lidos."));
        foreach (var v in c.Volumes.Where(v => v.Sealed && !v.Encrypted))
            res.Log.Add(L.T($"Volume \"{v.Name}\" é o sistema do macOS (selado, só arquivos do sistema) — pulado."));

        // mapa dos blocos ocupados por arquivos vivos: base da condição dos apagados e da escavação
        var used = new byte[(c.BlockCount + 7) / 8];
        static void Mark(byte[] bmp, ulong start, ulong n) { for (ulong i = start; i < start + n && i / 8 < (ulong)bmp.Length; i++) bmp[i / 8] |= (byte)(1 << (int)(i & 7)); }
        res.Allocation = new AllocationMap(0, c.BlockSize, (long)c.BlockCount, b => NtfsVolume.IsSet(used, b));

        int vi = 0;
        foreach (var vol in usable)
        {
            ct.ThrowIfCancellationRequested();
            var top = many ? new RecoveredItem { Id = res.NewId(), Name = vol.Name + (vol.RoleName.Length > 0 ? $" ({vol.RoleName})" : ""), IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent } : res.Root;
            if (many) res.Attach(res.Root, top);
            double baseF = (double)vi / usable.Count, span = 1.0 / usable.Count;
            vi++;
            progress?.Report(new(L.T($"Lendo a árvore de {vol.Name}"), baseF, res.Items.Count, 0, 1));
            ApfsRecords live;
            try { live = c.ReadLive(vol, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { res.Log.Add(L.T($"Volume \"{vol.Name}\": árvore de arquivos ilegível ({ex.Message}) — tente a varredura profunda.")); continue; }
            foreach (var m in live.Extents.Values) foreach (var (phys, len, _) in m.Values) if (phys != 0) Mark(used, phys, (len + c.BlockSize - 1) / c.BlockSize);

            IDataSource? Source(ApfsRecords recs, ApfsInode ino, out string? note, out long size)
            {
                note = null; size = ino.Size;
                if (ino.Compressed && recs.Xattrs.TryGetValue((ino.Id, "com.apple.decmpfs"), out var dx))
                {
                    IDataSource? rsrc = null;
                    if (recs.Xattrs.TryGetValue((ino.Id, "com.apple.ResourceFork"), out var rx))
                    {
                        if ((rx.flags & 1) != 0 && rx.data.Length >= 16)
                            rsrc = c.DataSource(recs.Extents.GetValueOrDefault(U64(rx.data, 0)) ?? live.Extents.GetValueOrDefault(U64(rx.data, 0)), (long)U64(rx.data, 8));
                        else rsrc = new ResidentSource(rx.data);
                    }
                    var src = Decmpfs.Open(dx.data, rsrc, out note);
                    size = src?.Length ?? (dx.data.Length >= 16 ? (long)U64(dx.data, 8) : 0);
                    return src;
                }
                var ext = recs.Extents.GetValueOrDefault(ino.PrivateId) ?? live.Extents.GetValueOrDefault(ino.PrivateId);
                if (ext == null && size > 0) return null;
                return c.DataSource(ext, size);
            }

            // 1) árvore viva, da raiz (inode 2) pelas entradas de pasta
            var folders = new Dictionary<ulong, RecoveredItem> { [2] = top };
            var queue = new Queue<ulong>();
            queue.Enqueue(2);
            while (queue.Count > 0)
            {
                ulong dirId = queue.Dequeue();
                if (!live.Children.TryGetValue(dirId, out var kids)) continue;
                foreach (var (name, id, dt) in kids)
                {
                    if (!live.Inodes.TryGetValue(id, out var ino)) continue;
                    if (ino.IsDir || dt == 4)
                    {
                        if (folders.ContainsKey(id)) continue;
                        var f = new RecoveredItem { Id = res.NewId(), Name = name, IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent,
                                                    Created = ApfsContainer.Time(ino.Created), Modified = ApfsContainer.Time(ino.Modified) };
                        folders[id] = f;
                        res.Attach(folders[dirId], f);
                        queue.Enqueue(id);
                    }
                    else if (ino.IsFile && opt.IncludeLive)
                    {
                        var item = new RecoveredItem { Id = res.NewId(), Name = name, Origin = ItemOrigin.Live, Condition = Condition.Excellent,
                                                       Created = ApfsContainer.Time(ino.Created), Modified = ApfsContainer.Time(ino.Modified) };
                        item.Data = Source(live, ino, out var note, out var size);
                        item.Size = size; item.Note = note;
                        if (item.Data == null) item.Condition = Condition.Unknown;
                        res.Attach(folders[dirId], item);
                    }
                }
            }

            // 2) apagados: as árvores dos pontos de verificação anteriores e, na profunda, nós soltos
            progress?.Report(new(L.T($"Procurando apagados em {vol.Name}"), baseF + span * 0.3, res.Items.Count, 0, 1));
            var hist = c.ReadHistory(vol, ct, out int cps);
            if (opt.DeepScan) c.ScanOrphanNodes(vol, hist, progress, ct);
            var deleted = hist.Inodes.Values.Where(i => i.Id > 15 && !live.Inodes.ContainsKey(i.Id) && (i.IsDir || i.IsFile)).ToDictionary(i => i.Id);
            string? NameOf(ApfsInode i)
            {
                if (i.Name != null) return i.Name;
                if (hist.Children.TryGetValue(i.ParentId, out var l)) foreach (var e in l) if (e.id == i.Id) return e.name;
                return null;
            }
            var delFolders = new Dictionary<ulong, RecoveredItem>();
            RecoveredItem? lostBucket = null;
            RecoveredItem Folder(ulong id, int depth = 0)
            {
                if (folders.TryGetValue(id, out var f) || delFolders.TryGetValue(id, out f)) return f;
                if (depth < 256 && deleted.TryGetValue(id, out var di) && di.IsDir)
                {
                    var p = Folder(di.ParentId, depth + 1);
                    f = new RecoveredItem { Id = res.NewId(), Name = NameOf(di) ?? L.T($"Pasta {id}"), IsDirectory = true, Origin = ItemOrigin.Deleted, Condition = Condition.Good,
                                            Created = ApfsContainer.Time(di.Created), Modified = ApfsContainer.Time(di.Modified) };
                    delFolders[id] = f;
                    res.Attach(p, f);
                    return f;
                }
                lostBucket ??= res.GetOrAddFolder(top, L.T("Pastas perdidas"), L.T("a pasta original não aparece em nenhuma versão encontrada"));
                f = new RecoveredItem { Id = res.NewId(), Name = L.T($"Pasta {id}"), IsDirectory = true, Origin = ItemOrigin.Deleted, Condition = Condition.Good, Note = L.T("nome original perdido") };
                delFolders[id] = f;
                res.Attach(lostBucket, f);
                return f;
            }
            int found = 0, lost = 0;
            foreach (var ino in deleted.Values.Where(i => i.IsDir)) Folder(ino.Id);
            foreach (var ino in deleted.Values.Where(i => i.IsFile))
            {
                ct.ThrowIfCancellationRequested();
                var item = new RecoveredItem { Id = res.NewId(), Name = NameOf(ino) ?? L.T($"arquivo {ino.Id}"), Origin = ItemOrigin.Deleted,
                                               Created = ApfsContainer.Time(ino.Created), Modified = ApfsContainer.Time(ino.Modified) };
                var ext = hist.Extents.GetValueOrDefault(ino.PrivateId);
                item.Data = Source(hist, ino, out var note, out var size);
                item.Size = size; item.Note = note;
                if (item.Data == null)
                {
                    item.Condition = Condition.Overwritten;
                    item.Note ??= L.T("a lista de pedaços não sobrou — tente a varredura profunda");
                    lost++;
                }
                else
                {
                    // conflito com arquivo vivo = sobrescrito; bloco que virou metadado do APFS também
                    long total = 0, hit = 0;
                    bool meta = false;
                    if (ext != null)
                        foreach (var (phys, len, _) in ext.Values)
                        {
                            if (phys == 0) continue;
                            ulong n = (len + c.BlockSize - 1) / c.BlockSize, step = Math.Max(1, n / 512);
                            for (ulong b = 0; b < n; b += step) { total++; if (NtfsVolume.IsSet(used, (long)(phys + b))) hit++; }
                            if (!meta)
                                try { var first = c.ReadBlock(phys); meta = ApfsContainer.ChecksumOk(first) && (U32(first, 24) & 0xFFFF) is > 0 and < 0x30; }
                                catch (MediaReadException) { }
                        }
                    item.Condition = meta && hit == 0 ? Condition.Partial : Evaluate(hit, total);
                    if (meta) item.Note ??= L.T("parte dos blocos já virou metadado do APFS");
                    found++;
                }
                res.Attach(Folder(ino.ParentId), item);
            }
            res.Log.Add(L.T($"{vol.Name}: {live.Inodes.Count:N0} objetos na árvore atual; {cps} ponto(s) de verificação anterior(es) legível(is); {found} apagado(s) com conteúdo") + (lost > 0 ? L.T($", {lost} sem pedaços") : "") + ".");
        }
        if (usable.Count == 0 && c.Volumes.Count > 0) res.Log.Add(L.T("Nenhum volume legível sem senha — a varredura profunda ainda acha arquivos pelo conteúdo se o volume não for criptografado."));
        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        return res;
    }

    // ───────────────────────────── exFAT ─────────────────────────────

    public static ScanResult ScanExFat(ExFatVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 0, Name = L.T("Raiz"), IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = "exFAT", ClusterSize = v.ClusterSize,
        };
        if (v.UsedBackupBoot) res.Log.Add(L.T("Setor de boot principal danificado — usada a região de boot de reserva."));
        long nextId = 1;
        var visited = new HashSet<long>();
        var queue = new Queue<(long cluster, long size, bool noChain, bool deleted, RecoveredItem parent, bool root)>();
        queue.Enqueue((v.RootCluster, 0, false, false, res.Root, true));
        long dirsDone = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (cl, size, noChain, delDir, parent, root) = queue.Dequeue();
            if (!visited.Add(cl)) continue;
            byte[] raw;
            try { raw = v.ReadDirectory(cl, size, noChain); }
            catch (MediaReadException) { res.Log.Add(L.T($"Pasta ilegível: {parent.FullPath}")); continue; }
            var entries = v.ParseDirectory(raw, root);
            if (root && v.Bitmap != null)
                res.Allocation = new AllocationMap(v.HeapOffset, v.ClusterSize, v.ClusterCount, c => v.IsAllocated(c + 2));
            foreach (var e in entries)
            {
                bool deleted = e.Deleted || delDir;
                if (!deleted && !e.IsDir && !opt.IncludeLive) continue;
                var item = new RecoveredItem
                {
                    Id = nextId++, Name = e.Name, IsDirectory = e.IsDir,
                    Origin = deleted ? ItemOrigin.Deleted : ItemOrigin.Live,
                    Created = e.Created, Modified = e.Modified,
                };
                long need = (e.Size + v.ClusterSize - 1) / v.ClusterSize;
                if (e.IsDir)
                {
                    if (v.IsValidCluster(e.FirstCluster)) queue.Enqueue((e.FirstCluster, e.Size, e.NoFatChain, deleted, item, false));
                    item.Condition = deleted ? Condition.Good : Condition.Excellent;
                }
                else
                {
                    item.Size = e.Size;
                    var clusters = v.Chain(e.FirstCluster, need, e.NoFatChain);
                    if (deleted && !e.NoFatChain && clusters.Count < need)
                        clusters = v.Chain(e.FirstCluster, need, true); // cadeia desfeita: tenta contíguo
                    if (e.Size > 0 && clusters.Count == 0) item.Condition = Condition.Overwritten;
                    else
                    {
                        item.Data = new ExtentSource(v.Dev, v.ClustersToExtents(clusters), e.Size);
                        item.Condition = !deleted ? Condition.Excellent
                            : v.Bitmap == null ? Condition.Unknown
                            : Evaluate(clusters.Count(c => v.IsAllocated(c)), clusters.Count);
                        if (deleted && e.NoFatChain && item.Condition == Condition.Good) item.Condition = Condition.Excellent;
                    }
                }
                res.Attach(parent, item);
            }
            dirsDone++;
            progress?.Report(new(L.T("Lendo pastas"), Math.Min(0.99, dirsDone / (double)(dirsDone + queue.Count + 1)), res.Items.Count, dirsDone, dirsDone + queue.Count));
        }
        res.Label = v.Label;
        if (!string.IsNullOrEmpty(v.Label)) res.Root.Name = v.Label!;
        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        RecycleBin.Annotate(res.Root);
        return res;
    }
}

/// <summary>
/// Arquivos na Lixeira do Windows viram $Rxxxxxx; o par $Ixxxxxx guarda caminho e data
/// originais. Mostrar o nome real é o que transforma "$RX3K9.docx" em "Contrato final.docx".
/// </summary>
public static class RecycleBin
{
    public static void Annotate(RecoveredItem root)
    {
        var stack = new Stack<RecoveredItem>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d.Children == null) continue;
            var byName = d.Children.Where(c => !c.IsDirectory && c.Name.StartsWith("$I", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var i in byName)
            {
                var r = d.Children.FirstOrDefault(c => c.Name.Length == i.Name.Length && string.Equals(c.Name[2..], i.Name[2..], StringComparison.OrdinalIgnoreCase) && c.Name.StartsWith("$R", StringComparison.OrdinalIgnoreCase));
                if (r == null || i.Data == null || i.Size > 4096) continue;
                var buf = new byte[i.Size];
                try { i.Data.Read(0, buf); } catch { continue; }
                var path = Parse(buf);
                if (path == null) continue;
                r.Note = L.T("da Lixeira — original: ") + path;
                string orig = Path.GetFileName(path.Replace('\\', '/'));
                if (orig.Length > 0) r.Name = orig + "  [" + r.Name + "]";
            }
            foreach (var c in d.Children) if (c.IsDirectory) stack.Push(c);
        }
    }

    public static string? Parse(byte[] b)
    {
        if (b.Length < 24) return null;
        long ver = BitConverter.ToInt64(b, 0);
        if (ver == 2 && b.Length >= 28)
        {
            int len = BitConverter.ToInt32(b, 24);
            if (len <= 0 || 28 + len * 2 > b.Length) return null;
            return System.Text.Encoding.Unicode.GetString(b, 28, len * 2).TrimEnd('\0');
        }
        if (ver == 1 && b.Length >= 24 + 520)
            return System.Text.Encoding.Unicode.GetString(b, 24, 520).Split('\0')[0];
        return null;
    }
}
