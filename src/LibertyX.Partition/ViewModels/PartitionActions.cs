using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Windows;

namespace LibertyX.Partition.ViewModels;

/// <summary>As ações do menu de partição/disco: cada uma abre um formulário e roda a operação.</summary>
public static partial class ActionsBuilder
{
    private static FormDialogViewModel Form(string title, string icon, string? subtitle = null, string? confirm = null, bool danger = false) =>
        new() { Title = title, IconKey = icon, Subtitle = subtitle, ConfirmText = confirm ?? L.T("Adicionar"), Danger = danger };

    private static Task Exec(Func<OpSink, Task> f, OpSink s) => f(s);
    private static Task Exec(Action<OpSink> f, OpSink s) { f(s); return Task.CompletedTask; }

    private static string Who(PartitionInfo p) => p.Letter
        ?? (p.KernelName is { } k ? (string.IsNullOrEmpty(p.Label) ? k : $"{k} ({p.Label})")
        : string.IsNullOrEmpty(p.Label) ? L.T($"partição em {Fmt.Size(p.Offset)}") : p.Label!);

    // ───────────── disco ─────────────

    private static async Task InitializeDisk(DiskInfo d)
    {
        var f = Form(L.T("Inicializar disco"), "Create", d.Title + " · " + d.Model);
        var style = f.Add(new ChoiceField { Label = L.T("Estilo de partição") });
        style.Options.Add(L.T("GPT (recomendado: UEFI, discos acima de 2 TB, até 128 partições)"));
        style.Options.Add(L.T("MBR (computadores antigos com BIOS, até 4 partições primárias)"));
        f.Note(L.T("Cria uma tabela de partições vazia. Se este disco tinha partições, use antes Recuperar partição — inicializar por cima atrapalha a busca."), "warn");
        if (!await Main.ShowDialog(f)) return;
        var t = new PartitionTable { Style = style.SelectedIndex == 0 ? TableStyle.Gpt : TableStyle.Mbr, SectorSize = d.SectorSize, DiskSectors = d.Size / d.SectorSize };
        Main.Queue(L.T("Inicializar disco"), $"{d.Title} · {d.Model}", "Create", async s =>
        {
            var dq = MainViewModel.Fresh(d);
            await Exec(s => DiskOperations.WriteTable(dq, t, L.T("Inicializar disco"), s), s);
        }, d, dangerous: true, canPause: false);
    }

    private static async Task RestoreTableBackup(DiskInfo d)
    {
        var file = await MainViewModel.PickFile(L.T("Backup de tabela"), (L.T("Backup do LibertyX Partition"), ["*.lxbak"]));
        if (file == null) return;
        var (disk, bytes, when, reason) = TableBackup.Describe(file);
        if (bytes != d.Size) { await Main.Alert(L.T("Backup de outro disco"), L.T($"Este backup é de “{disk}” ({Fmt.Size(bytes)}); o disco escolhido tem {Fmt.Size(d.Size)}."), "danger"); return; }
        if (!await Main.Confirm(L.T("Restaurar backup de tabela"), L.T($"Backup de {when:dd/MM/yyyy HH:mm}, feito antes de: {reason}.\n\nA tabela atual de {d.Title} será substituída pela salva."), L.T("Adicionar"), true)) return;
        Main.Queue(L.T("Restaurar backup de tabela"), $"{d.Title} · {d.Model}", "Restore", async s =>
        {
            var dq = MainViewModel.Fresh(d);
            await Exec(s => DiskOperations.RestoreTableBackup(dq, file, s), s);
        }, d, dangerous: true, canPause: false);
    }

    // ───────────── criar ─────────────

    private static async Task CreatePartition(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form(L.T("Criar partição"), "Create", L.T($"{Fmt.Size(p.Length)} livres em {p.Disk.Title}"), L.T("Adicionar"));
        var size = f.Add(new SizeField { Label = L.T("Tamanho"), MinMb = 16, MaxMb = Math.Floor(p.Length / (double)MB) - 1, ValueMb = Math.Floor(p.Length / (double)MB) - 1 });
        var fs = f.Add(new ChoiceField { Label = L.T("Sistema de arquivos") });
        foreach (var o in new[] { "NTFS", "exFAT", "FAT32", L.T("Não formatar") }) fs.Options.Add(o);
        var label = f.Add(new TextField { Label = L.T("Rótulo"), Value = L.T("Novo volume"), MaxLength = 32 });
        var letter = f.Add(new ToggleField { Label = L.T("Atribuir letra de unidade"), Value = true });
        if (p.Disk.IsImage) { fs.SelectedIndex = 2; f.Note(L.T("Em imagem anexada, só FAT32 é formatado pelo próprio LibertyX."), "gold"); }
        if (!await Main.ShowDialog(f)) return;
        long start = (p.Offset + MB - 1) / MB * MB;
        long bytes = Math.Min(size.Bytes, (p.Offset + p.Length - start) / MB * MB);
        string? fsName = fs.SelectedIndex switch { 0 => "NTFS", 1 => "exFAT", 2 => "FAT32", _ => null };
        Main.Queue(L.T("Criar partição"), L.T($"{Fmt.Size(bytes)} em {fsName ?? L.T("sem formatação")}, rótulo “{label.Value}” · {p.Disk.Title}"), "Create", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
        {
            if (Win && !q.Disk.IsImage) { await CreateOnWindows(q.Disk, start, bytes, fsName, label.Value, letter.Value, s); return; }
            var t = q.Disk.Table.Clone();
            var e = new PartitionEntry { StartLba = start / q.Disk.SectorSize, SectorCount = bytes / q.Disk.SectorSize, Name = label.Value };
            if (t.Style == TableStyle.Gpt) { e.TypeGuid = PartitionTypes.BasicData; e.UniqueGuid = Guid.NewGuid(); }
            else e.MbrType = fsName == "FAT32" ? (byte)0x0C : (byte)0x07;
            t.Entries.Add(e);
            if (fsName == "FAT32")
            {
                using var dev = DiskSystem.OpenDisk(q.Disk, true);
                Fat32Formatter.Format(new SubDevice(dev, start, bytes), label.Value, 0, e.StartLba, new Progress<double>(x => s.Progress(x, L.T("Formatando FAT32"))));
            }
            DiskOperations.WriteTable(q.Disk, t, L.T("Criar partição"), s);
            if (Win && fsName == "FAT32" && letter.Value)
            {
                await Task.Delay(1500);
                await WinCommands.AssignTemporaryLetter(q.Disk.Number, start);
            }
        }, s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    // ───────────── mover / redimensionar / estender ─────────────

    private static async Task ApplyGeometry(PartitionInfo p, long newStart, long newBytes, OpSink s)
    {
        var d = p.Disk;
        long off = p.Offset, old = p.Length;
        if (newBytes < old)
        {
            s.Log(L.T($"Reduzindo {Who(p)} para {Fmt.Size(newBytes)} (o próprio Windows reorganiza os arquivos)…"));
            s.Progress(-1, L.T("Reduzindo"));
            Ensure(await WinCommands.Resize(d.Number, off, newBytes, s.Token), L.T("Reduzir"));
        }
        if (newStart != off)
        {
            var (_, np) = Reload(d, off);
            if (np == null) throw new InvalidOperationException(L.T("A partição sumiu da tabela depois do redimensionamento."));
            DiskOperations.MovePartition(np, newStart / d.SectorSize, s);
            off = newStart;
            await Task.Delay(2500, s.Token);
        }
        if (newBytes > old)
        {
            s.Log(L.T($"Aumentando para {Fmt.Size(newBytes)}…"));
            s.Progress(-1, L.T("Estendendo"));
            Ensure(await WinCommands.Resize(d.Number, off, newBytes, s.Token), L.T("Estender"));
        }
    }

    private static async Task ResizeMove(PartitionVM pv)
    {
        var p = pv.P;
        var (before, after) = Neighbors(pv);
        long freeBefore = before?.IsUnallocated == true ? before.Length : 0;
        long freeAfter = after?.IsUnallocated == true ? after.Length : 0;
        bool canMove = PartLock(p) == null && (Win || p.Disk.IsImage);
        bool resizable = Win && !p.Disk.IsImage && p.FileSystem is FsKind.Ntfs or FsKind.ReFS && p.VolumePath != null;
        long min = p.Length, max = p.Length;
        if (resizable)
        {
            var sup = await WinCommands.SupportedSize(p.Disk.Number, p.Offset);
            if (sup != null) { min = sup.Value.min; }
            max = p.Length + freeAfter + (canMove ? freeBefore : 0);
        }
        long regionStart = p.Offset - (canMove ? freeBefore : 0), regionEnd = p.Offset + p.Length + freeAfter;
        double origMb = Math.Round(p.Length / (double)MB), origBefore = Math.Round(freeBefore / (double)MB);

        var f = Form(L.T("Mover / redimensionar"), "Resize", $"{Who(p)} · {FsDetect.Name(p.FileSystem)} · {Fmt.Size(p.Length)}");
        var size = f.Add(new SizeField { Label = L.T("Tamanho da partição"), MinMb = Math.Ceiling(min / (double)MB), MaxMb = Math.Floor(max / (double)MB), ValueMb = origMb });
        SizeField? pos = null;
        if (canMove && (freeBefore > 0 || freeAfter > 0))
            pos = f.Add(new SizeField { Label = L.T("Espaço livre antes da partição"), MinMb = 0, MaxMb = Math.Floor((freeBefore + freeAfter) / (double)MB), ValueMb = origBefore });
        void Update()
        {
            double total = (regionEnd - regionStart) / (double)MB;
            double b = pos?.ValueMb ?? origBefore;
            double a = total - b - size.ValueMb;
            size.Caption = a < -0.5 ? L.T("Não cabe: diminua o espaço antes ou o tamanho.") : L.T($"Livre depois: {Fmt.Size((long)Math.Max(0, a) * MB)}");
        }
        size.Changed += _ => Update();
        if (pos != null) pos.Changed += _ => Update();
        Update();
        if (!resizable) f.Note(p.FileSystem is FsKind.Ntfs or FsKind.ReFS ? L.T("Redimensionar exige o volume montado no Windows; aqui dá só para mover.") : L.T($"{FsDetect.Name(p.FileSystem)} não é redimensionado pelo Windows; aqui dá para mover a partição."), "gold");
        if (canMove) f.Note(L.T("Mover desloca todos os dados no disco: leva tempo e o computador não pode desligar no meio. Tenha backup do que importa."), "warn");
        f.Validate = () =>
        {
            double b = pos?.ValueMb ?? origBefore;
            long newStart = Math.Abs(b - origBefore) < 0.5 ? p.Offset : (regionStart + (long)b * MB + MB - 1) / MB * MB;
            long newBytes = Math.Abs(size.ValueMb - origMb) < 0.5 ? p.Length : size.Bytes;
            if (newStart + newBytes > regionEnd) return L.T("A partição não cabe nessa posição com esse tamanho.");
            if (newStart == p.Offset && newBytes == p.Length) return L.T("Nada mudou.");
            return null;
        };
        if (!await Main.ShowDialog(f)) return;
        double bb = pos?.ValueMb ?? origBefore;
        long ns = Math.Abs(bb - origBefore) < 0.5 ? p.Offset : (regionStart + (long)bb * MB + MB - 1) / MB * MB;
        long nb = Math.Abs(size.ValueMb - origMb) < 0.5 ? p.Length : size.Bytes;
        Main.Queue(L.T("Mover / redimensionar"), $"{Who(p)}: {Fmt.Size(p.Length)} → {Fmt.Size(nb)}{(ns != p.Offset ? L.T($", início em {Fmt.Size(ns)} (move os dados)") : "")} · {p.Disk.Title}", "Resize", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(s => ApplyGeometry(q, ns, nb, s), s);
        }, p.Disk, dangerous: false, canPause: true);
    }

    private static async Task Extend(PartitionVM pv)
    {
        var p = pv.P;
        var (before, after) = Neighbors(pv);
        long freeAfter = after?.IsUnallocated == true ? after.Length : 0;
        long freeBefore = before?.IsUnallocated == true && PartLock(p) == null ? before.Length : 0;
        var f = Form(L.T("Estender partição"), "Extend", $"{Who(p)} · {Fmt.Size(p.Length)}");
        var add = f.Add(new SizeField { Label = L.T("Aumentar em"), MinMb = 1, MaxMb = Math.Floor((freeAfter + freeBefore) / (double)MB), ValueMb = Math.Floor((freeAfter + freeBefore) / (double)MB) });
        add.Changed += v => add.Caption = L.T($"Novo tamanho: {Fmt.Size(p.Length + (long)v * MB)}") + ((long)v * MB > freeAfter ? L.T(" · a partição será movida para a esquerda") : "");
        add.Caption = L.T($"Novo tamanho: {Fmt.Size(p.Length + add.Bytes)}");
        if (freeBefore == 0 && before?.IsUnallocated == true) f.Note(L.T("Há espaço livre antes, mas esta partição está em uso e não pode ser movida agora."), "gold");
        if (!await Main.ShowDialog(f)) return;
        long extra = add.Bytes;
        long newStart = p.Offset;
        if (extra > freeAfter) newStart = Math.Max(p.Offset - freeBefore, (p.Offset - (extra - freeAfter)) / MB * MB);
        Main.Queue(L.T("Estender partição"), $"{Who(p)}: {Fmt.Size(p.Length)} → {Fmt.Size(p.Length + extra)} · {p.Disk.Title}", "Extend", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(s => ApplyGeometry(q, newStart, q.Length + extra, s), s);
        }, p.Disk, dangerous: false, canPause: true);
    }

    // ───────────── unir / dividir ─────────────

    private static async Task Merge(PartitionVM pv)
    {
        var p = pv.P;
        var (_, next) = Neighbors(pv);
        if (next == null) return;
        var f = Form(L.T("Unir partições"), "Merge", $"{Who(p)} + {Who(next)}", L.T("Adicionar"), true);
        var folder = f.Add(new TextField { Label = L.T($"Os arquivos de {next.Letter} vão para esta pasta em {p.Letter}"), Value = L.T($"Conteúdo de {next.Letter?.TrimEnd(':')}") });
        f.Note(L.T($"1. Copia tudo de {next.Letter} para {p.Letter}\\{folder.Value}\n2. Exclui a partição {next.Letter}\n3. Estende {p.Letter} sobre o espaço liberado"), "gold");
        if ((p.Free ?? 0) < (next.Used ?? next.Length)) f.Note(L.T($"{p.Letter} tem {Fmt.Size(p.Free)} livres e {next.Letter} usa {Fmt.Size(next.Used)}: não cabe."), "danger");
        f.Validate = () => (p.Free ?? 0) < (next.Used ?? next.Length) ? L.T("Espaço insuficiente.") : string.IsNullOrWhiteSpace(folder.Value) ? L.T("Dê um nome à pasta.") : null;
        if (!await Main.ShowDialog(f)) return;
        Main.Queue(L.T("Unir partições"), L.T($"{Who(next)} vai para {p.Letter}\\{folder.Value}, é excluída e {p.Letter} cresce"), "Merge", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
        {
            string dest = Path.Combine(q.Letter + "\\", RecoveryNames.Safe(folder.Value));
            s.Log(L.T($"Copiando {next.Letter} → {dest}"));
            s.Progress(-1, L.T("Copiando arquivos"));
            var r = await WinCommands.Robocopy(next.Letter + "\\", dest, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
            if (r.ExitCode >= 8) throw new InvalidOperationException(L.T($"A cópia teve falhas (robocopy {r.ExitCode}). Nada foi excluído."));
            s.Log(L.T($"Excluindo {next.Letter}…"));
            Ensure(await WinCommands.Delete(next.Disk.Number, next.Offset), L.T("Excluir"));
            await Task.Delay(1500, s.Token);
            s.Progress(-1, L.T("Estendendo"));
            Ensure(await WinCommands.ExtendFileSystem(q.Letter![0], s.Token), L.T("Estender"));
        }, s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    private static async Task Split(PartitionVM pv)
    {
        var p = pv.P;
        var sup = await WinCommands.SupportedSize(p.Disk.Number, p.Offset);
        long min = sup?.min ?? p.Length;
        var f = Form(L.T("Dividir partição"), "Split", L.T($"{Who(p)} · {Fmt.Size(p.Length)} · {Fmt.Size(p.Used)} usados"));
        var size = f.Add(new SizeField { Label = L.T($"Tamanho que fica em {Who(p)}"), MinMb = Math.Ceiling(min / (double)MB), MaxMb = Math.Floor((p.Length - 64 * MB) / (double)MB), ValueMb = Math.Max(Math.Ceiling(min / (double)MB), Math.Round(p.Length / 2.0 / MB)) });
        size.Changed += v => size.Caption = L.T($"Nova partição: {Fmt.Size(p.Length - (long)v * MB)}");
        size.Caption = L.T($"Nova partição: {Fmt.Size(p.Length - size.Bytes)}");
        var fs = f.Add(new ChoiceField { Label = L.T("Sistema de arquivos da nova") });
        foreach (var o in new[] { "NTFS", "exFAT", "FAT32" }) fs.Options.Add(o);
        var label = f.Add(new TextField { Label = L.T("Rótulo da nova"), Value = L.T("Novo volume") });
        if (sup == null) f.Note(L.T("Não foi possível consultar quanto o Windows consegue reduzir este volume."), "warn");
        if (!await Main.ShowDialog(f)) return;
        long keep = size.Bytes;
        Main.Queue(L.T("Dividir partição"), L.T($"{Who(p)} fica com {Fmt.Size(keep)}; o resto vira partição nova em {fs.Options[fs.SelectedIndex]}"), "Split", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
        {
            s.Progress(-1, L.T("Reduzindo"));
            Ensure(await WinCommands.Resize(q.Disk.Number, q.Offset, keep, s.Token), L.T("Reduzir"));
            long start = (q.Offset + keep + MB - 1) / MB * MB;
            long len = (q.Offset + q.Length - start) / MB * MB;
            s.Progress(-1, L.T("Criando a nova partição"));
            Ensure(await WinCommands.CreatePartition(q.Disk.Number, start, len, null, null, fs.Options[fs.SelectedIndex], label.Value, 0, true, s.Token), L.T("Criar"));
        }, s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    // ───────────── cluster / conversão (cópia de passagem) ─────────────

    private static async Task<bool> StagedReformat(PartitionInfo p, string staging, string fs, int cluster, OpSink s)
    {
        string label = p.Label ?? "";
        string temp = Path.Combine(staging, $"LibertyX-passagem-{DateTime.Now:yyyyMMdd-HHmmss}");
        s.Log(L.T($"1/3 Copiando {p.Letter} para {temp}…"));
        s.Progress(-1, L.T("Copiando para a área de passagem"));
        var r = await WinCommands.Robocopy(p.Letter + "\\", temp, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
        if (r.ExitCode >= 8) throw new InvalidOperationException(L.T($"A cópia de segurança falhou (robocopy {r.ExitCode}). A partição NÃO foi tocada."));
        s.Log(L.T($"2/3 Formatando {p.Letter} em {fs}{(cluster > 0 ? L.T($" com cluster de {cluster / 1024} KB") : "")}…"));
        s.Progress(-1, L.T("Formatando"));
        if (fs == "FAT32" && p.Length > 32L << 30)
        {
            using (var dev = DiskSystem.OpenDisk(p.Disk, true))
            using (DiskSystem.Native.LockVolumes(p.Disk, [p]))
                Fat32Formatter.Format(new SubDevice(dev, p.Offset, p.Length), label, cluster, p.StartLba);
            DiskSystem.Native.Rescan(p.Disk);
            await Task.Delay(2000, s.Token);
        }
        else Ensure(await WinCommands.Format(p.Disk.Number, p.Offset, fs, label, cluster, true, s.Token), L.T("Formatar"));
        if (p.Disk.Table.Style == TableStyle.Mbr)
            await WinCommands.SetMbrType(p.Disk.Number, p.Offset, fs == "FAT32" ? (byte)0x0C : (byte)0x07);
        s.Log(L.T($"3/3 Devolvendo os arquivos para {p.Letter}…"));
        s.Progress(-1, L.T("Devolvendo os arquivos"));
        var back = await WinCommands.Robocopy(temp, p.Letter + "\\", s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
        if (back.ExitCode >= 8) { s.Log(L.T($"ATENÇÃO: a volta teve falhas — seus arquivos continuam inteiros em {temp}.")); return false; }
        try { Directory.Delete(temp, true); } catch { s.Log(L.T($"A pasta de passagem ficou em {temp} (pode apagar).")); }
        return true;
    }

    private static string? StagingCheck(PathField path, PartitionInfo p)
    {
        if (string.IsNullOrWhiteSpace(path.Value) || !Directory.Exists(path.Value)) return L.T("Escolha uma pasta de passagem existente.");
        if (p.Letter != null && path.Value.StartsWith(p.Letter, StringComparison.OrdinalIgnoreCase)) return L.T("A pasta de passagem precisa estar em OUTRA unidade.");
        try
        {
            var di = new DriveInfo(Path.GetPathRoot(path.Value)!);
            if (di.AvailableFreeSpace < (p.Used ?? p.Length)) return L.T($"A unidade da pasta tem {Fmt.Size(di.AvailableFreeSpace)} livres; precisa de {Fmt.Size(p.Used)}.");
        }
        catch { }
        return null;
    }

    private static readonly int[] Clusters = [0, 4096, 8192, 16384, 32768, 65536];

    private static async Task ChangeCluster(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form(L.T("Alterar tamanho do cluster"), "Cluster", L.T($"{Who(p)} · {FsDetect.Name(p.FileSystem)} · {Fmt.Size(p.Used)} usados"));
        var c = f.Add(new ChoiceField { Label = L.T("Novo tamanho do cluster") });
        foreach (var x in Clusters) c.Options.Add(x == 0 ? L.T("Padrão do Windows") : $"{x / 1024} KB");
        c.SelectedIndex = 1;
        var path = f.Add(new PathField { Label = L.T("Pasta de passagem (em outra unidade)"), PickerTitle = L.T("Pasta de passagem") });
        f.Note(L.T("O tamanho do cluster só muda formatando. O LibertyX copia tudo para a pasta de passagem, formata com o cluster novo e devolve os arquivos. Se algo der errado na volta, a cópia fica intacta na pasta."), "gold");
        f.Validate = () => StagingCheck(path, p);
        if (!await Main.ShowDialog(f)) return;
        string fs = p.FileSystem == FsKind.ExFat ? "exFAT" : FsDetect.IsFat(p.FileSystem) ? "FAT32" : "NTFS";
        Main.Queue(L.T("Alterar tamanho do cluster"), $"{Who(p)} · {p.Disk.Title} ({p.Disk.Model})", "Cluster", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(s => StagedReformat(q, path.Value, fs, Clusters[c.SelectedIndex], s), s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    private static async Task FsConvert(PartitionVM pv)
    {
        var p = pv.P;
        if (FsDetect.IsFat(p.FileSystem))
        {
            if (!await Main.Confirm(L.T("Converter FAT → NTFS"), L.T($"{Who(p)} será convertida para NTFS pelo conversor do próprio Windows, sem apagar os arquivos. A conversão não tem volta direta."), L.T("Adicionar"))) return;
            Main.Queue(L.T("Converter FAT → NTFS"), $"{Who(p)} · {p.Disk.Title} ({p.Disk.Model})", "FsConvert", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
            {
                s.Progress(-1, L.T("Convertendo"));
                var r = await WinCommands.ConvertToNtfs(q.Letter![0], q.Label, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
                Ensure(r, L.T("Converter"));
            }, s);
        }, p.Disk, dangerous: false, canPause: false);
            return;
        }
        var f = Form(L.T("Converter NTFS → FAT"), "FsConvert", L.T($"{Who(p)} · {Fmt.Size(p.Used)} usados"));
        var target = f.Add(new ChoiceField { Label = L.T("Converter para") });
        target.Options.Add(L.T("FAT32 (máxima compatibilidade; arquivos até 4 GB)"));
        target.Options.Add(L.T("exFAT (pendrives e cartões; sem limite de 4 GB)"));
        var path = f.Add(new PathField { Label = L.T("Pasta de passagem (em outra unidade)"), PickerTitle = L.T("Pasta de passagem") });
        f.Note(L.T("Permissões e compactação do NTFS não existem em FAT e se perdem. Os arquivos são copiados para a pasta de passagem e voltam depois da formatação."), "gold");
        f.Validate = () => StagingCheck(path, p) ?? (target.SelectedIndex == 0 && p.Length > 2L * 1024 * 1024 * 1024 * 1024 ? L.T("FAT32 não passa de 2 TB.") : null);
        if (!await Main.ShowDialog(f)) return;
        string fs = target.SelectedIndex == 0 ? "FAT32" : "exFAT";
        Main.Queue(L.T($"Converter NTFS → {fs}"), $"{Who(p)} · {p.Disk.Title} ({p.Disk.Model})", "FsConvert", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
        {
            if (fs == "FAT32")
            {
                s.Progress(-1, L.T("Procurando arquivos acima de 4 GB"));
                var big = new DirectoryInfo(q.Letter + "\\").EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(x => x.Length >= 4L << 30).Take(5).ToList();
                if (big.Count > 0) throw new InvalidOperationException(L.T("FAT32 não guarda arquivos de 4 GB ou mais:\n") + string.Join("\n", big.Select(b => b.FullName)) + L.T("\nEscolha exFAT ou mova esses arquivos."));
            }
            await StagedReformat(q, path.Value, fs, 0, s);
        }, s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    // ───────────── formatar / excluir / alinhar ─────────────

    private static async Task Format(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form(L.T("Formatar partição"), "Format", $"{Who(p)} · {Fmt.Size(p.Length)}", L.T("Adicionar"), true);
        var fs = f.Add(new ChoiceField { Label = L.T("Sistema de arquivos") });
        var fsList = p.Disk.IsImage ? new[] { "FAT32" } : new[] { "NTFS", "exFAT", "FAT32", "ReFS" };
        foreach (var o in fsList) fs.Options.Add(o);
        var label = f.Add(new TextField { Label = L.T("Rótulo"), Value = p.Label ?? "", MaxLength = 32 });
        var c = f.Add(new ChoiceField { Label = L.T("Tamanho do cluster") });
        foreach (var x in Clusters) c.Options.Add(x == 0 ? L.T("Padrão") : $"{x / 1024} KB");
        var quick = f.Add(new ToggleField { Label = L.T("Formatação rápida"), Value = true, Hint = L.T("Sem ela, o Windows lê a partição inteira procurando setores ruins (demora).") });
        f.Note(L.T($"Tudo em {Who(p)} será apagado. A recuperação de dados ainda pode achar arquivos depois de uma formatação rápida — mas não conte com isso."), "danger");
        if (!await Main.ShowDialog(f)) return;
        string fsName = fsList[fs.SelectedIndex];
        int cl = Clusters[c.SelectedIndex];
        Main.Queue(L.T("Formatar partição"), L.T($"{Who(p)} → {fsList[fs.SelectedIndex]}, rótulo “{label.Value}” — apaga o conteúdo"), "Format", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await FormatRobust(q, fsName, FsLabel(fsName, label.Value), cl, quick.Value, s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    /// <summary>
    /// Criar no Windows em dois passos: a partição primeiro e só então a formatação (com planos B).
    /// Antes era um comando só, e uma recusa do Format-Volume deixava a partição criada e crua.
    /// </summary>
    internal static async Task<PartitionInfo?> CreateOnWindows(DiskInfo disk, long start, long bytes, string? fsName, string label, bool letter, OpSink s, int skipWindowsSteps = 0)
    {
        byte? mbr = disk.Table.Style == TableStyle.Mbr ? (fsName == "FAT32" ? (byte)0x0C : (byte)0x07) : null;
        var r = await WinCommands.CreatePartition(disk.Number, start, bytes, null, mbr, null, label, 0, letter, s.Token);
        Ensure(r, L.T("Criar partição"));
        long off = long.TryParse(r.Output.Trim().Split('\n').LastOrDefault()?.Trim(), out var o) ? o : start;
        s.Log(L.T($"Partição criada em {Fmt.Size(off)}."));
        PartitionInfo? np = null;
        for (int i = 0; i < 10 && np == null; i++)
        {
            await Task.Delay(700, s.Token);
            np = MainViewModel.Fresh(disk).Partitions.FirstOrDefault(x => !x.IsUnallocated && x.Offset == off);
        }
        if (np == null) throw new InvalidOperationException(L.T("A partição foi criada, mas o Windows ainda não a mostra — clique em Atualizar e use Formatar."));
        if (fsName != null) await FormatRobust(np, fsName, FsLabel(fsName, label), 0, true, s, skipWindowsSteps);
        return np;
    }

    /// <summary>FAT e exFAT guardam rótulo de no máximo 11 caracteres — o Windows recusa o resto.</summary>
    internal static string FsLabel(string fs, string label) => fs is "FAT32" or "exFAT" && label.Length > 11 ? label[..11].TrimEnd() : label;

    /// <summary>
    /// Formata pelo caminho que funcionar: Format-Volume do Windows; se ele recusar (acontece com
    /// cartões SD e pendrives), o format clássico; e, para FAT32, o formatador do próprio LibertyX
    /// (o mesmo que o autoteste prova com o chkdsk). Só desiste se todos falharem, e diz por quê.
    /// </summary>
    internal static async Task FormatRobust(PartitionInfo q, string fs, string label, int cluster, bool quick, OpSink s, int skipWindowsSteps = 0)
    {
        s.Progress(-1, L.T("Formatando"));
        if (fs == "FAT32" && (q.Disk.IsImage || !Win || q.Length > 32L << 30)) { OwnFat32(q, label, cluster, s); return; }
        var why = new List<string>();
        if (skipWindowsSteps < 1)
        {
            var r = await WinCommands.Format(q.Disk.Number, q.Offset, fs, label, cluster, quick, s.Token);
            if (r.Ok) return;
            why.Add("Format-Volume: " + r.Message);
            s.Log(L.T($"O Format-Volume recusou ({r.Message}). Tentando o format do Windows…"));
        }
        if (skipWindowsSteps < 2)
        {
            var lr = await WinCommands.AssignTemporaryLetter(q.Disk.Number, q.Offset);
            string letter = lr.Output.Trim();
            if (lr.Ok && letter.Length == 1 && char.IsLetter(letter[0]))
            {
                var f = await WinCommands.FormatCom(letter[0], fs, label, cluster, quick, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
                if (f.Ok)
                {
                    try { if (new DriveInfo(letter + ":\\").DriveFormat.Equals(fs, StringComparison.OrdinalIgnoreCase)) return; } catch { }
                    why.Add(L.T("format: terminou, mas o volume não ficou ") + fs);
                }
                else why.Add(L.T("format: ") + f.Message);
            }
            else why.Add(L.T("letra para o format: ") + lr.Message);
        }
        if (fs == "FAT32")
        {
            s.Log(L.T("Formatando FAT32 pelo próprio LibertyX…"));
            OwnFat32(MainViewModel.FreshPart(q), label, cluster, s);
            return;
        }
        throw new InvalidOperationException(L.T($"O Windows não conseguiu formatar em {fs}:\n") + string.Join("\n", why) +
            L.T("\n\nSe for cartão SD, confira a trava lateral de proteção contra gravação. FAT32 funciona sempre — o LibertyX formata sozinho."));
    }

    private static void OwnFat32(PartitionInfo q, string label, int cluster, OpSink s)
    {
        using (var dev = DiskSystem.OpenDisk(q.Disk, true))
        using (DiskSystem.Native.LockVolumes(q.Disk, [q]))
            Fat32Formatter.Format(new SubDevice(dev, q.Offset, q.Length), label, cluster, q.StartLba, new Progress<double>(x => s.Progress(x, L.T("Formatando FAT32"))));
        DiskSystem.Native.Rescan(q.Disk);
        if (q.Disk.Table.Style == TableStyle.Mbr && q.Entry is { } e && e.MbrType != 0x0C)
        {
            if (Win && !q.Disk.IsImage) _ = WinCommands.SetMbrType(q.Disk.Number, q.Offset, 0x0C).Result;
            else DiskOperations.ChangeTypeRaw(q, null, 0x0C, s);
        }
    }

    private static async Task Delete(PartitionVM pv)
    {
        var p = pv.P;
        string extra = p.IsEsp ? L.T("\n\nEsta é a partição de sistema EFI: sem ela, um Windows instalado neste disco não inicia.") : p.IsRecovery ? L.T("\n\nEsta é a partição de recuperação do Windows.") : "";
        if (!await Main.Confirm(L.T("Excluir partição"), L.T($"{Who(p)} ({FsDetect.Name(p.FileSystem)}, {Fmt.Size(p.Length)}) será removida da tabela.{extra}\n\nOs dados continuam no disco até serem sobrescritos — Recuperar partição consegue trazê-la de volta."), L.T("Adicionar"), true)) return;
        Main.Queue(L.T("Excluir partição"), $"{Who(p)} ({FsDetect.Name(p.FileSystem)}, {Fmt.Size(p.Length)}) · {p.Disk.Title}", "Delete", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
        {
            if (Win && !q.Disk.IsImage) Ensure(await WinCommands.Delete(q.Disk.Number, q.Offset), L.T("Excluir"));
            else DiskOperations.DeletePartitionRaw(q, s);
        }, s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    private static async Task Align(PartitionVM pv)
    {
        var p = pv.P;
        var target = DiskOperations.AlignedTarget(p);
        if (target == null) { await Main.Alert(L.T("Sem espaço para alinhar"), L.T("Não há espaço livre ao lado da partição para deslocá-la até um limite de 1 MB. Reduza-a um pouco antes.")); return; }
        if (!await Main.Confirm(L.T("Alinhar partição"), L.T($"{Who(p)} começa no setor {p.StartLba:N0}, fora do limite de 1 MB — em SSD e discos de 4K isso faz cada escrita tocar dois blocos físicos.\n\nEla será deslocada para o setor {target:N0}. Leva o tempo de copiar {Fmt.Size(p.Length)} e o computador não pode desligar no meio."), L.T("Adicionar"))) return;
        Main.Queue(L.T("Alinhar partição"), L.T($"{Who(p)}: setor {p.StartLba:N0} → {target:N0} (move os dados)"), "Align", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(s => DiskOperations.MovePartition(q, target.Value, s), s);
        }, p.Disk, dangerous: false, canPause: true);
    }

    // ───────────── rótulo / letra / tipo / série / ativa ─────────────

    private static async Task SetLabel(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form(L.T("Definir rótulo"), "Label", Who(p));
        var t = f.Add(new TextField { Label = L.T("Rótulo"), Value = p.Label ?? "", MaxLength = FsDetect.IsFat(p.FileSystem) ? 11 : 32 });
        if (!await Main.ShowDialog(f)) return;
        Main.Queue(L.T("Definir rótulo"), $"{Who(p)} → “{t.Value}”", "Label", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s => Ensure(await WinCommands.SetLabel(q.Disk.Number, q.Offset, t.Value), L.T("Rótulo")), s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task SetLetter(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form(L.T("Alterar letra da unidade"), "Letter", Who(p));
        var c = f.Add(new ChoiceField { Label = L.T("Letra") });
        c.Options.Add(L.T("(nenhuma — ocultar do Explorador)"));
        var letters = FreeLetters();
        if (p.Letter != null) letters.Insert(0, p.Letter[0]);
        foreach (var l in letters.Distinct()) c.Options.Add(l + ":");
        c.SelectedIndex = p.Letter != null ? 1 : Math.Min(1, c.Options.Count - 1);
        if (!await Main.ShowDialog(f)) return;
        char? nl = c.SelectedIndex == 0 ? null : c.Options[c.SelectedIndex][0];
        if (nl != null && p.Letter != null && nl == p.Letter[0]) return;
        Main.Queue(L.T("Alterar letra"), $"{Who(p)} → {(nl == null ? L.T("sem letra") : nl + ":")}", "Letter", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s => Ensure(await WinCommands.SetLetter(q.Disk.Number, q.Offset, nl, q.Letter), L.T("Letra")), s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task ChangeType(PartitionVM pv)
    {
        var p = pv.P;
        bool gpt = p.Disk.Table.Style == TableStyle.Gpt;
        var f = Form(L.T("Alterar ID de tipo"), "TypeId", L.T($"{Who(p)} · atual: {(gpt ? PartitionTypes.GptName(p.Entry!.TypeGuid) : $"0x{p.Entry!.MbrType:X2} {PartitionTypes.MbrName(p.Entry.MbrType)}")}"));
        var c = f.Add(new ChoiceField { Label = L.T("Novo tipo") });
        if (gpt) foreach (var (_, n) in PartitionTypes.CommonGpt) c.Options.Add(n);
        else foreach (var b in PartitionTypes.CommonMbr) c.Options.Add($"0x{b:X2} · {PartitionTypes.MbrName(b)}");
        c.Options.Add(L.T("Outro (digitar)"));
        var custom = f.Add(new TextField { Label = gpt ? L.T("GUID do tipo") : L.T("Byte do tipo (hex)"), Watermark = gpt ? "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" : "07", IsVisible = false });
        c.Changed += i => custom.IsVisible = i == c.Options.Count - 1;
        f.Note(L.T("O ID de tipo diz ao sistema o que é a partição. Um tipo errado pode esconder o volume ou impedir o boot — mude só se souber por quê."), "warn");
        f.Validate = () =>
        {
            if (c.SelectedIndex != c.Options.Count - 1) return null;
            return gpt ? (Guid.TryParse(custom.Value, out _) ? null : L.T("GUID inválido.")) : (byte.TryParse(custom.Value.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out _) ? null : L.T("Byte inválido."));
        };
        if (!await Main.ShowDialog(f)) return;
        bool isCustom = c.SelectedIndex == c.Options.Count - 1;
        Guid? g = gpt ? (isCustom ? Guid.Parse(custom.Value) : PartitionTypes.CommonGpt[c.SelectedIndex].id) : null;
        byte? m = !gpt ? (isCustom ? byte.Parse(custom.Value.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber) : PartitionTypes.CommonMbr[c.SelectedIndex]) : null;
        Main.Queue(L.T("Alterar ID de tipo"), $"{Who(p)} · {p.Disk.Title} ({p.Disk.Model})", "TypeId", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
        {
            if (Win && !q.Disk.IsImage)
                Ensure(g != null ? await WinCommands.SetGptType(q.Disk.Number, q.Offset, g.Value) : await WinCommands.SetMbrType(q.Disk.Number, q.Offset, m!.Value), L.T("Tipo"));
            else DiskOperations.ChangeTypeRaw(q, g, m, s);
        }, s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task ChangeSerial(PartitionVM pv)
    {
        var p = pv.P;
        ulong cur = 0;
        try { using var v = DiskSystem.OpenPartition(p); cur = BootSector.ReadSerial(v, p.FileSystem); } catch { }
        var f = Form(L.T("Alterar número de série"), "Serial", $"{Who(p)} · {FsDetect.Name(p.FileSystem)}");
        var t = f.Add(new TextField { Label = L.T("Número de série (hexadecimal)"), Value = BootSector.FormatSerial(cur, p.FileSystem) });
        f.Note(L.T("Alguns programas licenciados se prendem ao número de série do volume. Trocar pode desativá-los."), "warn");
        f.Validate = () => BootSector.TryParseSerial(t.Value, p.FileSystem, out _) ? null : L.T($"Use até {(p.FileSystem == FsKind.Ntfs ? 16 : 8)} dígitos hexadecimais.");
        if (!await Main.ShowDialog(f)) return;
        BootSector.TryParseSerial(t.Value, p.FileSystem, out var serial);
        Main.Queue(L.T("Alterar número de série"), $"{Who(p)} → {t.Value}", "Serial", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(s => DiskOperations.ChangeSerial(q, serial, s), s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task SetActive(PartitionVM pv)
    {
        var p = pv.P;
        bool active = !p.Entry!.Active;
        if (active && !await Main.Confirm(L.T("Definir como ativa"), L.T($"Em discos MBR, a BIOS inicia pela partição ativa. Só uma pode ser ativa: as outras deste disco serão desmarcadas."), L.T("Adicionar"))) return;
        Main.Queue(active ? L.T("Definir como ativa") : L.T("Desmarcar como ativa"), $"{Who(p)} · {p.Disk.Title} ({p.Disk.Model})", "Active", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await Exec(async s =>
        {
            if (Win && !q.Disk.IsImage) Ensure(await WinCommands.SetActive(q.Disk.Number, q.Offset, active), L.T("Ativa"));
            else
            {
                var t = q.Disk.Table.Clone();
                foreach (var e in t.Entries) e.Active = active && e.StartLba == q.Entry.StartLba;
                DiskOperations.WriteTable(q.Disk, t, L.T("Partição ativa"), s);
            }
        }, s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    // ───────────── verificar / propriedades ─────────────

    private static async Task CheckFs(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form(L.T("Verificar sistema de arquivos"), "CheckFs", Who(p), L.T("Verificar"));
        var mode = f.Add(new ChoiceField { Label = L.T("O que fazer") });
        mode.Options.Add(L.T("Só verificar (não altera nada)"));
        mode.Options.Add(L.T("Verificar e corrigir erros"));
        mode.Options.Add(L.T("Corrigir erros e procurar setores defeituosos (lento)"));
        f.Note(L.T("Em disco que está morrendo, corrigir pode piorar: faça antes uma imagem (Backup e imagem) e recupere dela."), "warn");
        if (p.IsSystemVolume) f.Note(L.T("No volume do Windows, a correção é agendada para a próxima reinicialização."), "gold");
        if (!await Main.ShowDialog(f)) return;
        string target = p.Letter ?? p.VolumePath!.TrimEnd('\\');
        Func<OpSink, Task> work = async s =>
        {
            s.Progress(-1, L.T("chkdsk em andamento"));
            var r = await WinCommands.Chkdsk(target, mode.SelectedIndex >= 1, mode.SelectedIndex == 2, s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
            s.Log(r.ExitCode switch
            {
                0 => L.T("Nenhum problema encontrado."),
                1 => L.T("Erros encontrados e corrigidos."),
                2 => L.T("Limpeza feita (sem erros graves)."),
                _ => L.T("Foram encontrados problemas que não puderam ser corrigidos agora."),
            });
        };
        if (mode.SelectedIndex == 0) await Main.Run(L.T("Verificando ") + Who(p), work, refresh: false);
        else Main.Queue(mode.SelectedIndex == 2 ? L.T("Corrigir erros e setores ruins") : L.T("Corrigir erros do sistema de arquivos"), L.T($"chkdsk em {Who(p)}"), "CheckFs", work, p.Disk);
    }

    private static Task Properties(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form(L.T("Propriedades da partição"), "Properties", pv.Title, L.T("Fechar"));
        void Row(string k, string v) => f.Add(new InfoField { Label = k, Text = v });
        Row(L.T("Sistema de arquivos"), pv.FsText);
        Row(L.T("Capacidade"), L.T($"{Fmt.Size(p.Length)} ({p.Length:N0} bytes)"));
        Row(L.T("Usado / livre"), $"{pv.UsedText} / {pv.FreeText}");
        Row(L.T("Disco"), $"{p.Disk.Title} · {p.Disk.Model}");
        Row(L.T("Tipo"), p.TypeText);
        Row(L.T("Primeiro setor"), $"{p.StartLba:N0}");
        Row(L.T("Último setor"), $"{p.StartLba + p.Sectors - 1:N0}");
        Row(L.T("Alinhamento"), DiskOperations.IsAligned(p) ? L.T("Alinhada a 1 MB") : L.T("Desalinhada"));
        if (p.Entry?.UniqueGuid is { } g && g != Guid.Empty) Row(L.T("GUID da partição"), g.ToString().ToUpperInvariant());
        if (p.Entry != null && p.Disk.Table.Style == TableStyle.Gpt) Row(L.T("Atributos GPT"), $"0x{p.Entry.Attributes:X16}");
        if (p.VolumeSerial != 0) Row(L.T("Nº de série do volume"), $"{p.VolumeSerial >> 16:X4}-{p.VolumeSerial & 0xFFFF:X4}");
        if (p.VolumePath != null) Row(Win ? L.T("Volume") : L.T("Dispositivo"), p.VolumePath);
        if (p.MountPoints.Count > 0) Row(L.T("Montada em"), string.Join(", ", p.MountPoints));
        if (!string.IsNullOrEmpty(p.FsUuid)) Row(L.T("UUID do sistema de arquivos"), p.FsUuid!);
        if (p.Entry is { Name.Length: > 0 } && p.Disk.Table.Style == TableStyle.Gpt) Row(L.T("Nome GPT (PARTLABEL)"), p.Entry.Name);
        if (p.HeldBy != null) Row(L.T("Aberta como"), p.HeldBy);
        if (p.StatusText != "—") Row(L.T("Status"), p.StatusText);
        return Main.ShowDialog(f);
    }
}

public static class RecoveryNames
{
    public static string Safe(string s) => Core.Recovery.RecoveryExporter.Sanitize(s);
}
