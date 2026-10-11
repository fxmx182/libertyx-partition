using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Recovery;

/// <summary>
/// Varredura rápida: lê as estruturas do sistema de arquivos (MFT, diretórios FAT/exFAT)
/// e devolve a árvore de arquivos — os apagados com nome, pasta e datas originais.
/// </summary>
public static class FileSystemScanner
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
                default:
                    // Boot destruído? Tenta as cópias de reserva de cada formato.
                    var n2 = NtfsVolume.Open(vol, out _);
                    if (n2 != null) { r = ScanNtfs(n2, opt, progress, ct); r.Log.Add("Setor de boot destruído — usada a cópia de reserva do NTFS."); break; }
                    var f2 = FatVolume.Open(vol, out _);
                    if (f2 != null) { r = ScanFat(f2, opt, progress, ct); r.Log.Add("Setor de boot destruído — usada a cópia de reserva do FAT32."); break; }
                    var x2 = ExFatVolume.Open(vol, out _);
                    if (x2 != null) { r = ScanExFat(x2, opt, progress, ct); r.Log.Add("Setor de boot destruído — usada a cópia de reserva do exFAT."); break; }
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { err = ex.Message; r = null; }

        if (r == null)
        {
            r = new ScanResult { Root = new RecoveredItem { Id = 0, Name = "Volume", IsDirectory = true, Origin = ItemOrigin.Live } };
            r.FileSystem = FsDetect.Name(kind);
            r.Log.Add(err ?? $"Sistema de arquivos {FsDetect.Name(kind)} não é lido pela varredura rápida — use a varredura profunda.");
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
            Root = new RecoveredItem { Id = 5, Name = label.Length > 0 ? label : "Raiz", IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = "NTFS", Label = label, ClusterSize = v.ClusterSize,
        };
        if (v.UsedBackupBoot) res.Log.Add("Setor de boot principal danificado — usada a cópia do fim da partição.");

        progress?.Report(new("Lendo o mapa de clusters", 0, 0, 0, 1));
        byte[]? bmp = null;
        try { bmp = v.LoadBitmap(); } catch (Exception ex) { res.Log.Add("Mapa de clusters ($Bitmap) ilegível: " + ex.Message); }
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
                catch (MediaReadException) { got = 0; bad += n / rs; res.Log.Add($"Setores ilegíveis no MFT (registros {index}–{index + n / rs})."); }
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
                progress?.Report(new("Lendo a tabela de arquivos (MFT)", (double)index / total, nodes.Count, index, total));
            }
        }

        // Monta as pastas e os itens.
        var lost = new RecoveredItem { Id = -2, Name = "$Sem pasta (órfãos)", IsDirectory = true, Origin = ItemOrigin.Orphan, Note = "pasta original não existe mais", Condition = Condition.Unknown };
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
                if (n.Data.Encrypted) item.Note = "criptografado (EFS) — sem a chave o conteúdo sai ilegível";
                else if (n.Data.Compressed) item.Note = "comprimido pelo NTFS";
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
        if (bad > 0) res.Log.Add($"{bad} registros MFT ficaram em setores ilegíveis.");
        res.Log.Add($"MFT: {total:N0} registros lidos, {res.Items.Count(i => !i.IsDirectory):N0} arquivos listados.");
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
            Root = new RecoveredItem { Id = 0, Name = "Raiz", IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = FsDetect.Name(v.Kind), ClusterSize = v.ClusterSize,
        };
        if (v.UsedBackupBoot) res.Log.Add("Setor de boot principal danificado — usada a cópia do setor 6.");
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
            catch (MediaReadException) { res.Log.Add($"Pasta ilegível: {parent.FullPath}"); continue; }
            foreach (var e in v.ParseDirectory(raw))
            {
                bool deleted = e.Deleted || delDir;
                if (!deleted && !e.IsDir && !opt.IncludeLive) continue;
                var item = new RecoveredItem
                {
                    Id = nextId++, Name = e.Name, IsDirectory = e.IsDir,
                    Origin = deleted ? ItemOrigin.Deleted : ItemOrigin.Live,
                    Created = e.Created, Modified = e.Modified,
                    Note = e.NameGuessed ? "1ª letra do nome perdida" : null,
                };
                if (e.IsDir)
                {
                    if (v.IsValidCluster(e.FirstCluster) && (!deleted || v.LooksLikeDirectory(e.FirstCluster)))
                        queue.Enqueue((e.FirstCluster, deleted, item));
                    else if (deleted) item.Note = "conteúdo da pasta já sobrescrito";
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
            progress?.Report(new("Lendo pastas", Math.Min(0.99, dirsDone / (double)(dirsDone + queue.Count + 1)), res.Items.Count, dirsDone, dirsDone + queue.Count));
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
            Root = new RecoveredItem { Id = 2, Name = label.Length > 0 ? label : "Raiz", IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = v.Info.Variant, Label = label, ClusterSize = v.BlockSize,
        };
        if (v.UsedBackupSuperblock) res.Log.Add("Superbloco principal destruído — usada a cópia de reserva do grupo 1.");
        progress?.Report(new("Lendo o mapa de blocos", 0, 0, 0, 1));
        byte[]? bmp = null;
        try { bmp = v.LoadBlockBitmap(); var b = bmp; res.Allocation = new AllocationMap(0, v.BlockSize, v.Info.Blocks, c => NtfsVolume.IsSet(b, c)); }
        catch (Exception ex) { res.Log.Add("Mapa de blocos ilegível: " + ex.Message); }
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
        if (rootIno == null) { res.Log.Add("Inode raiz ilegível."); return res; }
        queue.Enqueue((rootIno, res.Root, false));
        long dirs = 0, nextId = 3_000_000_000;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, parent, delDir) = queue.Dequeue();
            List<ExtVolume.DirEntry> entries;
            try { entries = v.ReadDirectory(dir, history: true); }
            catch (MediaReadException) { res.Log.Add($"Pasta ilegível: {parent.FullPath}"); continue; }
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
                    if (hist != null) { use = hist; fromJournal++; item.Note = "mapa de blocos recuperado do journal"; }
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
                    else if (deleted) item.Note = "conteúdo da pasta perdido";
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
                    item.Note = reused ? "inode já reaproveitado por outro arquivo" : "mapa de blocos apagado e sem cópia no journal — tente a varredura profunda";
                    lost++;
                }
                res.Attach(parent, item);
            }
            dirs++;
            progress?.Report(new("Lendo pastas", Math.Min(0.99, dirs / (double)(dirs + queue.Count + 1)), res.Items.Count, dirs, dirs + queue.Count));
        }
        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        if (!v.HasJournal && v.Info.Variant == "ext2") res.Log.Add("ext2 não tem journal: no Linux atual, nome e mapa de um arquivo apagado somem do disco — só a varredura profunda (por assinatura) acha o conteúdo.");
        if (journalUsed) res.Log.Add($"Journal: {v.JournalCopies:N0} cópias de blocos; {fromJournal} arquivo(s)/pasta(s) apagado(s) reconstruído(s) a partir dele.");
        if (lost > 0) res.Log.Add($"{lost} apagado(s) sem mapa de blocos recuperável (ext3/ext4 zeram o mapa; o journal já tinha sido reciclado).");
        return res;
    }

    // ───────────────────────────── exFAT ─────────────────────────────

    public static ScanResult ScanExFat(ExFatVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 0, Name = "Raiz", IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = "exFAT", ClusterSize = v.ClusterSize,
        };
        if (v.UsedBackupBoot) res.Log.Add("Setor de boot principal danificado — usada a região de boot de reserva.");
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
            catch (MediaReadException) { res.Log.Add($"Pasta ilegível: {parent.FullPath}"); continue; }
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
            progress?.Report(new("Lendo pastas", Math.Min(0.99, dirsDone / (double)(dirsDone + queue.Count + 1)), res.Items.Count, dirsDone, dirsDone + queue.Count));
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
                r.Note = "da Lixeira — original: " + path;
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
