using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using LibertyX.Partition;
using LibertyX.Partition.Core.Platform;
using LibertyX.Partition.ViewModels;
using LibertyX.Partition.Views;

// Captura telas do app sem monitor: dotnet run -- <pasta-saida> <imagens...> -- cena1 cena2 ...
string outDir = args[0];
var sep = Array.IndexOf(args, "--");
var images = args[1..sep];
var scenes = args[(sep + 1)..];
Environment.SetEnvironmentVariable("LIBERTYX_ONLY_IMAGES", "1");
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

foreach (var i in images) DiskSystem.AttachedImages.Add(Path.GetFullPath(i));
var vm = new MainViewModel();
var size = (Environment.GetEnvironmentVariable("LX_SNAP_SIZE") ?? "1440x900").Split('x').Select(double.Parse).ToArray();
var w = new MainWindow { DataContext = vm, Width = size[0], Height = size[1], MinWidth = 0, MinHeight = 0 };
w.Show();
vm.Start([]);

void Pump(int ms = 300)
{
    var end = DateTime.Now.AddMilliseconds(ms);
    while (DateTime.Now < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(15); }
}
void WaitIdle(Func<bool> busy, int maxMs = 60000)
{
    var end = DateTime.Now.AddMilliseconds(maxMs);
    while (busy() && DateTime.Now < end) Pump(50);
    Pump(400);
}
void Snap(string name)
{
    Pump(200);
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Pump(50);
    var frame = w.CaptureRenderedFrame();
    frame?.Save(Path.Combine(outDir, name + ".png"));
    Console.WriteLine("salvo " + name);
}
WaitIdle(() => vm.Loading);
Pump(1500);

foreach (var scene in scenes)
{
    try { await Scenes.Run(scene, vm, w, Pump, WaitIdle, Snap); }
    catch (Exception ex) { Console.WriteLine($"cena {scene} falhou: {ex}"); }
}
