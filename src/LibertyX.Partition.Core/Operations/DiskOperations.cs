using System.Security.Cryptography;
using System.Text.Json;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;
using LibertyX.Partition.Core.Recovery;
using LibertyX.Partition.Core.Tools;

namespace LibertyX.Partition.Core.Operations;

/// <summary>
/// As operações de verdade — cada uma: confere, salva backup do que vai mudar, tranca os
/// volumes, escreve, pede ao sistema para reler. A tela só monta o pedido e mostra o progresso.
/// </summary>
public static class DiskOperations
{
    public static string Size(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = b; int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    /// <summary>O disco inteiro vai ser reescrito: nunca o do sistema em uso.</summary>
    private static void RequireModifiable(DiskInfo d)
    {
        if (d.IsSystem) throw new InvalidOperationException(OperatingSystem.IsWindows()
            ? "Este é o disco do Windows em uso. Para alterá-lo, use a mídia inicializável."
            : "Este é o disco do sistema em uso (onde está o /). Para alterá-lo inteiro, inicie por um pendrive Linux.");
        if (d.ReadOnly) throw new InvalidOperationException("Disco aberto só para leitura.");
        if (d.IsVirtual) throw new InvalidOperationException($"Volume {d.VirtualKind}: altere a partição física onde ele está.");
    }

    /// <summary>
    /// Só uma partição vai mudar. No Windows o disco do sistema continua travado inteiro; no
    /// Linux vale qualquer disco, desde que a própria partição não esteja em uso pelo sistema.
    /// </summary>
    private static void RequirePartition(PartitionInfo p)
    {
        if (OperatingSystem.IsWindows() || p.Disk.IsImage) { RequireModifiable(p.Disk); return; }
        if (p.Disk.ReadOnly) throw new InvalidOperationException("Disco só para leitura (mídia protegida contra gravação).");
        if (p.IsSystemCritical) throw new InvalidOperationException($"{p.KernelName ?? p.DisplayName} está em uso pelo sistema. Para alterá-la, inicie por um pendrive Linux.");
    }

    /// <summary>Linux: partições cuja entrada na tabela muda (são as únicas que precisam ser desmontadas).</summary>
    private static List<PartitionInfo>? Affected(DiskInfo d, PartitionTable table)
    {
        if (OperatingSystem.IsWindows() || d.IsImage) return null;
        return d.Partitions.Where(p => p.Entry != null && !table.Entries.Any(e => e.StartLba == p.Entry.StartLba && e.SectorCount == p.Entry.SectorCount)).ToList();
    }

    private static IProgress<CopyProgress> CopyReporter(OpSink sink, string label) => new Progress<CopyProgress>(p =>
        sink.Progress(p.Total == 0 ? 0 : (double)p.Done / p.Total, label,
            $"{Size(p.Done)} de {Size(p.Total)} · {Size((long)p.BytesPerSecond)}/s{(p.BadSectors > 0 ? $" · {p.BadSectors} setores ilegíveis" : "")}"));

    // ───────────── tabela ─────────────

    /// <param name="growInPlace">Linux: partição que só cresce para a direita e fica montada — o kernel ajusta o tamanho com ela em uso.</param>
    public static void WriteTable(DiskInfo d, PartitionTable table, string reason, OpSink sink, bool allowSystem = false, PartitionInfo? growInPlace = null)
    {
        if (!allowSystem)
        {
            if (OperatingSystem.IsWindows() || d.IsImage) RequireModifiable(d);
            else if (!d.PartitionsEditable) throw new InvalidOperationException(d.IsVirtual ? $"Volume {d.VirtualKind}: não tem tabela de partições." : "Disco só para leitura.");
        }
        var errs = PartitionCodec.Validate(table);
        if (errs.Count > 0) throw new InvalidOperationException(string.Join("\n", errs));
        using var dev = DiskSystem.OpenDisk(d, true);
        var footprint = PartitionCodec.FootprintForBackup(dev, d.Table);
        foreach (var (lba, _) in PartitionCodec.Build(table.Clone())) footprint.Add((lba, 1));
        string bak = TableBackup.Save(dev, footprint, $"{d.Model}-{d.Serial}", reason);
        sink.Log($"Backup dos setores da tabela salvo em {bak}");
        var affected = Affected(d, table);
        if (growInPlace != null && affected != null)
        {
            var e = table.Entries.FirstOrDefault(x => x.StartLba == growInPlace.Entry?.StartLba);
            if (e == null || e.SectorCount < growInPlace.Entry!.SectorCount) throw new InvalidOperationException("Crescer no lugar só vale para aumentar a partição mantendo o início.");
            affected.RemoveAll(x => x.Offset == growInPlace.Offset);
        }
        using (DiskSystem.Native.LockVolumes(d, affected))
        {
            PartitionCodec.Write(dev, table);
            sink.Log("Tabela de partições gravada.");
            DiskSystem.Native.Rescan(d);
        }
        DiskSystem.Native.Rescan(d);
    }

    public static void DeleteAllPartitions(DiskInfo d, OpSink sink)
    {
        RequireModifiable(d);
        var t = d.Table.Clone();
        if (t.Style == TableStyle.None) t.Style = TableStyle.Gpt;
        t.Entries.Clear();
        WriteTable(d, t, "Excluir todas as partições", sink);
        sink.Log("Todas as partições foram removidas da tabela. Os dados continuam no disco até serem sobrescritos — a Recuperação de partição consegue trazê-las de volta.");
    }

    public static void DeletePartitionRaw(PartitionInfo p, OpSink sink)
    {
        RequirePartition(p);
        var t = p.Disk.Table.Clone();
        t.Entries.RemoveAll(e => e.StartLba == p.Entry!.StartLba);
        WriteTable(p.Disk, t, "Excluir partição", sink);
    }

    /// <summary>Diz se dá para converter sem perder nada e por quê.</summary>
    public static (bool ok, List<string> notes) AnalyzeConversion(DiskInfo d, TableStyle target)
    {
        var notes = new List<string>();
        bool ok = true;
        var parts = d.Partitions.Where(p => p.Entry != null).ToList();
        if (d.Table.Style == target) { notes.Add("O disco já está nesse estilo."); return (false, notes); }
        if (target == TableStyle.Mbr)
        {
            if (d.Size > 2L * 1024 * 1024 * 1024 * 1024 && d.SectorSize == 512) notes.Add("Disco acima de 2 TiB: em MBR, o espaço além de 2 TiB fica inutilizável.");
            var kept = parts.Where(p => !p.IsMsr).ToList();
            if (parts.Any(p => p.IsMsr)) notes.Add("A partição reservada da Microsoft (MSR) será descartada — ela não existe em MBR e não guarda dados.");
            if (kept.Count > 4) { ok = false; notes.Add($"São {kept.Count} partições; MBR comporta 4 primárias."); }
            if (kept.Any(p => p.Entry!.EndLba > uint.MaxValue)) { ok = false; notes.Add("Há partição além do limite de 2 TiB do MBR."); }
            if (d.IsSystem) { ok = false; notes.Add("O Windows em uso inicia por UEFI/GPT; convertê-lo para MBR o deixaria sem inicializar."); }
        }
        else
        {
            var probe = new PartitionTable { Style = TableStyle.Gpt, SectorSize = d.SectorSize, DiskSectors = d.Size / d.SectorSize };
            long first = PartitionCodec.GptFirstUsable(probe), last = PartitionCodec.GptLastUsable(probe);
            var lo = parts.FirstOrDefault(p => p.Entry!.StartLba < first);
            var hi = parts.FirstOrDefault(p => p.Entry!.EndLba > last);
            if (lo != null) { ok = false; notes.Add($"A partição {lo.DisplayName} começa no setor {lo.Entry!.StartLba}; o GPT precisa dos primeiros {first} setores. Mova-a 1 MB para a direita antes."); }
            if (hi != null) { ok = false; notes.Add($"A partição {hi.DisplayName} vai até o fim do disco; o GPT precisa de {d.Size / d.SectorSize - last - 1} setores no fim. Reduza-a 1 MB antes."); }
            if (d.IsSystem) { ok = false; notes.Add("Para o disco do Windows, use o conversor oficial MBR2GPT (o assistente oferece) e mude o firmware para UEFI."); }
            if (parts.Any(p => p.Entry!.IsLogical)) notes.Add("As partições lógicas viram partições GPT normais.");
        }
        if (ok) notes.Add("Conversão sem perda de dados: só a tabela é reescrita; o conteúdo das partições não é tocado.");
        return (ok, notes);
    }

    public static void ConvertStyle(DiskInfo d, TableStyle target, OpSink sink)
    {
        var (ok, notes) = AnalyzeConversion(d, target);
        if (!ok) throw new InvalidOperationException(string.Join("\n", notes));
        var t = new PartitionTable { Style = target, SectorSize = d.SectorSize, DiskSectors = d.Size / d.SectorSize, BootCode = d.Table.BootCode, DiskSignature = d.Table.DiskSignature };
        foreach (var p in d.Partitions.Where(p => p.Entry != null).OrderBy(p => p.Offset))
        {
            var e = p.Entry!;
            if (target == TableStyle.Mbr)
            {
                var type = PartitionTypes.GptToMbr(e.TypeGuid, p.FileSystem);
                if (type == null) continue;
                t.Entries.Add(new PartitionEntry { StartLba = e.StartLba, SectorCount = e.SectorCount, MbrType = type.Value });
            }
            else
            {
                var (g, a) = PartitionTypes.MbrToGpt(e.MbrType);
                t.Entries.Add(new PartitionEntry { StartLba = e.StartLba, SectorCount = e.SectorCount, TypeGuid = g, Attributes = a, Name = p.Label ?? "", UniqueGuid = Guid.NewGuid() });
            }
        }
        WriteTable(d, t, $"Converter para {target}", sink);
        sink.Log($"Disco convertido para {(target == TableStyle.Gpt ? "GPT" : "MBR")}.");
    }

    // ───────────── mover / alinhar ─────────────

    public static void MovePartition(PartitionInfo p, long newStartLba, OpSink sink)
    {
        RequirePartition(p);
        if (p.Entry == null) throw new InvalidOperationException("Escolha uma partição.");
        var d = p.Disk;
        var e = p.Entry;
        if (newStartLba == e.StartLba) return;
        var t = d.Table.Clone();
        var me = t.Entries.First(x => x.StartLba == e.StartLba);
        long oldStart = me.StartLba;
        me.StartLba = newStartLba;
        if (t.Entries.Any(o => o != me && o.StartLba <= me.EndLba && me.StartLba <= o.EndLba))
            throw new InvalidOperationException("A nova posição invade outra partição.");
        long lo = t.Style == TableStyle.Gpt ? PartitionCodec.GptFirstUsable(t) : 1;
        long hi = t.Style == TableStyle.Gpt ? PartitionCodec.GptLastUsable(t) : t.DiskSectors - 1;
        if (me.StartLba < lo || me.EndLba > hi) throw new InvalidOperationException("A nova posição sai da área utilizável do disco.");
        if (me.IsLogical && me.EbrLba >= newStartLba) me.EbrLba = 0;

        using var dev = DiskSystem.OpenDisk(d, true);
        int ss = dev.SectorSize;
        string bak = TableBackup.Save(dev, PartitionCodec.FootprintForBackup(dev, d.Table), $"{d.Model}-{d.Serial}", "Mover partição");
        sink.Log($"Backup da tabela: {bak}");
        sink.Log("ATENÇÃO: não desligue o computador até o fim — a partição está sendo deslocada no lugar.");
        using (DiskSystem.Native.LockVolumes(d, [p]))
        {
            var src = new SubDevice(dev, oldStart * ss, e.SectorCount * ss);
            SectorCopier.Copy(src, dev, new CopyOptions { TargetOffset = newStartLba * ss, Backwards = newStartLba > oldStart },
                CopyReporter(sink, "Deslocando dados"), sink.Token, sink.PauseGate);
            BootSector.SetHiddenSectors(new SubDevice(dev, newStartLba * ss, e.SectorCount * ss), p.FileSystem, newStartLba);
            PartitionCodec.Write(dev, t);
            DiskSystem.Native.Rescan(d);
        }
        DiskSystem.Native.Rescan(d);
        sink.Log($"Partição movida do setor {oldStart:N0} para {newStartLba:N0}.");
    }

    public static bool IsAligned(PartitionInfo p) => p.Entry == null || (p.Offset % (1024 * 1024)) == 0;

    /// <summary>Próxima posição alinhada a 1 MiB que cabe: para a direita se houver folga, senão para a esquerda.</summary>
    public static long? AlignedTarget(PartitionInfo p)
    {
        var d = p.Disk;
        long a = d.Table.AlignSectors;
        long start = p.Entry!.StartLba, count = p.Entry.SectorCount;
        var others = d.Table.Entries.Where(x => x.StartLba != start).ToList();
        long hi = d.Table.Style == TableStyle.Gpt ? PartitionCodec.GptLastUsable(d.Table) : d.Table.DiskSectors - 1;
        long lo = d.Table.Style == TableStyle.Gpt ? PartitionCodec.GptFirstUsable(d.Table) : 1;
        bool Free(long s) => s >= lo && s + count - 1 <= hi && !others.Any(o => o.StartLba <= s + count - 1 && s <= o.EndLba);
        long up = (start + a - 1) / a * a, down = start / a * a;
        if (Free(up)) return up;
        if (down != start && Free(down)) return down;
        return null;
    }

    // ───────────── número de série / tipo ─────────────

    public static void ChangeSerial(PartitionInfo p, ulong serial, OpSink sink)
    {
        RequirePartition(p);
        using var dev = DiskSystem.OpenDisk(p.Disk, true);
        using (DiskSystem.Native.LockVolumes(p.Disk, [p]))
            BootSector.WriteSerial(new SubDevice(dev, p.Offset, p.Length), p.FileSystem, serial);
        sink.Log($"Número de série alterado para {BootSector.FormatSerial(serial, p.FileSystem)}.");
    }

    public static void ChangeTypeRaw(PartitionInfo p, Guid? gpt, byte? mbr, OpSink sink)
    {
        var t = p.Disk.Table.Clone();
        var e = t.Entries.First(x => x.StartLba == p.Entry!.StartLba);
        if (gpt != null) e.TypeGuid = gpt.Value;
        if (mbr != null) e.MbrType = mbr.Value;
        WriteTable(p.Disk, t, "Alterar ID de tipo", sink, allowSystem: !p.IsSystemCritical);
    }

    // ───────────── limpeza ─────────────

    /// <summary>Apaga um LUKS achado pelo detector: cabeçalhos e chaves, sem tocar no que é de hoje.</summary>
    public static void EraseLuks(DiskInfo d, LuksFinding f, OpSink sink)
    {
        if (d.ReadOnly) throw new InvalidOperationException("Imagem só para leitura.");
        using var dev = DiskSystem.OpenDisk(d, true);
        var layout = LuksFinder.Layout(d, dev.Length);
        var touched = d.Partitions.Where(p => !p.IsUnallocated && f.Regions().Any(r => r.off < p.Offset + p.Length && r.off + r.len > p.Offset)).ToList();
        if (touched.FirstOrDefault(p => p.IsSystemCritical) is { } crit && crit.Offset == f.Start)
            throw new InvalidOperationException($"{crit.DisplayName} está em uso pelo sistema.");
        sink.Progress(-1, "Apagando o LUKS", f.Where);
        LuksEraseResult res;
        using (DiskSystem.Native.LockVolumes(d, touched.Where(p => !p.IsSystemCritical)))
        {
            res = LuksFinder.Erase(dev, layout, f, a => LuksFinder.UsedRanges(dev.Slice(a.Offset, a.Length)), sink.Log, sink.Token);
        }
        DiskSystem.Native.Rescan(d);
        sink.Log($"Zerados {Size(res.Written)} (cabeçalhos e áreas das chaves).");
        foreach (var s in res.Skipped) sink.Log("Não tocado: " + s);
        if (res.After == null) sink.Log("Conferido: não sobrou cabeçalho nem material de chave nesse lugar.");
        else if (res.After.State == LuksState.Openable || res.After.IntactSlots > 0)
            throw new InvalidOperationException($"Ainda sobrou LUKS ({res.After.Explain()}). O que restou está em área usada pelo sistema de arquivos atual: para sumir com tudo, use Limpar disco nessa partição.");
        else sink.Log("Conferido: " + res.After.Explain());
    }

    public static void Wipe(DiskInfo d, PartitionInfo? p, WipeMethod method, OpSink sink)
    {
        if (p == null) RequireModifiable(d); else RequirePartition(p);
        if (p is { IsSystemCritical: true }) throw new InvalidOperationException("Partição do sistema em uso.");
        if (method is WipeMethod.Discard or WipeMethod.Sanitize)
        {
            if (!OperatingSystem.IsLinux() || d.IsImage) throw new InvalidOperationException("Disponível no Linux, em discos físicos.");
            using (DiskSystem.Native.LockVolumes(d, p == null ? null : [p]))
            {
                if (method == WipeMethod.Discard) Platform.Linux.LinuxErase.Discard(d, p, sink);
                else
                {
                    if (p != null) throw new InvalidOperationException("O apagamento do fabricante é sempre do disco inteiro.");
                    Platform.Linux.LinuxErase.Sanitize(d, sink).GetAwaiter().GetResult();
                }
            }
            DiskSystem.Native.Rescan(d);
            return;
        }
        using var dev = DiskSystem.OpenDisk(d, true);
        long start = p?.Offset ?? 0, len = p?.Length ?? dev.Length;
        using (DiskSystem.Native.LockVolumes(d, p == null ? null : [p]))
        {
            Wiper.Wipe(dev, start, len, method, new Progress<WipeProgress>(w =>
                sink.Progress((w.Pass - 1 + (double)w.Done / w.Total) / w.Passes, $"Passada {w.Pass} de {w.Passes}", $"{Size(w.Done)} de {Size(w.Total)} · {Size((long)w.BytesPerSecond)}/s")), sink.Token);
            DiskSystem.Native.Rescan(d);
        }
        DiskSystem.Native.Rescan(d);
        sink.Log(p == null ? "Disco limpo. Ele aparece agora como não inicializado." : "Partição limpa (o conteúdo foi sobrescrito; a partição continua na tabela).");
    }

    // ───────────── origem com VSS ─────────────

    /// <summary>
    /// Abre a origem de uma partição para cópia. Volume NTFS montado → instantâneo VSS (cópia
    /// consistente com o Windows rodando); o resto → leitura direta.
    /// </summary>
    public static async Task<(IBlockDevice dev, Func<Task> release)> OpenSource(PartitionInfo p, bool useVss, OpSink sink)
    {
        if (useVss && OperatingSystem.IsWindows() && !p.Disk.IsImage && p.Letter != null && p.FileSystem == FsKind.Ntfs && !p.BitLocker)
        {
            sink.Log($"Criando instantâneo VSS de {p.Letter}…");
            var shadow = await WinCommands.CreateShadow(p.Letter + "\\", sink.Token);
            if (shadow is { } s)
            {
                sink.Log("Instantâneo pronto: a cópia sai consistente mesmo com o Windows em uso.");
                // o tamanho vem da partição: nem todo Windows responde IOCTL_DISK_GET_LENGTH_INFO no instantâneo
                var dev = new WinDevice(s.device, false, p.Disk.SectorSize, p.Length, isVolume: true);
                return (dev, async () => { dev.Dispose(); await WinCommands.DeleteShadow(s.id); });
            }
        }
        var d = DiskSystem.OpenPartition(p);
        return (d, () => { d.Dispose(); return Task.CompletedTask; });
    }

    private static List<(long, long)>? UsedRangesOf(IBlockDevice vol, FsKind fs)
    {
        try
        {
            var res = FileSystemScanner.Scan(vol, new ScanOptions { IncludeLive = false }, null, CancellationToken.None);
            var map = res.Allocation;
            if (map == null) return null;
            var ranges = map.UsedRanges(vol.Length).ToList();
            // juntar faixas próximas: menos E/S picada
            var merged = new List<(long, long)>();
            foreach (var (o, l) in ranges)
            {
                if (merged.Count > 0 && o - (merged[^1].Item1 + merged[^1].Item2) < (4 << 20))
                    merged[^1] = (merged[^1].Item1, o + l - merged[^1].Item1);
                else merged.Add((o, l));
            }
            return merged;
        }
        catch { return null; }
    }

    // ───────────── copiar partição ─────────────

    public static async Task CopyPartition(PartitionInfo src, DiskInfo target, long targetStartLba, bool usedOnly, bool useVss, OpSink sink)
    {
        if (OperatingSystem.IsWindows() || target.IsImage) RequireModifiable(target);
        else if (!target.PartitionsEditable) throw new InvalidOperationException("O disco de destino não aceita partições novas.");
        if (target.SectorSize != src.Disk.SectorSize) throw new InvalidOperationException("Os discos têm tamanhos de setor diferentes (512 × 4096).");
        var t = target.Table.Clone();
        if (t.Style == TableStyle.None) throw new InvalidOperationException("Inicialize o disco de destino (GPT ou MBR) antes.");
        long count = src.Length / target.SectorSize;
        var e = new PartitionEntry { StartLba = targetStartLba, SectorCount = count };
        if (t.Style == TableStyle.Gpt)
        {
            e.TypeGuid = src.Disk.Table.Style == TableStyle.Gpt ? src.Entry?.TypeGuid ?? PartitionTypes.BasicData : PartitionTypes.MbrToGpt(src.Entry?.MbrType ?? 7).type;
            e.Name = src.Label ?? "";
            e.UniqueGuid = Guid.NewGuid();
        }
        else e.MbrType = src.Disk.Table.Style == TableStyle.Mbr ? src.Entry?.MbrType ?? 7 : PartitionTypes.GptToMbr(src.Entry?.TypeGuid ?? PartitionTypes.BasicData, src.FileSystem) ?? 7;
        t.Entries.Add(e);
        var errs = PartitionCodec.Validate(t);
        if (errs.Count > 0) throw new InvalidOperationException(string.Join("\n", errs));

        var (sdev, release) = await OpenSource(src, useVss, sink);
        try
        {
            var ranges = usedOnly ? UsedRangesOf(sdev, src.FileSystem) : null;
            if (ranges != null) sink.Log($"Copiando só o espaço em uso: {Size(ranges.Sum(r => r.Item2))} de {Size(src.Length)}.");
            using var tdev = DiskSystem.OpenDisk(target, true);
            // Linux: o espaço livre não pertence a nenhuma partição montada — nada a desmontar
            using (DiskSystem.Native.LockVolumes(target, OperatingSystem.IsWindows() ? null : []))
            {
                var r = SectorCopier.Copy(sdev, tdev, new CopyOptions { TargetOffset = targetStartLba * target.SectorSize, Ranges = ranges },
                    CopyReporter(sink, "Copiando partição"), sink.Token, sink.PauseGate);
                if (r.BadSectors.Count > 0) sink.Log($"{r.BadSectors.Count} setores ilegíveis na origem foram gravados como zeros.");
                BootSector.SetHiddenSectors(new SubDevice(tdev, targetStartLba * target.SectorSize, src.Length), src.FileSystem, targetStartLba);
            }
        }
        finally { await release(); }
        WriteTable(target, t, "Copiar partição", sink);
        sink.Log(OperatingSystem.IsWindows() ? "Partição copiada. O Windows deve montá-la com uma letra nova em alguns segundos."
            : "Partição copiada. Atenção: a cópia tem o mesmo UUID da original — se as duas ficarem no mesmo computador, troque o UUID de uma (Alterar UUID) para o sistema não confundir.");
    }

    // ───────────── imagem de disco ─────────────

    public sealed record ImageMeta(string Source, string Model, string Serial, long Size, int SectorSize, DateTime Created, string? Sha256, long BadSectors, string Table, List<string> Partitions);

    public static async Task CreateImage(DiskInfo d, PartitionInfo? onlyPartition, string path, bool useVss, bool hash, int retries, OpSink sink)
    {
        long total = onlyPartition?.Length ?? d.Size;
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null)
        {
            var di = new DriveInfo(dir);
            if (di.IsReady && di.AvailableFreeSpace < total) throw new InvalidOperationException($"Espaço insuficiente no destino: precisa de {Size(total)}, há {Size(di.AvailableFreeSpace)}.");
        }
        using var sha = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        long bad = 0;
        using (var img = ImageFileDevice.Create(path, total, d.SectorSize))
        {
            var regions = new List<(PartitionInfo? part, long offset, long length)>();
            if (onlyPartition != null) regions.Add((onlyPartition, 0, onlyPartition.Length));
            else
            {
                long cur = 0;
                foreach (var p in d.Partitions.Where(p => p.Entry != null || p.IsWholeDisk).OrderBy(p => p.Offset))
                {
                    if (p.Offset > cur) regions.Add((null, cur, p.Offset - cur));
                    regions.Add((p, p.Offset, p.Length));
                    cur = p.Offset + p.Length;
                }
                if (cur < d.Size) regions.Add((null, cur, d.Size - cur));
            }
            long baseDone = 0;
            using var disk = DiskSystem.OpenDisk(d, false);
            foreach (var (part, offset, length) in regions)
            {
                IBlockDevice src; Func<Task> release;
                if (part != null && useVss) (src, release) = await OpenSource(part, true, sink);
                else { src = new SubDevice(disk, offset, length); release = () => Task.CompletedTask; }
                try
                {
                    var r = SectorCopier.Copy(src, img, new CopyOptions
                    {
                        TargetOffset = onlyPartition != null ? 0 : offset, Retries = retries, HashSink = sha,
                        ProgressBase = baseDone, ProgressTotal = total, Ranges = [(0, Math.Min(length, src.Length))],
                    }, CopyReporter(sink, "Gravando imagem"), sink.Token, sink.PauseGate);
                    bad += r.BadSectors.Count;
                    if (r.BadSectors.Count > 0) sink.Log($"{r.BadSectors.Count} setores ilegíveis em {part?.DisplayName ?? "área livre"} (primeiro: {r.BadSectors[0]:N0}).");
                }
                finally { await release(); }
                baseDone += length;
            }
        }
        string? digest = sha != null ? Convert.ToHexString(sha.GetHashAndReset()) : null;
        var meta = new ImageMeta(onlyPartition != null ? $"{d.Title} · {onlyPartition.DisplayName}" : d.Title, d.Model, d.Serial, total, d.SectorSize, DateTime.Now, digest, bad,
            d.StyleText, d.Partitions.Where(p => !p.IsUnallocated).Select(p => $"{p.DisplayName} {FsDetect.Name(p.FileSystem)} {Size(p.Length)} @ {p.Offset}").ToList());
        await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        sink.Log($"Imagem concluída: {path}");
        if (digest != null) sink.Log($"SHA-256: {digest}");
        sink.Log(bad == 0 ? "Nenhum setor ilegível." : $"{bad} setores ilegíveis foram preenchidos com zeros (lista no log).");
    }

    public static void RestoreImage(string imagePath, DiskInfo target, PartitionInfo? targetPartition, OpSink sink)
    {
        if (targetPartition == null) RequireModifiable(target); else RequirePartition(targetPartition);
        using var img = new ImageFileDevice(imagePath, false, target.SectorSize);
        long capacity = targetPartition?.Length ?? target.Size;
        if (img.Length > capacity) throw new InvalidOperationException($"A imagem ({Size(img.Length)}) é maior que o destino ({Size(capacity)}).");
        using var dev = DiskSystem.OpenDisk(target, true);
        using (DiskSystem.Native.LockVolumes(target, targetPartition == null ? null : [targetPartition]))
        {
            SectorCopier.Copy(img, dev, new CopyOptions { TargetOffset = targetPartition?.Offset ?? 0 }, CopyReporter(sink, "Restaurando imagem"), sink.Token, sink.PauseGate);
            DiskSystem.Native.Rescan(target);
        }
        DiskSystem.Native.Rescan(target);
        sink.Log("Imagem restaurada.");
    }

    // ───────────── clonar disco / migrar sistema ─────────────

    public sealed class ClonePlan
    {
        public required DiskInfo Source { get; init; }
        public required DiskInfo Target { get; init; }
        public List<(PartitionInfo src, long start, long sectors)> Items { get; } = [];
        public PartitionInfo? Grow { get; set; }
        public long GrowBy { get; set; }
        /// <summary>Quanto falta no destino (positivo = não cabe).</summary>
        public long Deficit { get; set; }
        public PartitionInfo? ShrinkCandidate { get; set; }
        public bool SystemOnly { get; set; }
    }

    public static ClonePlan PlanClone(DiskInfo source, DiskInfo target, bool systemOnly, bool fitToTarget)
    {
        var plan = new ClonePlan { Source = source, Target = target, SystemOnly = systemOnly };
        int ss = source.SectorSize;
        var parts = source.Partitions.Where(p => p.Entry != null)
            .Where(p => !systemOnly || p.IsEsp || p.IsMsr || p.IsSystemVolume || p.IsRecovery || p.Entry!.Active)
            .OrderBy(p => p.Offset).ToList();
        var probe = new PartitionTable { Style = source.Table.Style == TableStyle.None ? TableStyle.Gpt : source.Table.Style, SectorSize = ss, DiskSectors = target.Size / ss };
        long align = probe.AlignSectors;
        long first = probe.Style == TableStyle.Gpt ? PartitionCodec.GptFirstUsable(probe) : 1;
        long last = probe.Style == TableStyle.Gpt ? PartitionCodec.GptLastUsable(probe) : probe.DiskSectors - 1;
        var main = parts.FirstOrDefault(p => p.IsSystemVolume) ?? parts.Where(p => p.FileSystem == FsKind.Ntfs).MaxBy(p => p.Length);
        plan.ShrinkCandidate = main;

        // mantém o deslocamento original quando dá (ajuda o Windows a reconhecer), senão compacta
        long cur = Math.Max(first, align);
        var items = new List<(PartitionInfo, long, long)>();
        foreach (var p in parts)
        {
            long start = !systemOnly && p.Entry!.StartLba >= cur ? p.Entry.StartLba : (cur + align - 1) / align * align;
            items.Add((p, start, p.Entry!.SectorCount));
            cur = start + p.Entry.SectorCount;
        }
        long end = items.Count > 0 ? items[^1].Item2 + items[^1].Item3 - 1 : 0;
        long room = last - end;
        if (room < 0) plan.Deficit = -room * ss;
        else if (fitToTarget && main != null && room > align)
        {
            // cresce a principal: o que vem depois dela vai para o fim do disco
            int mi = items.FindIndex(i => i.Item1 == main);
            long grow = room / align * align;
            for (int i = mi + 1; i < items.Count; i++) items[i] = (items[i].Item1, items[i].Item2 + grow, items[i].Item3);
            plan.Grow = main;
            plan.GrowBy = grow * ss;
        }
        plan.Items.AddRange(items);
        return plan;
    }

    public static async Task Clone(ClonePlan plan, bool usedOnly, bool useVss, bool makeBootable, OpSink sink)
    {
        RequireModifiable(plan.Target);
        if (plan.Deficit > 0) throw new InvalidOperationException($"Não cabe: faltam {Size(plan.Deficit)} no disco de destino.");
        if (plan.Target.SectorSize != plan.Source.SectorSize) throw new InvalidOperationException("Os discos têm tamanhos de setor diferentes (512 × 4096): a cópia setor a setor não funcionaria.");
        int ss = plan.Source.SectorSize;
        var style = plan.Source.Table.Style == TableStyle.None ? TableStyle.Gpt : plan.Source.Table.Style;
        var t = new PartitionTable
        {
            Style = style, SectorSize = ss, DiskSectors = plan.Target.Size / ss, DiskGuid = Guid.NewGuid(),
            BootCode = plan.Source.Table.BootCode, DiskSignature = (uint)Random.Shared.NextInt64(1, uint.MaxValue),
        };
        long total = plan.Items.Sum(i => i.src.Length), baseDone = 0;
        using var tdev = DiskSystem.OpenDisk(plan.Target, true);
        using (DiskSystem.Native.LockVolumes(plan.Target))
        {
            foreach (var (src, start, sectors) in plan.Items)
            {
                sink.Log($"Copiando {src.DisplayName} ({FsDetect.Name(src.FileSystem)}, {Size(src.Length)}) para o setor {start:N0}…");
                var (sdev, release) = await OpenSource(src, useVss, sink);
                try
                {
                    var ranges = usedOnly && !src.IsMsr ? UsedRangesOf(sdev, src.FileSystem) : null;
                    var r = SectorCopier.Copy(sdev, tdev, new CopyOptions
                    {
                        TargetOffset = start * ss, Ranges = ranges ?? [(0, Math.Min(src.Length, sdev.Length))],
                        ProgressBase = baseDone, ProgressTotal = total,
                    }, CopyReporter(sink, "Clonando"), sink.Token, sink.PauseGate);
                    if (r.BadSectors.Count > 0) sink.Log($"  {r.BadSectors.Count} setores ilegíveis gravados como zeros.");
                    BootSector.SetHiddenSectors(new SubDevice(tdev, start * ss, sectors * ss), src.FileSystem, start);
                }
                finally { await release(); }
                baseDone += src.Length;
                var e = src.Entry!.Clone();
                e.StartLba = start;
                e.SectorCount = sectors;
                e.UniqueGuid = Guid.NewGuid();
                e.EbrLba = 0;
                t.Entries.Add(e);
            }
            tdev.Flush();
        }
        WriteTable(plan.Target, t, plan.SystemOnly ? "Migrar sistema" : "Copiar disco", sink);

        if (!OperatingSystem.IsWindows() || plan.Target.IsImage) return;
        await Task.Delay(2500, sink.Token);
        if (plan.Grow != null)
        {
            long newOfs = plan.Items.First(i => i.src == plan.Grow).start * ss;
            sink.Log($"Expandindo {plan.Grow.DisplayName} para ocupar o espaço extra ({Size(plan.GrowBy)})…");
            var r = await WinCommands.PowerShell($"$p=Get-Partition -DiskNumber {plan.Target.Number} | Where-Object {{ $_.Offset -eq {newOfs} }}; $m=($p | Get-PartitionSupportedSize).SizeMax; $p | Resize-Partition -Size $m", sink.Token);
            sink.Log(r.Ok ? "Partição expandida." : "Não foi possível expandir agora (faça depois em Mover/Redimensionar): " + r.Message);
        }
        if (makeBootable)
        {
            var win = plan.Items.FirstOrDefault(i => i.src.IsSystemVolume);
            var sys = plan.Items.FirstOrDefault(i => i.src.IsEsp || (style == TableStyle.Mbr && i.src.Entry!.Active));
            if (win.src == null || sys.src == null) { sink.Log("Sem partição do Windows/sistema no plano: o disco novo não foi preparado para iniciar."); return; }
            sink.Log("Preparando o disco novo para iniciar (bcdboot)…");
            var wl = await WinCommands.AssignTemporaryLetter(plan.Target.Number, win.start * ss);
            var sl = await WinCommands.AssignTemporaryLetter(plan.Target.Number, sys.start * ss);
            string w = wl.Output.Trim(), s = sl.Output.Trim();
            if (!wl.Ok || !sl.Ok || w.Length != 1 || s.Length != 1) { sink.Log("Não foi possível dar letras temporárias às partições novas: " + wl.Message + " " + sl.Message); return; }
            var b = await WinCommands.BcdBoot($"{w}:\\Windows", $"{s}:", style == TableStyle.Gpt, sink.Token);
            sink.Log(b.Ok ? "Inicialização configurada no disco novo." : "bcdboot falhou: " + b.Message);
            await WinCommands.PowerShell($"Get-Partition -DiskNumber {plan.Target.Number} | Where-Object {{ $_.Offset -eq {sys.start * ss} }} | Remove-PartitionAccessPath -AccessPath '{s}:\\'");
            sink.Log("Pronto. Para iniciar pelo disco novo, escolha-o na BIOS/UEFI (ou troque os discos de lugar).");
        }
    }

    // ───────────── partições perdidas ─────────────

    public static void RestoreFound(DiskInfo d, IEnumerable<FoundPartition> chosen, TableStyle style, OpSink sink)
    {
        var t = PartitionFinder.BuildRestoredTable(d.Table, chosen, style, d.Size / d.SectorSize, d.SectorSize);
        WriteTable(d, t, "Recuperar partições", sink);
        sink.Log($"{chosen.Count()} partição(ões) restaurada(s) na tabela {(style == TableStyle.Gpt ? "GPT" : "MBR")}.");
    }

    public static void RestoreTableBackup(DiskInfo d, string backupFile, OpSink sink)
    {
        RequireModifiable(d);
        using var dev = DiskSystem.OpenDisk(d, true);
        using (DiskSystem.Native.LockVolumes(d))
        {
            TableBackup.Restore(dev, backupFile);
            DiskSystem.Native.Rescan(d);
        }
        DiskSystem.Native.Rescan(d);
        sink.Log("Backup de tabela restaurado.");
    }
}
