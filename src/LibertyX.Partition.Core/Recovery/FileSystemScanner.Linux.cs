using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;

namespace LibertyX.Partition.Core.Recovery;

public static partial class FileSystemScanner
{
    // ───────────────────────────── XFS ─────────────────────────────

    /// <summary>
    /// XFS: a árvore viva pelos diretórios; os apagados pelos restos de entrada nos blocos de pasta
    /// (nome e inode). O XFS atual zera o mapa de blocos do inode ao apagar — o mapa antigo vem do
    /// log, enquanto ele não foi reciclado; sem ele, sobra o nome e a varredura profunda acha o conteúdo.
    /// </summary>
    public static ScanResult ScanXfs(XfsVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = (long)v.RootIno, Name = v.Label.Length > 0 ? v.Label : "Raiz", IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = v.V5 ? "XFS v5" : "XFS", Label = v.Label, ClusterSize = v.BlockSize,
        };
        if (v.UsedSecondarySuperblock) res.Log.Add("Superbloco principal destruído — usada a cópia de um grupo de alocação.");
        progress?.Report(new("Lendo o mapa de espaço livre", 0, 0, 0, 1));
        byte[]? free = null;
        try
        {
            free = v.FreeBitmap();
            var f = free;
            res.Allocation = new AllocationMap(0, v.BlockSize, (long)v.AgCount * v.AgBlocks, c => !XfsVolume.IsFree(f, c));
        }
        catch (Exception ex) { res.Log.Add("Mapa de espaço livre ilegível: " + ex.Message); }
        progress?.Report(new("Lendo o log", 0.02, 0, 0, 1));
        Dictionary<ulong, XfsVolume.Historic> hist = [];
        try { hist = v.HistoricInodes(); } catch (Exception ex) { res.Log.Add("Log ilegível: " + ex.Message); }

        Condition Eval(List<XfsVolume.Bmbt> ext)
        {
            if (free == null) return Condition.Unknown;
            long freeN = 0, total = 0;
            foreach (var e in ext)
            {
                long b0 = v.Linear(e.Block);
                long step = Math.Max(1, (long)e.Count / 2048);
                for (long k = 0; k < (long)e.Count; k += step) { total++; if (XfsVolume.IsFree(free, b0 + k)) freeN++; }
            }
            return total == 0 ? Condition.Excellent : freeN == total ? Condition.Good : freeN == 0 ? Condition.Overwritten : Condition.Partial;
        }

        var root = v.ReadInode(v.RootIno);
        if (root == null || !root.IsDir) { res.Log.Add("Inode raiz ilegível."); return res; }
        var visited = new HashSet<ulong> { v.RootIno };
        var queue = new Queue<(XfsInode dir, RecoveredItem item, bool deleted)>();
        queue.Enqueue((root, res.Root, false));
        long dirs = 0, fromLog = 0, nameOnly = 0, nextId = 4_000_000_000;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, parent, delDir) = queue.Dequeue();
            List<XfsVolume.DirEntry> entries;
            try { entries = v.ReadDirectory(dir, history: true); }
            catch (MediaReadException) { res.Log.Add($"Pasta ilegível: {parent.FullPath}"); continue; }
            // pasta pequena: os nomes apagados não ficam no disco, mas as versões antigas dela, no log
            if (v.HistoricDirForks.TryGetValue(dir.Ino, out var olds))
                foreach (var fork in olds)
                {
                    var old = new List<XfsVolume.DirEntry>();
                    try { v.ParseShortform(fork, old); } catch { continue; }
                    foreach (var o in old)
                        if (!entries.Any(x => x.Name == o.Name && (x.Ino == o.Ino || !x.Deleted)))
                            entries.Add(o with { Deleted = true });
                }
            // a mesma entrada apagada pode sobrar em mais de um trecho livre: uma vez só
            var seen = new HashSet<(string, ulong)>();
            foreach (var e in entries)
            {
                bool deleted = e.Deleted || delDir;
                if (e.Deleted && !seen.Add((e.Name, e.Ino))) continue;
                if (e.Deleted && entries.Any(x => !x.Deleted && x.Name == e.Name)) continue;   // apagado e recriado com o mesmo nome
                var ino = v.ReadInode(e.Ino);
                bool isDir = e.FType == 2 || (ino != null && !ino.Free && ino.IsDir);
                if (!deleted && !opt.IncludeLive && !isDir) continue;
                if (!deleted && (ino == null || ino.Free)) continue;
                // o tipo de um apagado sem ftype confiável vem da cópia do inode no log
                if (deleted && (ino == null || ino.Free) && hist.TryGetValue(e.Ino, out var hh) && (hh.Mode & 0xF000) == 0x4000) isDir = true;
                var item = new RecoveredItem { Id = nextId++, Name = e.Name, IsDirectory = isDir, Origin = deleted ? ItemOrigin.Deleted : ItemOrigin.Live };
                if (!deleted)
                {
                    item.Modified = ino!.Mtime;
                    item.Created = ino.Crtime ?? ino.Ctime;
                    if (ino.IsDir)
                    {
                        item.Condition = Condition.Excellent;
                        if (visited.Add(ino.Ino)) queue.Enqueue((ino, item, false));
                    }
                    else if (ino.IsFile)
                    {
                        item.Data = v.DataSource(ino);
                        item.Size = (long)ino.Size;
                        item.Condition = Condition.Excellent;
                    }
                    else continue;   // links, dispositivos, FIFOs
                    res.Attach(parent, item);
                    continue;
                }
                // apagado: o inode ainda é este arquivo? (o XFS zera o mapa ao liberar) — senão, o log
                bool reused = ino != null && !ino.Free && !delDir;
                if (!reused && hist.TryGetValue(e.Ino, out var h) && (h.Mode & 0xF000) == (isDir ? 0x4000 : 0x8000))
                {
                    item.Modified = h.Mtime;
                    item.Note = "mapa de blocos recuperado do log";
                    fromLog++;
                    var fake = v.ParseInode(BuildInode(v, h), h.Ino);
                    if (isDir && fake != null)
                    {
                        item.Condition = Condition.Good;
                        if (visited.Add(h.Ino)) queue.Enqueue((fake, item, true));
                    }
                    else if (fake != null)
                    {
                        item.Data = v.DataSource(fake, h.Extents);
                        item.Size = (long)h.Size;
                        item.Condition = Eval(h.Extents);
                        if (h.Stale && item.Condition is Condition.Excellent or Condition.Good)
                        {
                            // o arquivo mudou de mapa depois da versão usada: parte pode não ser mais dele
                            item.Condition = Condition.Partial;
                            item.Note = "mapa de blocos do log, mas o arquivo mudou depois dele (trechos soltos ou reescritos): confira o conteúdo";
                        }
                    }
                }
                else if (isDir)
                {
                    item.Condition = Condition.Good;
                    item.Note = "pasta apagada: o conteúdo dela aparece na varredura profunda";
                }
                else
                {
                    item.Condition = Condition.Overwritten;
                    item.Note = reused ? "inode já reaproveitado por outro arquivo" : "nome achado; o XFS apaga o mapa de blocos — o conteúdo aparece na varredura profunda";
                    nameOnly++;
                }
                res.Attach(parent, item);
            }
            dirs++;
            progress?.Report(new("Lendo pastas", Math.Min(0.99, dirs / (double)(dirs + queue.Count + 1)), res.Items.Count, dirs, dirs + queue.Count));
        }
        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        res.Log.Add($"Log do XFS: {v.LogRecords:N0} registros; {hist.Count:N0} inodes com mapa de blocos; {fromLog} apagado(s) reconstruído(s) a partir dele.");
        if (nameOnly > 0) res.Log.Add($"{nameOnly} apagado(s) só com o nome: o mapa de blocos já saiu do log. Use a varredura profunda (ela lê só o espaço livre).");
        return res;
    }

    /// <summary>Um inode "de mentira" com o garfo do log, para reaproveitar a leitura de extents e de pastas.</summary>
    private static byte[] BuildInode(XfsVolume v, XfsVolume.Historic h)
    {
        var b = new byte[v.InodeSize];
        b[0] = (byte)'I'; b[1] = (byte)'N';
        b[2] = (byte)(h.Mode >> 8); b[3] = (byte)h.Mode;
        b[4] = 3; b[5] = h.Format == 1 ? (byte)1 : (byte)2;
        for (int i = 0; i < 8; i++) b[56 + i] = (byte)(h.Size >> (56 - 8 * i));
        if (h.Format == 1)
        {
            h.Fork.AsSpan(0, Math.Min(h.Fork.Length, v.InodeSize - 176)).CopyTo(b.AsSpan(176));
            return b;
        }
        int n = Math.Min(h.Extents.Count, (v.InodeSize - 176) / 16);
        b[76] = (byte)(n >> 24); b[77] = (byte)(n >> 16); b[78] = (byte)(n >> 8); b[79] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var e = h.Extents[i];
            ulong l0 = ((e.Unwritten ? 1UL : 0) << 63) | (e.Off << 9) | (e.Block >> 43);
            ulong l1 = (e.Block << 21) | (e.Count & 0x1FFFFF);
            for (int k = 0; k < 8; k++) { b[176 + i * 16 + k] = (byte)(l0 >> (56 - 8 * k)); b[176 + i * 16 + 8 + k] = (byte)(l1 >> (56 - 8 * k)); }
        }
        return b;
    }

    // ───────────────────────────── Btrfs ─────────────────────────────

    /// <summary>
    /// Btrfs: a árvore atual (com subvolumes e snapshots como pastas) e, para os apagados, as folhas
    /// antigas das árvores de arquivos que ainda estão no espaço de metadados — cópia na escrita.
    /// O que volta é conferido contra a árvore de extents atual (o espaço foi reaproveitado?) e,
    /// quando a folha de checksums antiga também sobrou, pelo checksum de cada setor.
    /// </summary>
    public static ScanResult ScanBtrfs(BtrfsVolume v, ScanOptions opt, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var res = new ScanResult
        {
            Root = new RecoveredItem { Id = 256, Name = v.Label.Length > 0 ? v.Label : "Raiz", IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent },
            FileSystem = "Btrfs", Label = v.Label, ClusterSize = v.SectorSize,
        };
        if (v.UsedMirrorSuperblock) res.Log.Add("Superbloco principal destruído — usada a cópia de 64 MiB.");
        progress?.Report(new("Lendo as árvores", 0, 0, 0, 1));
        var roots = v.Roots();
        var live = new Dictionary<ulong, BtrfsVolume.FsTree>();
        foreach (var r in roots) { ct.ThrowIfCancellationRequested(); live[r.Id] = v.LoadTree(r); }

        // espaço em uso (árvore de extents atual), para o estado dos apagados e a varredura profunda
        var alloc = v.AllocatedLogical();
        bool Overlaps(ulong start, ulong len)
        {
            int lo = 0, hi = alloc.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var (s0, l0) = alloc[mid];
                if (s0 + l0 <= start) lo = mid + 1;
                else if (s0 >= start + len) hi = mid - 1;
                else return true;
            }
            return false;
        }
        long sectors = v.Dev.Length / v.SectorSize;
        var used = new byte[(sectors + 7) / 8];
        void Mark(long physByte, long len) { for (long sct = physByte / v.SectorSize; sct < (physByte + len + v.SectorSize - 1) / v.SectorSize && sct < sectors; sct++) used[sct >> 3] |= (byte)(1 << (int)(sct & 7)); }
        foreach (var c in v.Chunks.Where(c => (c.Type & 0x1) == 0)) Mark((long)c.Physical, (long)c.Length);   // metadados/sistema: em uso
        foreach (var (s0, l0) in alloc) { long ph = v.Physical(s0); if (ph >= 0) Mark(ph, (long)l0); }
        Mark(0, 1 << 20);   // superbloco e área reservada
        res.Allocation = new AllocationMap(0, v.SectorSize, sectors, sct => (used[sct >> 3] & (1 << (int)(sct & 7))) != 0);

        // o passado
        var hist = new Dictionary<ulong, BtrfsVolume.FsTree>();
        var csums = new SortedDictionary<ulong, byte[]>();
        int leaves = 0;
        try
        {
            foreach (var leaf in v.StaleLeaves(new Progress<double>(x => progress?.Report(new("Procurando versões antigas da árvore", 0.05 + 0.6 * x, 0, 0, 1))), ct))
            {
                leaves++;
                if (leaf.Owner == 7)
                {
                    foreach (var (k, d) in BtrfsVolume.LeafItems(leaf.Node))
                        if (k.Type == 128 && k.ObjectId == 0xFFFFFFFFFFFFFFF6UL) csums[k.Offset] = d.ToArray();
                    continue;
                }
                if (!hist.TryGetValue(leaf.Owner, out var t)) hist[leaf.Owner] = t = new BtrfsVolume.FsTree { Id = leaf.Owner };
                foreach (var (k, d) in BtrfsVolume.LeafItems(leaf.Node)) BtrfsVolume.Absorb(t, k, d.Span, leaf.Gen);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { res.Log.Add("Leitura das folhas antigas interrompida: " + ex.Message); }

        // checksum de cada setor de um trecho no disco, se a folha (atual ou antiga) ainda existir
        bool? Verify(ulong bytenr, ulong len)
        {
            int ss = (int)v.SectorSize;
            int checkedN = 0;
            for (ulong o = 0; o < len; o += (ulong)ss)
            {
                ulong at = bytenr + o;
                var kv = csums.LastOrDefault(x => x.Key <= at);
                if (kv.Value == null) return checkedN > 0 ? true : null;
                ulong idx = (at - kv.Key) / (ulong)ss;
                if ((idx + 1) * 4 > (ulong)kv.Value.Length) return checkedN > 0 ? true : null;
                long ph = v.Physical(at);
                if (ph < 0) return null;
                var data = v.Dev.ReadBytes(ph, ss);
                if (Bin.U32(kv.Value, (int)idx * 4) != BtrfsVolume.Crc32C(data)) return false;
                checkedN++;
            }
            return checkedN > 0 ? true : null;
        }

        long nextId = 5_000_000_000;
        int deletedFiles = 0, verified = 0;
        var visitedTrees = new HashSet<ulong>();

        void Walk(ulong treeId, ulong dirIno, RecoveredItem parent, bool parentDeleted, int depth)
        {
            if (depth > 64) return;
            live.TryGetValue(treeId, out var cur);
            hist.TryGetValue(treeId, out var old);
            var entries = new List<(BtrfsVolume.DirEnt e, bool deleted)>();
            if (cur != null && cur.Dirs.TryGetValue(dirIno, out var cl) && !parentDeleted) foreach (var e in cl) entries.Add((e, false));
            if (old != null && old.Dirs.TryGetValue(dirIno, out var ol))
                foreach (var e in ol)
                {
                    if (entries.Any(x => x.e.Name == e.Name && x.e.Child == e.Child)) continue;
                    // inode ainda vivo com outro nome: foi renomeado, não apagado (o Btrfs não reaproveita números de inode)
                    if (!parentDeleted && cur != null && e.ChildKeyType == 1 && cur.Inodes.ContainsKey(e.Child)) continue;
                    if (entries.Any(x => !x.deleted && x.e.Name == e.Name)) continue;
                    entries.Add((e, true));
                }
            foreach (var (e, del) in entries)
            {
                ct.ThrowIfCancellationRequested();
                bool deleted = del || parentDeleted;
                if (e.ChildKeyType == 132)
                {
                    // subvolume ou snapshot: continua na árvore dele
                    if (deleted || !visitedTrees.Add(e.Child)) continue;
                    var sub = new RecoveredItem { Id = nextId++, Name = e.Name, IsDirectory = true, Origin = ItemOrigin.Live, Condition = Condition.Excellent, Note = "subvolume" };
                    res.Attach(parent, sub);
                    Walk(e.Child, 256, sub, false, depth + 1);
                    continue;
                }
                BtrfsVolume.InodeItem? ino = null;
                if (!deleted && cur != null) cur.Inodes.TryGetValue(e.Child, out ino);
                if (ino == null && old != null) old.Inodes.TryGetValue(e.Child, out ino);
                bool isDir = e.FileType == 2 || ino?.IsDir == true;
                if (!deleted && !isDir && !opt.IncludeLive) continue;
                var item = new RecoveredItem { Id = nextId++, Name = e.Name, IsDirectory = isDir, Origin = deleted ? ItemOrigin.Deleted : ItemOrigin.Live, Modified = ino?.Mtime, Created = ino?.Otime ?? ino?.Ctime };
                if (isDir)
                {
                    item.Condition = deleted ? Condition.Good : Condition.Excellent;
                    res.Attach(parent, item);
                    Walk(treeId, e.Child, item, deleted, depth + 1);
                    continue;
                }
                if (e.FileType is not (0 or 1) || ino == null || !ino.IsFile)
                {
                    if (deleted && e.FileType == 1) { item.Condition = Condition.Overwritten; item.Note = "nome achado; o inode já não está em nenhuma folha antiga"; res.Attach(parent, item); }
                    continue;
                }
                SortedDictionary<ulong, BtrfsVolume.FileExtent>? ex = null;
                if (!deleted && cur != null) cur.Extents.TryGetValue(e.Child, out ex);
                if (ex == null && old != null) old.Extents.TryGetValue(e.Child, out ex);
                var list = ex?.Values.ToList() ?? [];
                item.Size = (long)ino.Size;
                item.Data = v.DataSource(ino, list);
                if (!deleted) item.Condition = Condition.Excellent;
                else
                {
                    deletedFiles++;
                    var regs = list.Where(x => x.Type != 0 && x.DiskBytenr != 0).ToList();
                    int hit = regs.Count(x => Overlaps(x.DiskBytenr, x.DiskNumBytes));
                    item.Condition = regs.Count == 0 ? (list.Count > 0 || ino.Size == 0 ? Condition.Good : Condition.Overwritten)
                        : hit == 0 ? Condition.Good : hit == regs.Count ? Condition.Overwritten : Condition.Partial;
                    if (regs.Count > 0 && item.Condition != Condition.Overwritten)
                    {
                        bool? ok = true;
                        foreach (var x in regs)
                        {
                            var r = Verify(x.DiskBytenr, x.DiskNumBytes);
                            if (r == false) { ok = false; break; }
                            if (r == null) ok = null;
                        }
                        if (ok == true) { item.Note = "conteúdo conferido pelo checksum do Btrfs"; verified++; }
                        else if (ok == false) { item.Condition = Condition.Overwritten; item.Note = "o espaço foi reaproveitado (checksum não confere)"; }
                    }
                    if (list.Count == 0 && ino.Size > 0) item.Note = "sem mapa de extents nas folhas antigas — tente a varredura profunda";
                }
                res.Attach(parent, item);
            }
        }

        visitedTrees.Add(5);
        Walk(5, 256, res.Root, false, 0);
        if (!opt.IncludeLive) { Prune(res.Root); res.Items.Clear(); Flatten(res.Root, res.Items); }
        res.Log.Add($"Btrfs geração {v.Generation:N0} · {roots.Count} árvore(s) de arquivos · {v.Chunks.Count} chunks · {leaves:N0} folhas válidas no espaço de metadados (atuais e antigas).");
        res.Log.Add($"{deletedFiles} apagado(s) achado(s) nas versões antigas da árvore; {verified} conferido(s) pelo checksum do próprio Btrfs.");
        return res;
    }
}
