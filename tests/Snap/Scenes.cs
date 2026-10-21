using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.ViewModels;
using LibertyX.Partition.Views;

public static class Scenes
{
    public static async Task Run(string scene, MainViewModel vm, MainWindow w, Action<int> pump, Action<Func<bool>, int> wait, Action<string> snap)
    {
        var disks = vm.Page<DisksPageViewModel>("discos");
        PartitionVM Part(FsKind fs) => disks.Disks.SelectMany(d => d.Partitions).First(p => p.P.FileSystem == fs);
        switch (scene)
        {
            case "superficie":
                vm.Navigate("superficie");
                var sv = vm.Page<SurfaceViewModel>("superficie");
                pump(500);
                sv.Picker.Select(vm.Disks[0], null);
                pump(300);
                _ = sv.StartCommand.ExecuteAsync(null);
                pump(150);
                snap("23-superficie-rodando");
                wait(() => sv.Running, 60000);
                snap("24-superficie-fim");
                break;
            case "luks":
                vm.Navigate("luks");
                var lv = vm.Page<LuksViewModel>("luks");
                pump(500);
                lv.Picker.Select(vm.Disks.First(d => d.ImagePath?.EndsWith("luks.img") == true), null);
                pump(300);
                _ = lv.StartCommand.ExecuteAsync(null);
                wait(() => lv.Running, 60000);
                pump(400);
                snap("27-luks");
                break;
            case "todas":
                int k = 30;
                foreach (var key in new[] { "discos", "premium", "recuperar", "particoes", "imagem", "superficie", "saude", "migrar", "copiar-disco", "copiar-particao", "converter", "alinhar", "limpar", "excluir-todas", "bitlocker", "benchmark", "espaco", "duplicados", "boot" })
                {
                    vm.Navigate(key, vm.Disks.FirstOrDefault());
                    pump(600);
                    snap($"{k++}-{key}");
                }
                break;
            case "gravar":
                vm.Navigate("boot");
                var bm = vm.Page<BootMediaViewModel>("boot");
                string fl = Path.GetFullPath("tests/images/flash");
                var tw = bm.SetImage(Path.Combine(fl, "windows.iso"));
                wait(() => !tw.IsCompleted, 30000);
                snap("25-gravar-windows");
                var ti = bm.SetImage(Path.Combine(fl, "disco.img.xz"));
                wait(() => !ti.IsCompleted, 30000);
                snap("26-gravar-imagem");
                break;
            case "premium":
                vm.Navigate("premium");
                snap("20-premium");
                break;
            case "premium-trava":
                vm.Navigate("migrar");
                _ = vm.RequirePremium("Migrar sistema", "Levar o Windows inteiro para outro SSD ou HD, pronto para dar boot, é do Premium.");
                snap("21-premium-trava");
                vm.Dialog = null;
                break;
            case "premium-ativo":
                var chave = Environment.GetEnvironmentVariable("LXP_CHAVE_REAL");
                if (chave == null) break;
                var err = await vm.License.Activate(chave);
                vm.LicenseChanged();
                vm.Navigate("premium");
                snap("22-premium-ativo" + (err == null ? "" : "-erro"));
                await vm.License.Deactivate();
                vm.LicenseChanged();
                break;
            case "discos":
                vm.Navigate("discos");
                snap("01-discos");
                break;
            case "discos-ntfs":
                vm.Navigate("discos");
                disks.Select(Part(FsKind.Ntfs));
                snap("02-discos-ntfs");
                break;
            case "dialogo":
                vm.Navigate("discos");
                var f = new FormDialogViewModel { Title = "Formatar partição", IconKey = "Format", Subtitle = "DADOS · 120 MB", ConfirmText = "Formatar", Danger = true };
                var c = f.Add(new ChoiceField { Label = "Sistema de arquivos" }); c.Options.Add("NTFS"); c.Options.Add("exFAT");
                f.Add(new TextField { Label = "Rótulo", Value = "DADOS" });
                f.Add(new SizeField { Label = "Tamanho", MinMb = 16, MaxMb = 1000, ValueMb = 500, Caption = "Livre depois: 500 MB" });
                f.Add(new ToggleField { Label = "Formatação rápida", Value = true, Hint = "Sem ela, o Windows lê a partição inteira." });
                f.Note("Tudo em DADOS será apagado.", "danger");
                _ = vm.ShowDialog(f);
                snap("03-dialogo");
                f.Close(false);
                pump(100);
                break;
            case "operacao":
                var op = new OperationViewModel { Title = "Clonando disco", CanPause = true };
                vm.Operation = op;
                op.Sink.Log("Criando instantâneo VSS de C:…");
                op.Sink.Log("Copiando C: (NTFS, 476 GB) para o setor 2.048…");
                op.Sink.Progress(0.42, "Clonando", "201 GB de 476 GB · 1,2 GB/s");
                pump(400);
                snap("04-operacao");
                vm.Operation = null;
                break;
            case "recuperar":
                {
                    var r = vm.Page<RecoveryViewModel>("recuperar");
                    vm.Navigate("recuperar", Part(FsKind.Ntfs).P);
                    pump(300);
                    snap("05-recuperar-escolha");
                    r.DeepScan = true;
                    var t = r.StartCommand.ExecuteAsync(null);
                    pump(150);
                    snap("06-recuperar-varrendo");
                    while (!t.IsCompleted) pump(100);
                    pump(500);
                    snap("07-recuperar-resultado");
                    var docs = r.Tree[0].Children.FirstOrDefault(c => c.Name == "Documentos");
                    if (docs != null) { docs.IsExpanded = true; r.SelectedFolder = docs; }
                    pump(300);
                    var pdf = r.Files.FirstOrDefault();
                    if (pdf != null) { r.SelectedFile = pdf; pdf.IsChecked = true; }
                    pump(800);
                    snap("08-recuperar-previa");
                    var fot = r.Tree[0].Children.FirstOrDefault(c => c.Name == "Fotos");
                    if (fot != null) r.SelectedFolder = fot;
                    pump(300);
                    if (r.Files.Count > 0) r.SelectedFile = r.Files[0];
                    pump(1200);
                    snap("09-recuperar-foto");
                    break;
                }
            case "fila":
                vm.Navigate("discos");
                disks.Select(Part(FsKind.Ntfs));
                vm.Queue("Mover / redimensionar", "DADOS: 120 MB → 180 MB · Imagem · disk.img", "Resize", _ => Task.CompletedTask, disks.Disks[0].D);
                vm.Queue("Formatar partição", "PENDRIVE → exFAT, rótulo “FOTOS” — apaga o conteúdo", "Format", _ => Task.CompletedTask, disks.Disks[0].D, dangerous: true);
                vm.Queue("Converter para MBR", "Imagem · disk.img: GPT → MBR, sem apagar partições", "Convert", _ => Task.CompletedTask, disks.Disks[0].D);
                pump(300);
                snap("10-fila");
                _ = vm.ApplyPendingCommand.ExecuteAsync(null);
                pump(400);
                snap("11-fila-confirmar");
                vm.Dialog?.Close(false);
                vm.Pending.Clear();
                pump(100);
                break;
            case "linux":
                {
                    // disco loop preparado pelo script do teste (LX_SNAP_REAL=1, LIBERTYX_LOOPS=1)
                    vm.Navigate("discos");
                    var lp = disks.Disks.FirstOrDefault(x => x.D.KernelName.StartsWith("loop") && x.D.Model.Contains("snap.img")) ?? disks.Disks.FirstOrDefault(x => x.D.KernelName.StartsWith("loop"));
                    if (lp == null) { Console.WriteLine("sem disco loop"); break; }
                    disks.Select(lp);
                    pump(400);
                    snap("40-linux-disco");
                    foreach (var (fs, name) in new[] { (FsKind.Ext, "41-linux-ext4"), (FsKind.Ntfs, "42-linux-ntfs"), (FsKind.Luks, "43-linux-luks"), (FsKind.LinuxSwap, "44-linux-swap") })
                    {
                        var pv = lp.Partitions.FirstOrDefault(x => x.P.FileSystem == fs);
                        if (pv == null) continue;
                        disks.Select(pv);
                        pump(300);
                        snap(name);
                    }
                    var ext = lp.Partitions.FirstOrDefault(x => x.P.FileSystem == FsKind.Ext);
                    if (ext != null)
                    {
                        disks.Select(ext);
                        pump(200);
                        foreach (var (title, shot) in new[] { ("Formatar", "45-linux-formatar"), ("Mover / redimensionar", "46-linux-redimensionar"), ("Verificar sistema de arquivos", "47-linux-verificar"), ("Sinalizadores (boot, esp, oculta…)", "48-linux-sinalizadores") })
                        {
                            var a = disks.Actions.SelectMany(g => g.Items).FirstOrDefault(i => i.Title == title);
                            if (a == null || !a.Enabled) { Console.WriteLine($"ação {title} indisponível: {a?.Reason}"); continue; }
                            _ = a.Command.ExecuteAsync(null);
                            for (int i = 0; i < 40 && vm.Dialog == null; i++) pump(100);
                            pump(500);
                            snap(shot);
                            vm.Dialog?.Close(false);
                            pump(200);
                        }
                    }
                    var free = lp.Partitions.FirstOrDefault(x => x.IsUnallocated);
                    if (free != null)
                    {
                        disks.Select(free);
                        pump(200);
                        var a = disks.Actions.SelectMany(g => g.Items).FirstOrDefault(i => i.Title == "Criar partição");
                        if (a != null) { _ = a.Command.ExecuteAsync(null); for (int i = 0; i < 40 && vm.Dialog == null; i++) pump(100); pump(400); snap("49-linux-criar"); vm.Dialog?.Close(false); pump(200); }
                    }
                    foreach (var (key, shot) in new[] { ("bitlocker", "50-linux-criptografia"), ("lvm", "51-linux-lvm"), ("ferramentas", "52-linux-ferramentas"), ("saude", "53-linux-saude"), ("benchmark", "54-linux-benchmark"), ("boot", "55-linux-boot") })
                    {
                        vm.Navigate(key, lp.D);
                        pump(1200);
                        snap(shot);
                    }
                    break;
                }
            default:
                vm.Navigate(scene);
                pump(500);
                snap("p-" + scene);
                break;
        }
        await Task.CompletedTask;
    }
}
