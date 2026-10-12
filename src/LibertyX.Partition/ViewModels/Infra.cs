using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.Platform;

namespace LibertyX.Partition.ViewModels;

public abstract class ViewModelBase : ObservableObject { }

/// <summary>Uma tela da navegação lateral.</summary>
public abstract partial class PageViewModel : ViewModelBase
{
    public abstract string Title { get; }
    public virtual string Subtitle => "";
    public abstract string IconKey { get; }
    public Geometry? Icon => Ui.Icon(IconKey);
    protected MainViewModel Main => MainViewModel.Instance;
    /// <summary>Chamado ao entrar na tela (com argumento opcional: disco/partição de onde veio).</summary>
    public virtual void OnShown(object? arg) { }
    /// <summary>A lista de discos foi relida.</summary>
    public virtual void OnDisksChanged() { }
    /// <summary>A fila de alterações pendentes mudou.</summary>
    public virtual void OnPendingChanged() { }
}

public static class Ui
{
    public static Geometry? Icon(string key) =>
        Application.Current != null && Application.Current.TryGetResource("Ic" + key, null, out var v) ? v as Geometry : null;

    public static void Post(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    /// <summary>O acento da casa (ouro suave) e a família de tons dessaturados das partições:
    /// todos com o mesmo brilho, para nenhum gritar mais que o outro.</summary>
    public static readonly Color Accent = Color.Parse("#E7C064");

    public static Color FsColor(FsKind fs) => fs switch
    {
        FsKind.Ntfs => Color.Parse("#D2B67A"),
        FsKind.Fat32 or FsKind.Fat16 or FsKind.Fat12 => Color.Parse("#8DB8A8"),
        FsKind.ExFat => Color.Parse("#A3A1CC"),
        FsKind.Ext => Color.Parse("#C99D88"),
        FsKind.BitLocker => Color.Parse("#C4929F"),
        FsKind.ReFS => Color.Parse("#92A9C6"),
        FsKind.HfsPlus or FsKind.Apfs => Color.Parse("#B4B4BC"),
        FsKind.LinuxSwap => Color.Parse("#AC9AC4"),
        _ => Color.Parse("#7A7980"),
    };

    public static Color PartitionColor(PartitionInfo p) =>
        p.IsUnallocated ? Color.Parse("#3A3940") : p.IsEsp ? Color.Parse("#BDB39A") : p.IsMsr ? Color.Parse("#5C5B63") : p.IsRecovery ? Color.Parse("#A99683") : FsColor(p.FileSystem);

    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    public static string HealthClass(HealthLevel h) => h switch { HealthLevel.Good => "green", HealthLevel.Caution => "orange", HealthLevel.Bad => "red", _ => "" };
    public static string HealthText(HealthLevel h) => h switch { HealthLevel.Good => "Saudável", HealthLevel.Caution => "Atenção", HealthLevel.Bad => "Crítico", _ => "Saúde ?" };
}

public static class Fmt
{
    public static string Size(long? b)
    {
        if (b == null) return "—";
        string[] u = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = b.Value; int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : v >= 100 ? $"{v:0} {u[i]}" : v >= 10 ? $"{v:0.#} {u[i]}" : $"{v:0.##} {u[i]}";
    }

    public static string Speed(double bps) => Size((long)bps) + "/s";
    public static string Date(DateTime? d) => d == null ? "—" : d.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public static string Num(long n) => n.ToString("N0");
    public static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}min {t.Seconds:00}s" : $"{t.Seconds}s";
}

public static class Conv
{
    public static readonly Avalonia.Data.Converters.IValueConverter Size =
        new Avalonia.Data.Converters.FuncValueConverter<long, string>(v => Fmt.Size(v));
}
