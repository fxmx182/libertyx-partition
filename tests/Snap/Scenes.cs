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
            default:
                vm.Navigate(scene);
                pump(500);
                snap("p-" + scene);
                break;
        }
        await Task.CompletedTask;
    }
}
