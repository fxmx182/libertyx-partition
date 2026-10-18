using Avalonia;
using Avalonia.Controls;

namespace LibertyX.Partition.Views;

public partial class MainWindow : Window
{
    /// <summary>Abaixo desta largura a barra lateral vira só ícones.</summary>
    public const double CompactBelow = 1180;

    public MainWindow() => InitializeComponent();

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
