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
    public static bool IsWin => OperatingSystem.IsWindows();
    /// <summary>"Disco do Windows em uso." / "Disco do sistema em uso." — os filtros e avisos que bloqueiam o disco do sistema.</summary>
    public static string SystemDiskInUse => IsWin ? "Disco do Windows em uso." : "Disco do sistema em uso (onde está o /).";
    public static string SystemBadge => IsWin ? "Windows" : "Sistema";
    /// <summary>A opção de copiar um volume em uso sem pegá-lo no meio de uma gravação: VSS no Windows; LVM ou só leitura no Linux.</summary>
    public static string SnapshotImage => IsWin ? "Instantâneo VSS dos volumes em uso" : "Cópia consistente dos volumes em uso (instantâneo LVM ou só leitura)";
    public static string SnapshotPartition => IsWin ? "Instantâneo VSS se estiver em uso" : "Cópia consistente se estiver em uso (LVM ou só leitura)";
    public static string SnapshotClone => IsWin ? "Instantâneo VSS (Windows em uso)" : "Cópia consistente (instantâneo LVM ou só leitura)";

    public static Geometry? Icon(string key) =>
        Application.Current != null && Application.Current.TryGetResource("Ic" + key, null, out var v) ? v as Geometry : null;

    public static void Post(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    /// <summary>O ouro da casa.</summary>
    public static readonly Color Accent = Color.Parse("#FBC501");

    /// <summary>Os sistemas de arquivos são tons de um mesmo metal — ouro, marfim, âmbar, cobre,
    /// bronze, pérola — em vez de um arco-íris: o disco se lê pela família LibertyX e as partições
    /// ainda se distinguem pela temperatura do tom.</summary>
    public static Color FsColor(FsKind fs) => fs switch
    {
        FsKind.Ntfs => Color.Parse("#FBC501"),
        FsKind.Fat32 or FsKind.Fat16 or FsKind.Fat12 => Color.Parse("#EFE0B9"),
        FsKind.ExFat => Color.Parse("#E3A23A"),
        FsKind.Ext => Color.Parse("#D2783F"),
        FsKind.BitLocker => Color.Parse("#B08A62"),
        FsKind.ReFS => Color.Parse("#F7D774"),
        FsKind.HfsPlus or FsKind.Apfs => Color.Parse("#D3CABA"),
        FsKind.LinuxSwap => Color.Parse("#94704A"),
        _ => Color.Parse("#756C60"),
    };

    public static Color PartitionColor(PartitionInfo p) =>
        p.IsUnallocated ? Color.Parse("#3A3630") : p.IsEsp ? Color.Parse("#C9B48A") : p.IsMsr ? Color.Parse("#4E4840") : p.IsRecovery ? Color.Parse("#9E7A52") : FsColor(p.FileSystem);

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
    /// <summary>Janela de operação em tela baixa: o anel e o registro encolhem para os botões caberem.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter RingSize =
        new Avalonia.Data.Converters.FuncValueConverter<double, double>(h => h < 760 ? 120 : 176);
    public static readonly Avalonia.Data.Converters.IValueConverter LogHeight =
        new Avalonia.Data.Converters.FuncValueConverter<double, double>(h => Math.Clamp(h - 600, 70, 150));
}
