using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Partitioning;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.Core.Platform.Linux;

namespace LibertyX.Partition.ViewModels;

/// <summary>
/// As ações de partição no Linux. A tabela e a movimentação de dados são do motor do LibertyX
/// (iguais às do Windows); formatar, redimensionar o conteúdo, verificar, rótulo e UUID ficam
/// com as ferramentas oficiais de cada sistema de arquivos; montar e a swap, com o kernel.
/// </summary>
public static partial class ActionsBuilder
{
    private static bool Lin => OperatingSystem.IsLinux();

    private static string? LinPartLock(PartitionInfo p) =>
        p.Disk.ReadOnly ? "Disco só para leitura (mídia protegida contra gravação)." :
        p.IsSystemCritical ? $"Em uso pelo sistema ({(p.MountPoints.Count > 0 ? string.Join(", ", p.MountPoints) : "swap")}). Para alterar, inicie por um pendrive Linux." :
        p.Disk.IsVirtual && !p.IsWholeDisk ? null : null;

    /// <summary>"ext4", "XFS v5", "LUKS2"… — mais exato que o nome genérico da família.</summary>
    private static string FsName(PartitionInfo p) => p.FsVariant ?? FsDetect.Name(p.FileSystem);

    private static string? NeedsDev(PartitionInfo p) => p.DeviceNode == null ? "O kernel ainda não vê esta partição — clique em Atualizar." : null;

    private static string? HeldLock(PartitionInfo p) =>
        p.KernelName != null && LinuxDiskSystem.HoldersOf(p.KernelName).Count > 0 ? $"Aberta como {p.HeldBy}. Feche antes (Criptografia → Fechar, ou desative o LVM)." : null;

    public static IEnumerable<ActionGroup> ForPartitionLinux(PartitionVM pv)
    {
        var p = pv.P;
        if (p.IsUnallocated)
        {
            yield return new ActionGroup("Espaço livre", [
                ActionItem.Of("Criar partição", "Create", () => CreatePartitionLinux(pv), p.Disk.Table.Style == TableStyle.None ? "Inicialize o disco primeiro." : !p.Disk.PartitionsEditable ? "Disco só para leitura." : null),
                ActionItem.Of("Recuperar dados desta área", "RecoverFiles", () => { Main.Navigate("recuperar", p); return Task.CompletedTask; }),
                ActionItem.Of("Procurar partição perdida aqui", "RecoverPartition", () => { Main.Navigate("particoes", p.Disk); return Task.CompletedTask; }),
                ActionItem.Of("Teste de superfície", "Surface", () => { Main.Navigate("superficie", p); return Task.CompletedTask; }),
            ]);
            yield break;
        }
        string? lockR = LinPartLock(p);
        string? held = HeldLock(p);
        bool table = p.Entry != null && p.Disk.PartitionsEditable;
        var spec = LinuxFs.SpecOf(p);
        var (before, after) = Neighbors(pv);
        bool mountable = !FsDetect.IsContainer(p.FileSystem) && p.FileSystem is not (FsKind.Unknown or FsKind.Empty or FsKind.LinuxSwap);

        yield return new ActionGroup("Alterar partição", [
            ActionItem.Of("Mover / redimensionar", "Resize", () => ResizeMoveLinux(pv),
                !table ? (p.IsWholeDisk ? "Volume sem tabela de partições." : "Disco só para leitura.") : held ?? NeedsDev(p)),
            ActionItem.Of("Estender partição", "Extend", () => ExtendLinux(pv),
                !table ? "Sem tabela de partições." : held ?? (spec?.Grow != true ? $"{FsDetect.Name(p.FileSystem)} não cresce por ferramenta confiável." : (before?.IsUnallocated != true && after?.IsUnallocated != true) ? "Não há espaço livre ao lado. Reduza ou mova a vizinha primeiro." : null)),
            ActionItem.Of("Dividir partição", "Split", () => SplitLinux(pv),
                !table ? "Sem tabela de partições." : lockR ?? held ?? (spec?.Shrink != true || p.FileSystem == FsKind.LinuxSwap ? $"{FsDetect.Name(p.FileSystem)} não diminui — não dá para dividir." : null)),
            ActionItem.Of("Unir com a próxima", "Merge", () => MergeLinux(pv),
                !table ? "Sem tabela de partições." : lockR ?? held ?? (!mountable || spec?.Grow != true ? "A partição precisa ter um sistema de arquivos que cresça (ext4, XFS, Btrfs, NTFS)." :
                after == null || after.IsUnallocated ? "Não há partição logo depois desta." : LinPartLock(after) ?? HeldLock(after) ?? (FsDetect.IsContainer(after.FileSystem) || after.FileSystem is FsKind.Unknown or FsKind.LinuxSwap ? "A partição seguinte precisa ter arquivos legíveis." : null))),
            ActionItem.Of("Converter sistema de arquivos", "FsConvert", () => ConvertFsLinux(pv),
                lockR ?? held ?? NeedsDev(p) ?? (!mountable ? "Só partições com arquivos (ext, XFS, Btrfs, NTFS, FAT, exFAT, F2FS)." : null)),
            ActionItem.Of("Alterar tamanho do bloco", "Cluster", () => ConvertFsLinux(pv, sameFs: true),
                lockR ?? held ?? NeedsDev(p) ?? (!mountable ? "Só partições com arquivos." : null)),
        ]);

        yield return new ActionGroup("Gerenciar partição", [
            ActionItem.Of("Formatar", "Format", () => FormatLinux(pv), lockR ?? held ?? NeedsDev(p), true),
            ActionItem.Of("Excluir partição", "Delete", () => DeleteLinux(pv), !table ? "Sem tabela de partições." : lockR ?? held, true),
            ActionItem.Of(p.IsMounted ? "Desmontar" : "Montar", p.IsMounted ? "Eject" : "Mount", () => MountToggle(pv),
                p.IsSystemCritical && p.IsMounted ? "Em uso pelo sistema." : NeedsDev(p) ?? (p.IsMounted ? null : !mountable ? (FsDetect.IsContainer(p.FileSystem) ? "Recipiente: abra primeiro em Criptografia (LUKS/BitLocker) ou ative o LVM." : "Sem sistema de arquivos montável.") : held)),
            .. (p.FileSystem == FsKind.LinuxSwap ? new[] { ActionItem.Of(p.HasPageFile ? "Desligar swap" : "Ligar swap", "Swap", () => SwapToggle(pv), NeedsDev(p) ?? (p.HasPageFile && p.InUseBySystem && !p.HasPageFile ? "Em uso." : null)) } : []),
            .. (p.IsMounted ? new[] { ActionItem.Of("Abrir no gerenciador de arquivos", "Explore", () => { LinuxSession.OpenAsUser(p.MountPoints[0]); return Task.CompletedTask; }) } : []),
            ActionItem.Of(p.Disk.IsSsd == false ? "Otimizar (desfragmentar)" : "Otimizar (TRIM)", "Benchmark", () => OptimizeLinux(pv),
                !p.IsMounted ? "Monte a partição primeiro (a otimização roda com o volume em uso)." : p.Disk.IsSsd == false && p.FileSystem is not (FsKind.Ext or FsKind.Xfs or FsKind.Btrfs) ? $"{FsDetect.Name(p.FileSystem)} não tem desfragmentador no Linux." : null),
            ActionItem.Of("Copiar partição", "CopyPartition", () => { Main.Navigate("copiar-particao", p); return Task.CompletedTask; }),
            ActionItem.Of("Alinhar partição", "Align", () => Align(pv), !table ? "Sem tabela de partições." : lockR ?? held ?? (DiskOperations.IsAligned(p) ? "Já está alinhada a 1 MB." : null)),
            ActionItem.Of("Limpar partição", "Wipe", () => { Main.Navigate("limpar", p); return Task.CompletedTask; }, lockR ?? held, true),
        ]);

        bool serialKind = p.FileSystem is FsKind.Ntfs or FsKind.ExFat || FsDetect.IsFat(p.FileSystem);
        yield return new ActionGroup("Identidade", [
            ActionItem.Of("Definir rótulo", "Label", () => SetLabelLinux(pv), NeedsDev(p) ?? (p.FileSystem is FsKind.Unknown or FsKind.Empty or FsKind.LvmPv or FsKind.MdRaid or FsKind.BitLocker ? "Sem sistema de arquivos com rótulo." : null)),
            ActionItem.Of(serialKind ? "Alterar número de série" : "Alterar UUID", "Serial", () => serialKind ? ChangeSerial(pv) : SetUuidLinux(pv),
                lockR ?? held ?? NeedsDev(p) ?? (!serialKind && !LinuxFs.UuidByTool(p.FileSystem) ? $"UUID de {FsDetect.Name(p.FileSystem)} não é alterado aqui." : null)),
            .. (p.Entry != null && p.Disk.Table.Style == TableStyle.Gpt ? new[] { ActionItem.Of("Nome da partição (GPT)", "Label", () => SetGptName(pv), table ? null : "Disco só para leitura.") } : []),
            ActionItem.Of("Alterar ID de tipo", "TypeId", () => ChangeType(pv), !table ? "Sem tabela de partições." : null),
            ActionItem.Of("Sinalizadores (boot, esp, oculta…)", "Active", () => FlagsLinux(pv), !table ? "Sem tabela de partições." : null),
        ]);

        yield return new ActionGroup("Verificar partição", [
            ActionItem.Of("Verificar sistema de arquivos", "CheckFs", () => CheckLinux(pv), NeedsDev(p) ?? held ?? (!mountable || p.FileSystem is FsKind.Jfs or FsKind.ReiserFs or FsKind.Bcachefs or FsKind.Squashfs or FsKind.HfsPlus or FsKind.Apfs or FsKind.ReFS ? "Sem verificador para este sistema de arquivos." : null)),
            ActionItem.Of("Explorar partição", "Explore", () => { Main.Navigate("recuperar", new RecoveryTarget(p, true)); return Task.CompletedTask; }),
            ActionItem.Of("Teste de superfície", "Surface", () => { Main.Navigate("superficie", p); return Task.CompletedTask; }),
            ActionItem.Of("Propriedades da partição", "Properties", () => Properties(pv)),
            ActionItem.Of("Recuperação de dados", "RecoverFiles", () => { Main.Navigate("recuperar", p); return Task.CompletedTask; }),
            .. (p.FileSystem is FsKind.Luks or FsKind.BitLocker ? new[] { ActionItem.Of(p.HeldBy != null ? "Criptografia (fechar, chaves)" : "Desbloquear", "BitLocker", () => { Main.Navigate("bitlocker", p); return Task.CompletedTask; }) } : []),
        ]);
    }

    // ───────── criar ─────────

    /// <summary>Itens da escolha de sistema de arquivos: os do Linux, a versão criptografada e "não formatar".</summary>
    private static List<(string key, string text)> FsChoices(bool allowLuks = true)
    {
        var l = LinuxFs.Formats.Select(f => (f.Key, $"{f.Name} — {f.Note}" + (LinuxTools.Has(f.Mkfs) || f.Key == "fat32" ? "" : $" (falta {LinuxTools.PackageOf(f.Mkfs)})"))).ToList();
        if (allowLuks) l.Insert(1, ("luks", "ext4 criptografado (LUKS2) — pede a senha para abrir" + (LinuxTools.Has("cryptsetup") ? "" : " (falta cryptsetup)")));
        return l;
    }

    private static async Task CreatePartitionLinux(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form("Criar partição", "Create", $"{Fmt.Size(p.Length)} livres em {p.Disk.Title}");
        var size = f.Add(new SizeField { Label = "Tamanho", MinMb = 2, MaxMb = Math.Floor(p.Length / (double)MB) - 1, ValueMb = Math.Floor(p.Length / (double)MB) - 1 });
        var choices = FsChoices();
        choices.Add(("none", "Não formatar (só reservar o espaço)"));
        var fs = f.Add(new ChoiceField { Label = "Sistema de arquivos" });
        foreach (var c in choices) fs.Options.Add(c.text);
        var label = f.Add(new TextField { Label = "Rótulo", Value = "", MaxLength = 32, Watermark = "Dados" });
        var pw = f.Add(new TextField { Label = "Senha da criptografia", Password = true, IsVisible = false });
        var pw2 = f.Add(new TextField { Label = "Repita a senha", Password = true, IsVisible = false });
        var esp = f.Add(new ToggleField { Label = "Partição de sistema EFI (ESP)", Hint = "Para instalar um sistema que inicia por UEFI. Use FAT32, 300 MB a 1 GB.", IsVisible = false });
        var bios = f.Add(new ToggleField { Label = "BIOS boot (GRUB em disco GPT com BIOS antigo)", Hint = "1 MB sem formatação; o GRUB grava o núcleo dele aqui.", IsVisible = false });
        void Upd(int i)
        {
            string k = choices[i].key;
            pw.IsVisible = pw2.IsVisible = k == "luks";
            esp.IsVisible = k == "fat32";
            bios.IsVisible = k == "none" && p.Disk.Table.Style == TableStyle.Gpt;
        }
        fs.Changed += Upd;
        Upd(0);
        bool mbr = p.Disk.Table.Style == TableStyle.Mbr;
        int prim = p.Disk.Table.Entries.Count(e => !e.IsLogical) + (p.Disk.Table.Entries.Any(e => e.IsLogical) ? 1 : 0);
        if (mbr && prim >= 4) f.Note("O MBR já tem 4 entradas primárias: não cabe outra. Converta o disco para GPT (sem perder dados) ou exclua uma.", "danger");
        if (p.Disk.IsSystem) f.Note("Disco do sistema: a partição nova é criada no espaço livre sem tocar nas que estão em uso — o kernel é avisado na hora.", "gold");
        f.Validate = () =>
        {
            if (mbr && prim >= 4) return "MBR cheio (4 primárias).";
            string k = choices[fs.SelectedIndex].key;
            if (k != "none" && k != "luks" && LinuxFs.Formats.First(x => x.Key == k) is { } sp && !LinuxTools.Has(sp.Mkfs) && k != "fat32") return LinuxTools.Missing(sp.Mkfs);
            if (k == "luks" && (pw.Value.Length < 8 || pw.Value != pw2.Value)) return pw.Value.Length < 8 ? "Senha de pelo menos 8 caracteres." : "As senhas não conferem.";
            if (k == "fat32" && size.Bytes < 33L << 20) return "FAT32 precisa de pelo menos 33 MB.";
            return null;
        };
        if (!await Main.ShowDialog(f)) return;
        string key = choices[fs.SelectedIndex].key;
        long start = (p.Offset + MB - 1) / MB * MB;
        long bytes = Math.Min(size.Bytes, (p.Offset + p.Length - start) / MB * MB);
        string lbl = label.Value.Trim(), pass = pw.Value;
        bool isEsp = esp.Value && key == "fat32", isBios = bios.Value && key == "none";
        string fsText = key == "none" ? (isBios ? "BIOS boot" : "sem formatação") : key == "luks" ? "ext4 criptografado" : LinuxFs.Formats.First(x => x.Key == key).Name;
        Main.Queue("Criar partição", $"{Fmt.Size(bytes)} em {fsText}{(lbl.Length > 0 ? $", rótulo “{lbl}”" : "")} · {p.Disk.Title}", "Create", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await CreateLinux(q.Disk, start, bytes, key, lbl, s, pass, isEsp, isBios);
        }, p.Disk, dangerous: false, canPause: false);
    }

    /// <summary>
    /// Criar no Linux: a entrada na tabela (motor próprio, o kernel é avisado), espera o /dev
    /// aparecer e formata. <paramref name="key"/>: um de <see cref="LinuxFs.Formats"/>, "luks" ou "none".
    /// </summary>
    internal static async Task<string?> CreateLinux(DiskInfo disk, long start, long bytes, string key, string lbl, OpSink s, string? password = null, bool esp = false, bool biosBoot = false)
    {
        var t = disk.Table.Clone();
        var kind = key switch { "luks" => FsKind.Luks, "none" => FsKind.Unknown, _ => LinuxFs.Formats.First(x => x.Key == key).Kind };
        var (g, m) = PartitionTypes.ForFileSystem(kind, esp);
        if (biosBoot) g = PartitionTypes.BiosBoot;
        var e = new PartitionEntry { StartLba = start / disk.SectorSize, SectorCount = bytes / disk.SectorSize, Name = lbl.Length > 0 ? lbl : esp ? "EFI system partition" : "" };
        if (t.Style == TableStyle.Gpt) { e.TypeGuid = g; e.UniqueGuid = Guid.NewGuid(); }
        else e.MbrType = key == "none" ? (byte)0x83 : m;
        t.Entries.Add(e);
        DiskOperations.WriteTable(disk, t, "Criar partição", s);
        if (key == "none" || disk.IsImage) return null;
        s.Progress(-1, "Esperando o kernel criar o dispositivo");
        string dev = KernelSync.WaitPartitionNode(disk, start) ?? throw new InvalidOperationException("A partição foi criada, mas o kernel não criou o /dev dela. Clique em Atualizar e use Formatar.");
        s.Log($"Partição criada: {dev}");
        using (AutomountInhibitor.Start())
        {
            if (key == "luks") await LinuxFs.MkfsEncrypted(dev, "ext4", lbl, password!, s);
            else await LinuxFs.Mkfs(dev, key, lbl, 0, true, s);
        }
        return dev;
    }

    // ───────── formatar / excluir ─────────

    private static readonly int[] Blocks = [0, 1024, 2048, 4096, 8192, 16384, 32768, 65536];

    private static async Task FormatLinux(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form("Formatar partição", "Format", $"{Who(p)} · {FsName(p)} · {Fmt.Size(p.Length)}", "Adicionar", true);
        var choices = FsChoices();
        var fs = f.Add(new ChoiceField { Label = "Sistema de arquivos" });
        foreach (var c in choices) fs.Options.Add(c.text);
        int cur = choices.FindIndex(c => LinuxFs.SpecOf(p)?.Key == c.key);
        fs.SelectedIndex = Math.Max(0, cur);
        var label = f.Add(new TextField { Label = "Rótulo", Value = p.Label ?? "", MaxLength = 32 });
        var bs = f.Add(new ChoiceField { Label = "Tamanho do bloco / cluster" });
        foreach (var x in Blocks) bs.Options.Add(x == 0 ? "Padrão (recomendado)" : x >= 1024 ? $"{x / 1024} KB" : $"{x} B");
        var quick = f.Add(new ToggleField { Label = "Formatação rápida", Value = true, Hint = "Sem ela, ext e FAT passam um teste de setores ruins (badblocks) antes, e o NTFS zera a partição inteira." });
        var pw = f.Add(new TextField { Label = "Senha da criptografia", Password = true, IsVisible = false });
        var pw2 = f.Add(new TextField { Label = "Repita a senha", Password = true, IsVisible = false });
        void Upd(int i) { pw.IsVisible = pw2.IsVisible = choices[i].key == "luks"; bs.IsVisible = choices[i].key is not ("luks" or "swap" or "btrfs" or "f2fs"); }
        fs.Changed += Upd;
        Upd(fs.SelectedIndex);
        if (p.IsMounted) f.Note($"Ela está montada em {p.MountPoints[0]}: será desmontada antes.", "gold");
        f.Note($"Tudo em {Who(p)} será apagado. A recuperação de dados ainda pode achar arquivos depois de uma formatação rápida — mas não conte com isso.", "danger");
        f.Validate = () =>
        {
            string k = choices[fs.SelectedIndex].key;
            if (k == "luks") return pw.Value.Length < 8 ? "Senha de pelo menos 8 caracteres." : pw.Value != pw2.Value ? "As senhas não conferem." : LinuxTools.Missing("cryptsetup");
            var sp = LinuxFs.Formats.First(x => x.Key == k);
            if (k != "fat32" && !LinuxTools.Has(sp.Mkfs)) return LinuxTools.Missing(sp.Mkfs);
            if (k == "fat32" && p.Length < 33L << 20) return "FAT32 precisa de pelo menos 33 MB.";
            if (k == "fat16" && p.Length > 4L << 30) return "FAT16 vai até 4 GB.";
            if (k == "xfs" && p.Length < 300L << 20) return "XFS precisa de pelo menos 300 MB.";
            if (k == "btrfs" && p.Length < 114L << 20) return "Btrfs precisa de pelo menos 114 MB.";
            return null;
        };
        if (!await Main.ShowDialog(f)) return;
        string key = choices[fs.SelectedIndex].key, lbl = label.Value.Trim(), pass = pw.Value;
        int block = Blocks[bs.SelectedIndex];
        bool q1 = quick.Value;
        Main.Queue("Formatar partição", $"{Who(p)} → {(key == "luks" ? "ext4 criptografado" : LinuxFs.Formats.First(x => x.Key == key).Name)}{(lbl.Length > 0 ? $", rótulo “{lbl}”" : "")} — apaga o conteúdo", "Format", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await FormatLinuxCore(q, key, lbl, block, q1, pass, s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    internal static async Task FormatLinuxCore(PartitionInfo q, string key, string lbl, int block, bool quick, string? password, OpSink s)
    {
        if (q.IsSystemCritical) throw new InvalidOperationException("Partição em uso pelo sistema.");
        if (q.DeviceNode == null) throw new InvalidOperationException("O kernel não vê esta partição — clique em Atualizar.");
        if (q.IsMounted || q.HasPageFile) { s.Log($"Desmontando {q.KernelName}…"); await LinuxFs.Unmount(q, s); }
        using (var inhibit = AutomountInhibitor.Start())
        {
            if (key == "luks") await LinuxFs.MkfsEncrypted(q.DeviceNode, "ext4", lbl, password!, s);
            else await LinuxFs.Mkfs(q.DeviceNode, key, lbl, block, quick, s);
        }
        // o tipo na tabela acompanha o conteúdo (o systemd e o Windows se guiam por ele)
        if (q.Entry != null && q.Disk.PartitionsEditable)
        {
            var kind = key == "luks" ? FsKind.Luks : LinuxFs.Formats.First(x => x.Key == key).Kind;
            var (g, m) = PartitionTypes.ForFileSystem(kind, q.IsEsp && kind == FsKind.Fat32);
            bool gpt = q.Disk.Table.Style == TableStyle.Gpt;
            // tipos Linux mais específicos (raiz, /home, /var…) continuam valendo para um sistema Linux
            Guid[] linuxData = [PartitionTypes.LinuxFs, PartitionTypes.LinuxRootX64, PartitionTypes.LinuxRootArm64, PartitionTypes.LinuxHome, PartitionTypes.LinuxSrv, PartitionTypes.LinuxVar, PartitionTypes.LinuxXbootldr];
            bool keep = gpt ? q.Entry.TypeGuid == g || (g == PartitionTypes.LinuxFs && linuxData.Contains(q.Entry.TypeGuid))
                            : q.Entry.MbrType == m || (m == 0x0C && q.Entry.MbrType == 0x0B);
            if (!keep)
            {
                var fresh = MainViewModel.FreshPart(q);
                DiskOperations.ChangeTypeRaw(fresh, gpt ? g : null, gpt ? null : m, s);
            }
        }
    }

    private static async Task DeleteLinux(PartitionVM pv)
    {
        var p = pv.P;
        string extra = p.IsEsp ? "\n\nEsta é a partição de sistema EFI: sem ela, os sistemas instalados neste disco não iniciam." : p.IsRecovery ? "\n\nEsta é a partição de recuperação do Windows." : "";
        if (p.IsMounted) extra += $"\n\nEla está montada em {p.MountPoints[0]} e será desmontada.";
        if (!await Main.Confirm("Excluir partição", $"{Who(p)} ({FsDetect.Name(p.FileSystem)}, {Fmt.Size(p.Length)}) será removida da tabela.{extra}\n\nOs dados continuam no disco até serem sobrescritos — Recuperar partição consegue trazê-la de volta.", "Adicionar", true)) return;
        Main.Queue("Excluir partição", $"{Who(p)} ({FsDetect.Name(p.FileSystem)}, {Fmt.Size(p.Length)}) · {p.Disk.Title}", "Delete", s =>
        {
            var q = MainViewModel.FreshPart(p);
            DiskOperations.DeletePartitionRaw(q, s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    // ───────── mover / redimensionar / estender / dividir / unir ─────────

    /// <summary>
    /// Ordem segura: encolher o conteúdo → encolher a partição → mover → aumentar a partição →
    /// aumentar o conteúdo. Crescer para a direita com ext4/XFS/Btrfs montados acontece com a
    /// partição em uso (o kernel ajusta o tamanho na hora, como o growpart da nuvem).
    /// </summary>
    internal static async Task ApplyGeometryLinux(PartitionInfo p, long newStart, long newBytes, OpSink s)
    {
        var d = p.Disk;
        int ss = d.SectorSize;
        long oldOff = p.Offset, oldLen = p.Length;
        var spec = LinuxFs.SpecOf(p);
        if (newBytes < oldLen)
        {
            if (spec == null || !spec.Shrink) throw new InvalidOperationException($"{FsDetect.Name(p.FileSystem)} não diminui.");
            if (p.IsMounted && !spec.ShrinkMounted) { s.Log($"Desmontando {p.KernelName} para encolher…"); await LinuxFs.Unmount(p, s); p = MainViewModel.FreshPart(p); }
            s.Log($"Encolhendo o conteúdo de {Who(p)} para {Fmt.Size(newBytes)}…");
            await LinuxFs.ResizeFs(p, newBytes, grow: false, s);
            var t = p.Disk.Table.Clone();
            t.Entries.First(e => e.StartLba == p.Entry!.StartLba).SectorCount = newBytes / ss;
            DiskOperations.WriteTable(p.Disk, t, "Reduzir partição", s, growInPlace: null);
            p = Find(p, oldOff);
        }
        if (newStart != p.Offset)
        {
            DiskOperations.MovePartition(p, newStart / ss, s);
            p = Find(p, newStart);
        }
        if (newBytes > p.Length)
        {
            bool online = p.IsMounted && spec?.GrowMounted == true;
            if (p.IsMounted && !online) { s.Log($"Desmontando {p.KernelName} para crescer…"); await LinuxFs.Unmount(p, s); p = MainViewModel.FreshPart(p); }
            var t = p.Disk.Table.Clone();
            t.Entries.First(e => e.StartLba == p.Entry!.StartLba).SectorCount = newBytes / ss;
            DiskOperations.WriteTable(p.Disk, t, "Aumentar partição", s, growInPlace: online ? p : null);
            p = Find(p, p.Offset);
            if (spec?.Grow == true)
            {
                s.Log(online ? $"Aumentando o conteúdo com {p.KernelName} montada em {p.MountPoints[0]}…" : $"Aumentando o conteúdo de {Who(p)}…");
                await LinuxFs.ResizeFs(p, newBytes, grow: true, s);
            }
        }
    }

    private static PartitionInfo Find(PartitionInfo p, long offset)
    {
        var nd = MainViewModel.Fresh(p.Disk);
        return nd.Partitions.FirstOrDefault(x => !x.IsUnallocated && (p.Entry?.UniqueGuid is { } g && g != Guid.Empty ? x.Entry?.UniqueGuid == g : x.Offset == offset))
            ?? nd.Partitions.FirstOrDefault(x => !x.IsUnallocated && x.Offset == offset)
            ?? throw new InvalidOperationException("A partição sumiu da tabela no meio da operação — confira em Atualizar.");
    }

    private static async Task ResizeMoveLinux(PartitionVM pv)
    {
        var p = pv.P;
        var (before, after) = Neighbors(pv);
        long freeBefore = before?.IsUnallocated == true ? before.Length : 0;
        long freeAfter = after?.IsUnallocated == true ? after.Length : 0;
        var spec = LinuxFs.SpecOf(p);
        bool canMove = !p.IsSystemCritical;
        bool shrink = spec?.Shrink == true && (!p.IsSystemCritical || spec.ShrinkMounted) && p.FileSystem != FsKind.LinuxSwap || p.FileSystem == FsKind.LinuxSwap && !p.HasPageFile;
        bool grow = spec?.Grow == true;
        long min = p.Length, max = p.Length;
        if (shrink)
        {
                min = await Task.Run(() => LinuxFs.MinSize(p)) ?? (p.Used is { } u ? u + Math.Max(256L << 20, u / 10) : p.Length);
            min = Math.Min(min, p.Length);
        }
        if (grow) max = p.Length + freeAfter + (canMove ? freeBefore : 0);
        long regionStart = p.Offset - (canMove ? freeBefore : 0), regionEnd = p.Offset + p.Length + freeAfter;
        double origMb = Math.Round(p.Length / (double)MB), origBefore = Math.Round(freeBefore / (double)MB);

        var f = Form("Mover / redimensionar", "Resize", $"{Who(p)} · {FsName(p)} · {Fmt.Size(p.Length)}{(p.Used is { } us ? $" · {Fmt.Size(us)} usados" : "")}");
        var size = f.Add(new SizeField { Label = "Tamanho da partição", MinMb = Math.Ceiling(min / (double)MB), MaxMb = Math.Max(Math.Ceiling(min / (double)MB), Math.Floor(max / (double)MB)), ValueMb = origMb });
        SizeField? pos = null;
        if (canMove && (freeBefore > 0 || freeAfter > 0))
            pos = f.Add(new SizeField { Label = "Espaço livre antes da partição", MinMb = 0, MaxMb = Math.Floor((freeBefore + freeAfter) / (double)MB), ValueMb = origBefore });
        void Update()
        {
            double total = (regionEnd - regionStart) / (double)MB;
            double b = pos?.ValueMb ?? origBefore;
            double a = total - b - size.ValueMb;
            size.Caption = a < -0.5 ? "Não cabe: diminua o espaço antes ou o tamanho." : $"Livre depois: {Fmt.Size((long)Math.Max(0, a) * MB)}";
        }
        size.Changed += _ => Update();
        if (pos != null) pos.Changed += _ => Update();
        Update();
        if (!shrink && spec != null && !spec.Shrink) f.Note($"{spec.Name} não diminui — só cresce e muda de lugar.", "gold");
        if (spec == null) f.Note($"{FsDetect.Name(p.FileSystem)} não é redimensionado aqui; dá para mover a partição.", "gold");
        if (p.IsSystemCritical && grow) f.Note($"Em uso pelo sistema: dá para crescer para a direita com ela montada (sem reiniciar); mover ou reduzir exige iniciar por um pendrive.", "gold");
        else if (p.IsMounted) f.Note($"Montada em {p.MountPoints[0]}: será desmontada só se a operação exigir.", "gold");
        if (canMove) f.Note("Mover desloca todos os dados no disco: leva tempo e o computador não pode desligar no meio. Tenha backup do que importa.", "warn");
        f.Validate = () =>
        {
            double b = pos?.ValueMb ?? origBefore;
            long ns = Math.Abs(b - origBefore) < 0.5 ? p.Offset : (regionStart + (long)b * MB + MB - 1) / MB * MB;
            long nb = Math.Abs(size.ValueMb - origMb) < 0.5 ? p.Length : size.Bytes;
            if (ns + nb > regionEnd) return "A partição não cabe nessa posição com esse tamanho.";
            if (ns == p.Offset && nb == p.Length) return "Nada mudou.";
            if (p.IsSystemCritical && (ns != p.Offset || nb < p.Length)) return "Partição em uso: só dá para crescer para a direita.";
            return null;
        };
        if (!await Main.ShowDialog(f)) return;
        double bb = pos?.ValueMb ?? origBefore;
        long nsF = Math.Abs(bb - origBefore) < 0.5 ? p.Offset : (regionStart + (long)bb * MB + MB - 1) / MB * MB;
        long nbF = Math.Abs(size.ValueMb - origMb) < 0.5 ? p.Length : size.Bytes;
        Main.Queue("Mover / redimensionar", $"{Who(p)}: {Fmt.Size(p.Length)} → {Fmt.Size(nbF)}{(nsF != p.Offset ? $", início em {Fmt.Size(nsF)} (move os dados)" : "")} · {p.Disk.Title}", "Resize", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await ApplyGeometryLinux(q, nsF, nbF, s);
        }, p.Disk, dangerous: false, canPause: true);
    }

    private static async Task ExtendLinux(PartitionVM pv)
    {
        var p = pv.P;
        var (before, after) = Neighbors(pv);
        long freeAfter = after?.IsUnallocated == true ? after.Length : 0;
        long freeBefore = before?.IsUnallocated == true && !p.IsSystemCritical ? before.Length : 0;
        var f = Form("Estender partição", "Extend", $"{Who(p)} · {Fmt.Size(p.Length)}");
        var add = f.Add(new SizeField { Label = "Aumentar em", MinMb = 1, MaxMb = Math.Floor((freeAfter + freeBefore) / (double)MB), ValueMb = Math.Floor((freeAfter + freeBefore) / (double)MB) });
        add.Changed += v => add.Caption = $"Novo tamanho: {Fmt.Size(p.Length + (long)v * MB)}" + ((long)v * MB > freeAfter ? " · a partição será movida para a esquerda" : "");
        add.Caption = $"Novo tamanho: {Fmt.Size(p.Length + add.Bytes)}";
        if (p.IsMounted && LinuxFs.SpecOf(p)?.GrowMounted == true && freeAfter > 0) f.Note("Cresce com ela montada, sem interromper nada (para a direita).", "good");
        if (freeBefore == 0 && before?.IsUnallocated == true) f.Note("Há espaço livre antes, mas esta partição está em uso e não pode ser movida agora.", "gold");
        if (!await Main.ShowDialog(f)) return;
        long extra = add.Bytes;
        long newStart = p.Offset;
        if (extra > freeAfter) newStart = Math.Max(p.Offset - freeBefore, (p.Offset - (extra - freeAfter)) / MB * MB);
        Main.Queue("Estender partição", $"{Who(p)}: {Fmt.Size(p.Length)} → {Fmt.Size(p.Length + extra)} · {p.Disk.Title}", "Extend", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await ApplyGeometryLinux(q, newStart, q.Length + extra, s);
        }, p.Disk, dangerous: false, canPause: true);
    }

    private static async Task SplitLinux(PartitionVM pv)
    {
        var p = pv.P;
        long min = await Task.Run(() => LinuxFs.MinSize(p)) ?? p.Length;
        var f = Form("Dividir partição", "Split", $"{Who(p)} · {Fmt.Size(p.Length)} · {Fmt.Size(p.Used)} usados");
        var size = f.Add(new SizeField { Label = $"Tamanho que fica em {Who(p)}", MinMb = Math.Ceiling(min / (double)MB), MaxMb = Math.Floor((p.Length - 64 * MB) / (double)MB), ValueMb = Math.Max(Math.Ceiling(min / (double)MB), Math.Round(p.Length / 2.0 / MB)) });
        size.Changed += v => size.Caption = $"Nova partição: {Fmt.Size(p.Length - (long)v * MB)}";
        size.Caption = $"Nova partição: {Fmt.Size(p.Length - size.Bytes)}";
        var choices = FsChoices(allowLuks: false);
        var fs = f.Add(new ChoiceField { Label = "Sistema de arquivos da nova" });
        foreach (var c in choices) fs.Options.Add(c.text);
        var label = f.Add(new TextField { Label = "Rótulo da nova", Value = "" });
        if (min >= p.Length - 64 * MB) f.Note("Não dá para reduzir o bastante: a partição está quase cheia.", "danger");
        f.Validate = () => min >= p.Length - 64 * MB ? "Sem espaço para dividir." : null;
        if (!await Main.ShowDialog(f)) return;
        long keep = size.Bytes;
        string key = choices[fs.SelectedIndex].key, lbl = label.Value.Trim();
        Main.Queue("Dividir partição", $"{Who(p)} fica com {Fmt.Size(keep)}; o resto vira partição nova em {LinuxFs.Formats.First(x => x.Key == key).Name}", "Split", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            await ApplyGeometryLinux(q, q.Offset, keep, s);
            var nd = MainViewModel.Fresh(q.Disk);
            long start = (q.Offset + keep + MB - 1) / MB * MB;
            var free = nd.Partitions.First(x => x.IsUnallocated && x.Offset <= start && start < x.Offset + x.Length);
            long len = (free.Offset + free.Length - start) / MB * MB;
            var t = nd.Table.Clone();
            var kind = LinuxFs.Formats.First(x => x.Key == key).Kind;
            var (g, m) = PartitionTypes.ForFileSystem(kind);
            var e = new PartitionEntry { StartLba = start / nd.SectorSize, SectorCount = len / nd.SectorSize, Name = lbl };
            if (t.Style == TableStyle.Gpt) { e.TypeGuid = g; e.UniqueGuid = Guid.NewGuid(); } else e.MbrType = m;
            t.Entries.Add(e);
            DiskOperations.WriteTable(nd, t, "Dividir partição", s);
            string dev = KernelSync.WaitPartitionNode(nd, start) ?? throw new InvalidOperationException("A nova partição foi criada, mas o kernel não criou o /dev dela. Atualize e formate.");
            await LinuxFs.Mkfs(dev, key, lbl, 0, true, s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task MergeLinux(PartitionVM pv)
    {
        var p = pv.P;
        var (_, next) = Neighbors(pv);
        if (next == null) return;
        var f = Form("Unir partições", "Merge", $"{Who(p)} + {Who(next)}", "Adicionar", true);
        var folder = f.Add(new TextField { Label = $"Os arquivos de {Who(next)} vão para esta pasta dentro de {Who(p)}", Value = $"Conteúdo de {next.Label ?? next.KernelName}" });
        f.Note($"1. Copia tudo de {Who(next)} para a pasta (com dono, permissões e links)\n2. Exclui {Who(next)}\n3. Estende {Who(p)} sobre o espaço liberado", "gold");
        if ((p.Free ?? 0) < (next.Used ?? next.Length)) f.Note($"{Who(p)} tem {Fmt.Size(p.Free)} livres e {Who(next)} usa {Fmt.Size(next.Used)}: não cabe.", "danger");
        f.Validate = () => (p.Free ?? 0) < (next.Used ?? next.Length) ? "Espaço insuficiente." : string.IsNullOrWhiteSpace(folder.Value) ? "Dê um nome à pasta." : null;
        if (!await Main.ShowDialog(f)) return;
        string sub = RecoveryNames.Safe(folder.Value);
        Main.Queue("Unir partições", $"{Who(next)} vai para {Who(p)}/{sub}, é excluída e {Who(p)} cresce", "Merge", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            var nq = MainViewModel.FreshPart(next);
            await LinuxFs.WithMount(q, async dst =>
                await LinuxFs.WithMount(nq, async src =>
                {
                    s.Log($"Copiando {Who(nq)} → {dst}/{sub}");
                    await LinuxFs.CopyTree(src, Path.Combine(dst, sub), s, keepOwnership: LinuxFs.SpecOf(q)?.Kind is FsKind.Ext or FsKind.Xfs or FsKind.Btrfs or FsKind.F2fs);
                }, s, readOnly: true), s);
            s.Log($"Excluindo {Who(nq)}…");
            DiskOperations.DeletePartitionRaw(MainViewModel.FreshPart(nq), s);
            var q2 = MainViewModel.FreshPart(q);
            var free = MainViewModel.Fresh(q2.Disk).Partitions.FirstOrDefault(x => x.IsUnallocated && x.Offset == q2.Offset + q2.Length);
            if (free != null) await ApplyGeometryLinux(q2, q2.Offset, q2.Length + free.Length / MB * MB, s);
        }, p.Disk, dangerous: true, canPause: false);
    }

    // ───────── converter / tamanho do bloco (cópia de passagem) ─────────

    private static async Task ConvertFsLinux(PartitionVM pv, bool sameFs = false)
    {
        var p = pv.P;
        var cur = LinuxFs.SpecOf(p);
        var f = Form(sameFs ? "Alterar tamanho do bloco" : "Converter sistema de arquivos", sameFs ? "Cluster" : "FsConvert", $"{Who(p)} · {FsName(p)} · {Fmt.Size(p.Used)} usados");
        var targets = LinuxFs.Formats.Where(x => x.Key != "swap" && (sameFs ? x.Key == cur?.Key : x.Key != cur?.Key)).ToList();
        if (sameFs && targets.Count == 0) { await Main.Alert("Sem como recriar", $"{FsDetect.Name(p.FileSystem)} não é formatado aqui."); return; }
        var to = f.Add(new ChoiceField { Label = "Converter para", IsVisible = !sameFs });
        foreach (var t in targets) to.Options.Add($"{t.Name} — {t.Note}");
        var bs = f.Add(new ChoiceField { Label = "Tamanho do bloco / cluster" });
        foreach (var x in Blocks) bs.Options.Add(x == 0 ? "Padrão" : $"{x / 1024} KB");
        bs.SelectedIndex = sameFs ? 3 : 0;
        var path = f.Add(new PathField { Label = "Pasta de passagem (em OUTRO disco ou partição)", PickerTitle = "Pasta de passagem" });
        f.Note("Só formatando o sistema de arquivos muda. O LibertyX copia tudo para a pasta de passagem, formata e devolve. Se algo der errado na volta, a cópia fica intacta na pasta.", "gold");
        f.Validate = () =>
        {
            if (string.IsNullOrWhiteSpace(path.Value) || !Directory.Exists(path.Value)) return "Escolha uma pasta de passagem existente.";
            if (p.MountPoints.Any(m => path.Value.StartsWith(m.TrimEnd('/') + "/") || path.Value == m)) return "A pasta de passagem precisa estar em OUTRA partição.";
            try { var di = new DriveInfo(path.Value); if (di.AvailableFreeSpace < (p.Used ?? p.Length)) return $"Lá há {Fmt.Size(di.AvailableFreeSpace)} livres; precisa de {Fmt.Size(p.Used)}."; } catch { }
            var t = targets[to.SelectedIndex];
            if (t.Key == "fat32" && p.Length > 2L << 40) return "FAT32 não passa de 2 TB.";
            return t.Key != "fat32" && !LinuxTools.Has(t.Mkfs) ? LinuxTools.Missing(t.Mkfs) : null;
        };
        if (!await Main.ShowDialog(f)) return;
        var target = targets[to.SelectedIndex];
        int block = Blocks[bs.SelectedIndex];
        string staging = path.Value;
        Main.Queue(sameFs ? "Alterar tamanho do bloco" : $"Converter {FsDetect.Name(p.FileSystem)} → {target.Name}", $"{Who(p)} · {p.Disk.Title} ({p.Disk.Model})", sameFs ? "Cluster" : "FsConvert", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            string label = q.Label ?? "";
            string temp = Path.Combine(staging, $"LibertyX-passagem-{DateTime.Now:yyyyMMdd-HHmmss}");
            bool posix = target.Kind is FsKind.Ext or FsKind.Xfs or FsKind.Btrfs or FsKind.F2fs;
            if (target.Key is "fat32" or "fat16")
            {
                s.Progress(-1, "Procurando arquivos acima de 4 GB");
                await LinuxFs.WithMount(q, mp =>
                {
                    var big = new DirectoryInfo(mp).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(x => x.Length >= 4L << 30).Take(5).ToList();
                    if (big.Count > 0) throw new InvalidOperationException("FAT32 não guarda arquivos de 4 GB ou mais:\n" + string.Join("\n", big.Select(b => b.FullName)) + "\nEscolha exFAT ou mova esses arquivos.");
                    return Task.CompletedTask;
                }, s, readOnly: true);
            }
            s.Log($"1/3 Copiando {Who(q)} para {temp}…");
            await LinuxFs.WithMount(q, mp => LinuxFs.CopyTree(mp, temp, s), s, readOnly: true);
            s.Log($"2/3 Formatando em {target.Name}{(block > 0 ? $" com bloco de {block / 1024} KB" : "")}…");
            q = MainViewModel.FreshPart(q);
            await FormatLinuxCore(q, target.Key, label, block, true, null, s);
            s.Log("3/3 Devolvendo os arquivos…");
            q = MainViewModel.FreshPart(q);
            try { await LinuxFs.WithMount(q, mp => LinuxFs.CopyTree(temp, mp, s, keepOwnership: posix), s); }
            catch (Exception ex) { s.Log($"ATENÇÃO: a volta falhou ({ex.Message}) — seus arquivos continuam inteiros em {temp}."); throw; }
            try { Directory.Delete(temp, true); } catch { s.Log($"A pasta de passagem ficou em {temp} (pode apagar)."); }
        }, p.Disk, dangerous: true, canPause: false);
    }

    // ───────── montar / swap ─────────

    private static async Task MountToggle(PartitionVM pv)
    {
        var p = pv.P;
        if (p.IsMounted)
        {
            await Main.Run($"Desmontando {Who(p)}", s => LinuxFs.Unmount(p, s), success: "Desmontada");
            return;
        }
        var f = Form("Montar partição", "Mount", $"{Who(p)} · {FsName(p)}", "Montar");
        var where = f.Add(new TextField { Label = "Ponto de montagem", Value = LinuxFs.DefaultMountPoint(p) });
        var ro = f.Add(new ToggleField { Label = "Só leitura", Hint = "Recomendado para resgatar arquivos de disco com defeito ou de um Windows que não desligou direito (hibernado)." });
        f.Validate = () => !where.Value.StartsWith('/') ? "Use um caminho absoluto, como /media/dados." : null;
        if (!await Main.ShowDialog(f)) return;
        string? mp = null;
        bool ok = await Main.Run($"Montando {Who(p)}", async s => { mp = await LinuxFs.Mount(p, where.Value.Trim(), ro.Value, s); }, success: "Montada");
        if (ok && mp != null) Main.ShowToast($"{Who(p)} montada em {mp}");
    }

    private static Task SwapToggle(PartitionVM pv)
    {
        var p = pv.P;
        return p.HasPageFile ? Main.Run("Desligando a swap", s => LinuxFs.Unmount(p, s), success: "Swap desligada")
                             : Main.Run("Ligando a swap", s => LinuxFs.SwapOn(p, s), success: "Swap ligada");
    }

    // ───────── rótulo, UUID, nome, sinalizadores ─────────

    private static async Task SetLabelLinux(PartitionVM pv)
    {
        var p = pv.P;
        int max = LinuxFs.SpecOf(p)?.LabelMax ?? (p.FileSystem == FsKind.Luks ? 48 : 32);
        var f = Form("Definir rótulo", "Label", Who(p));
        var t = f.Add(new TextField { Label = "Rótulo", Value = p.Label ?? "", MaxLength = max });
        if (p.IsMounted && p.FileSystem is FsKind.Xfs or FsKind.Ntfs or FsKind.ExFat or FsKind.F2fs or FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32) f.Note($"{FsDetect.Name(p.FileSystem)} troca o rótulo com o volume parado: ele será desmontado.", "gold");
        if (!await Main.ShowDialog(f)) return;
        string v = t.Value.Trim();
        Main.Queue("Definir rótulo", $"{Who(p)} → “{v}”", "Label", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            // XFS só troca rótulo parado; NTFS/FAT/exFAT/F2FS montados teriam o cache do kernel desatualizado
            if (q.IsMounted && q.FileSystem is FsKind.Xfs or FsKind.Ntfs or FsKind.ExFat or FsKind.F2fs or FsKind.Fat12 or FsKind.Fat16 or FsKind.Fat32) await LinuxFs.Unmount(q, s);
            await LinuxFs.SetLabel(q, v, s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task SetUuidLinux(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form("Alterar UUID", "Serial", $"{Who(p)} · atual: {p.FsUuid ?? "—"}");
        var mode = f.Add(new ChoiceField { Label = "Novo UUID" });
        mode.Options.Add("Gerar um aleatório (para separar uma cópia/clone da original)");
        mode.Options.Add("Digitar");
        var custom = f.Add(new TextField { Label = "UUID", Watermark = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", IsVisible = false });
        mode.Changed += i => custom.IsVisible = i == 1;
        f.Note("O /etc/fstab e o GRUB costumam achar as partições pelo UUID. Se esta for usada por um sistema, atualize lá também — senão ele não monta (ou não inicia).", "warn");
        if (p.IsMounted) f.Note("Ela será desmontada (o UUID só muda com o volume parado).", "gold");
        f.Validate = () => mode.SelectedIndex == 1 && !Guid.TryParse(custom.Value, out _) ? "UUID inválido." : null;
        if (!await Main.ShowDialog(f)) return;
        string? u = mode.SelectedIndex == 1 ? Guid.Parse(custom.Value).ToString() : null;
        Main.Queue("Alterar UUID", $"{Who(p)} → {u ?? "aleatório"}", "Serial", async s =>
        {
            var q = MainViewModel.FreshPart(p);
            if (q.IsMounted) await LinuxFs.Unmount(q, s);
            await LinuxFs.SetUuid(q, u, s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task SetGptName(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form("Nome da partição (GPT)", "Label", $"{Who(p)} · atual: {(p.Entry!.Name.Length > 0 ? p.Entry.Name : "—")}");
        var t = f.Add(new TextField { Label = "Nome (PARTLABEL)", Value = p.Entry.Name, MaxLength = 36 });
        f.Note("O nome fica na tabela GPT, não no sistema de arquivos — o /dev/disk/by-partlabel usa ele.", "");
        if (!await Main.ShowDialog(f)) return;
        string v = t.Value.Trim();
        Main.Queue("Nome da partição", $"{Who(p)} → “{v}”", "Label", s =>
        {
            var q = MainViewModel.FreshPart(p);
            var tb = q.Disk.Table.Clone();
            tb.Entries.First(e => e.StartLba == q.Entry!.StartLba).Name = v;
            DiskOperations.WriteTable(q.Disk, tb, "Nome da partição", s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    private static async Task FlagsLinux(PartitionVM pv)
    {
        var p = pv.P;
        var e = p.Entry!;
        bool gpt = p.Disk.Table.Style == TableStyle.Gpt;
        var f = Form("Sinalizadores", "Active", Who(p));
        ToggleField? boot = null, esp = null, bios = null, legacy = null, hidden = null, noauto = null, ro = null, req = null, lvm = null, raid = null, swap = null;
        if (gpt)
        {
            esp = f.Add(new ToggleField { Label = "esp — partição de sistema EFI", Value = e.TypeGuid == PartitionTypes.Esp });
            bios = f.Add(new ToggleField { Label = "bios__grub — BIOS boot (GRUB em GPT com BIOS)", Value = e.TypeGuid == PartitionTypes.BiosBoot });
            lvm = f.Add(new ToggleField { Label = "lvm", Value = e.TypeGuid == PartitionTypes.LinuxLvm });
            raid = f.Add(new ToggleField { Label = "raid", Value = e.TypeGuid == PartitionTypes.LinuxRaid });
            swap = f.Add(new ToggleField { Label = "swap", Value = e.TypeGuid == PartitionTypes.LinuxSwap });
            legacy = f.Add(new ToggleField { Label = "legacy__boot — inicializável pela BIOS (bit 2)", Value = (e.Attributes & PartitionTypes.AttrLegacyBoot) != 0 });
            req = f.Add(new ToggleField { Label = "Requerida pela plataforma (bit 0)", Value = (e.Attributes & PartitionTypes.AttrRequired) != 0 });
            hidden = f.Add(new ToggleField { Label = "Oculta (bit 62)", Value = (e.Attributes & PartitionTypes.AttrHidden) != 0 });
            noauto = f.Add(new ToggleField { Label = "Não montar automaticamente (bit 63)", Value = (e.Attributes & PartitionTypes.AttrNoAutomount) != 0 });
            ro = f.Add(new ToggleField { Label = "Somente leitura (bit 60)", Value = (e.Attributes & PartitionTypes.AttrReadOnly) != 0 });
            f.Note("esp, bios_grub, lvm, raid e swap mudam o ID de tipo da partição (no GPT, é isso que eles são).", "");
        }
        else
        {
            boot = f.Add(new ToggleField { Label = "boot — partição ativa (a BIOS inicia por ela)", Value = e.Active });
            esp = f.Add(new ToggleField { Label = "esp (tipo 0xEF)", Value = e.MbrType == 0xEF });
            lvm = f.Add(new ToggleField { Label = "lvm (tipo 0x8E)", Value = e.MbrType == 0x8E });
            raid = f.Add(new ToggleField { Label = "raid (tipo 0xFD)", Value = e.MbrType == 0xFD });
            swap = f.Add(new ToggleField { Label = "swap (tipo 0x82)", Value = e.MbrType == 0x82 });
            hidden = f.Add(new ToggleField { Label = "Oculta (tipo com 0x10)", Value = e.MbrType is 0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E });
        }
        if (!await Main.ShowDialog(f)) return;
        Main.Queue("Sinalizadores", $"{Who(p)} · {p.Disk.Title}", "Active", s =>
        {
            var q = MainViewModel.FreshPart(p);
            var t = q.Disk.Table.Clone();
            var x = t.Entries.First(y => y.StartLba == q.Entry!.StartLba);
            if (gpt)
            {
                var natural = PartitionTypes.ForFileSystem(q.FileSystem).gpt;
                Guid type = esp!.Value ? PartitionTypes.Esp : bios!.Value ? PartitionTypes.BiosBoot : lvm!.Value ? PartitionTypes.LinuxLvm : raid!.Value ? PartitionTypes.LinuxRaid : swap!.Value ? PartitionTypes.LinuxSwap
                    : x.TypeGuid == PartitionTypes.Esp || x.TypeGuid == PartitionTypes.BiosBoot || x.TypeGuid == PartitionTypes.LinuxLvm || x.TypeGuid == PartitionTypes.LinuxRaid || x.TypeGuid == PartitionTypes.LinuxSwap ? natural : x.TypeGuid;
                x.TypeGuid = type;
                ulong a = x.Attributes;
                a = legacy!.Value ? a | PartitionTypes.AttrLegacyBoot : a & ~PartitionTypes.AttrLegacyBoot;
                a = req!.Value ? a | PartitionTypes.AttrRequired : a & ~PartitionTypes.AttrRequired;
                a = hidden!.Value ? a | PartitionTypes.AttrHidden : a & ~PartitionTypes.AttrHidden;
                a = noauto!.Value ? a | PartitionTypes.AttrNoAutomount : a & ~PartitionTypes.AttrNoAutomount;
                a = ro!.Value ? a | PartitionTypes.AttrReadOnly : a & ~PartitionTypes.AttrReadOnly;
                x.Attributes = a;
            }
            else
            {
                if (boot!.Value) foreach (var y in t.Entries) y.Active = y == x;
                else x.Active = false;
                byte natural = PartitionTypes.ForFileSystem(q.FileSystem).mbr;
                byte type = esp!.Value ? (byte)0xEF : lvm!.Value ? (byte)0x8E : raid!.Value ? (byte)0xFD : swap!.Value ? (byte)0x82 : x.MbrType is 0xEF or 0x8E or 0xFD or 0x82 ? natural : (byte)(x.MbrType & ~0x10);
                if (hidden!.Value && type is 0x01 or 0x04 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0E) type |= 0x10;
                x.MbrType = type;
            }
            DiskOperations.WriteTable(q.Disk, t, "Sinalizadores", s);
        }, p.Disk, dangerous: false, canPause: false);
    }

    // ───────── otimizar ─────────

    /// <summary>SSD: TRIM do espaço livre (fstrim). HD: desfragmentação online (e4defrag, xfs_fsr, btrfs defragment).</summary>
    private static async Task OptimizeLinux(PartitionVM pv)
    {
        var p = pv.P;
        string mp = p.MountPoints[0];
        bool ssd = p.Disk.IsSsd != false;
        string tool = ssd ? "fstrim" : p.FileSystem switch { FsKind.Ext => "e4defrag", FsKind.Xfs => "xfs_fsr", _ => "btrfs" };
        if (LinuxTools.Missing(tool) is { } miss) { await Main.Alert("Falta ferramenta", miss, "warn"); return; }
        await Main.Run(ssd ? $"TRIM em {Who(p)}" : $"Desfragmentando {Who(p)}", async s =>
        {
            s.Progress(-1, ssd ? "Avisando o SSD dos blocos livres" : "Reorganizando os arquivos");
            var r = ssd ? await Proc.Run("fstrim", ["-v", mp], s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); })
                  : tool == "btrfs" ? await Proc.Run("btrfs", ["filesystem", "defragment", "-r", mp], s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); })
                  : await Proc.Run(tool, tool == "xfs_fsr" ? ["-v", mp] : [mp], s.Token, l => { if (l.Trim().Length > 0 && !l.Contains("[")) s.Log(l.Trim()); });
            if (!r.Ok) throw new InvalidOperationException(r.Message.Split('\n').LastOrDefault()?.Trim() ?? "falhou");
            s.Log(ssd ? "Pronto: o SSD sabe quais blocos estão livres e pode reaproveitá-los (mais velocidade e vida útil)." : "Pronto.");
        }, refresh: false, success: "Otimizado");
    }

    // ───────── verificar ─────────

    private static async Task CheckLinux(PartitionVM pv)
    {
        var p = pv.P;
        var f = Form("Verificar sistema de arquivos", "CheckFs", $"{Who(p)} · {FsName(p)}", "Verificar");
        var mode = f.Add(new ChoiceField { Label = "O que fazer" });
        mode.Options.Add("Só verificar (não altera nada)");
        mode.Options.Add("Verificar e corrigir erros");
        if (p.FileSystem is FsKind.Ext || FsDetect.IsFat(p.FileSystem)) mode.Options.Add("Corrigir erros e marcar setores defeituosos (lento)");
        string tool = p.FileSystem switch
        {
            FsKind.Ext => "e2fsck", FsKind.Xfs => "xfs_repair", FsKind.Btrfs => "btrfs", FsKind.Ntfs => "ntfsfix", FsKind.ExFat => "fsck.exfat",
            FsKind.F2fs => "fsck.f2fs", _ => "fsck.fat",
        };
        if (!LinuxTools.Has(tool)) f.Note(LinuxTools.Missing(tool)!, "danger");
        if (p.FileSystem == FsKind.Ntfs) f.Note("No Linux, o ntfsfix corrige só o básico e marca o volume para o chkdsk do Windows terminar o serviço. Para o reparo completo, use o LibertyX Partition no Windows.", "gold");
        if (p.FileSystem == FsKind.Btrfs) f.Note(p.IsMounted ? "Montado: o scrub confere a soma de verificação de todos os dados." : "O reparo do Btrfs (check --repair) é o último recurso: faça uma imagem antes.", "warn");
        if (p.IsMounted && p.FileSystem != FsKind.Btrfs) f.Note($"Montada em {p.MountPoints[0]}: para corrigir, ela será desmontada{(p.IsSystemCritical ? " — mas está em uso pelo sistema; só a verificação sem correção roda agora" : "")}.", "gold");
        f.Note("Em disco que está morrendo, corrigir pode piorar: faça antes uma imagem (Backup e imagem) e recupere dela.", "warn");
        f.Validate = () => !LinuxTools.Has(tool) ? LinuxTools.Missing(tool) : mode.SelectedIndex > 0 && p.IsSystemCritical && p.FileSystem != FsKind.Btrfs ? "Em uso pelo sistema: só dá para verificar sem corrigir." : null;
        if (!await Main.ShowDialog(f)) return;
        var m = (LinuxFs.CheckMode)mode.SelectedIndex;
        Func<OpSink, Task> work = async s =>
        {
            var q = MainViewModel.FreshPart(p);
            if (m != LinuxFs.CheckMode.ReadOnly && q.IsMounted && q.FileSystem != FsKind.Btrfs) await LinuxFs.Unmount(q, s);
            q = MainViewModel.FreshPart(q);
            s.Log(await LinuxFs.Check(q, m, s));
        };
        if (m == LinuxFs.CheckMode.ReadOnly) await Main.Run("Verificando " + Who(p), work, refresh: false);
        else Main.Queue(m == LinuxFs.CheckMode.RepairDeep ? "Corrigir erros e setores ruins" : "Corrigir erros do sistema de arquivos", $"{tool} em {Who(p)}", "CheckFs", work, p.Disk);
    }
}
