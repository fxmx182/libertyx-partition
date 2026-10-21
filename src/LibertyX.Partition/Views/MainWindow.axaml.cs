using Avalonia;
using Avalonia.Controls;

namespace LibertyX.Partition.Views;

public partial class MainWindow : Window
{
    /// <summary>Abaixo desta largura a barra lateral vira só ícones.</summary>
    public const double CompactBelow = 1180;

    public MainWindow() => InitializeComponent();

    /// <summary>
    /// Com a interface ampliada (4K em 200–300%) a janela de 1440×900 pode não caber na tela:
    /// encolhe para a área útil e centraliza.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // só no Linux (a escala vem do app); no Windows a janela abre como sempre abriu
        if (!OperatingSystem.IsLinux() || Screens.ScreenFromWindow(this) is not { } sc) return;
        var area = sc.WorkingArea.Size.ToSize(sc.Scaling);
        if (Width <= area.Width * 0.96 && Height <= area.Height * 0.94) return;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width * 0.96));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height * 0.94));
        if (Width > area.Width || Height > area.Height) WindowState = WindowState.Maximized;
        else Position = new PixelPoint(sc.WorkingArea.X + (int)((sc.WorkingArea.Width - Width * sc.Scaling) / 2),
                                       sc.WorkingArea.Y + (int)((sc.WorkingArea.Height - Height * sc.Scaling) / 2));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        bool compact = e.NewSize.Width < CompactBelow;
        if (Shell.Classes.Contains("compact") == compact && Shell.ColumnDefinitions[0].Width.Value > 0) return;
        Shell.Classes.Set("compact", compact);
        Shell.ColumnDefinitions[0].Width = new GridLength(compact ? 72 : 256);
        PageHost.Margin = compact ? new Thickness(20, 0, 20, 18) : new Thickness(30, 0, 30, 26);
    }
}
