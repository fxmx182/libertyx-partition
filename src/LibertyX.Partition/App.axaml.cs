using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LibertyX.Partition.ViewModels;
using LibertyX.Partition.Views;

namespace LibertyX.Partition;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            vm.Start(desktop.Args ?? []);
            if (OperatingSystem.IsLinux())
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var st = Avalonia.Platform.AssetLoader.Open(new Uri("avares://LibertyXPartition/Assets/icone-256.png"));
                        using var ms = new MemoryStream();
                        st.CopyTo(ms);
                        LibertyX.Partition.Core.Platform.Linux.LinuxSession.IntegrateAppImage(ms.ToArray());
                    }
                    catch { }
                });
            // reaberto como root pelo pkexec: avisa a instância sem privilégio que já pode fechar
            if (Environment.GetEnvironmentVariable("LIBERTYX_READY") is { Length: > 0 } ready)
                desktop.MainWindow.Opened += async (_, _) =>
                {
                    try { File.WriteAllText(ready, "ok"); await Task.Delay(5000); File.Delete(ready); } catch { }
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
