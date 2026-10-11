using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LibertyX.Partition.Controls;
using LibertyX.Partition.ViewModels;

namespace LibertyX.Partition.Views;

public partial class DisksPageView : UserControl
{
    public DisksPageView()
    {
        InitializeComponent();
        // clique direito em qualquer partição (linha da tabela ou bloco do mapa) abre as mesmas ações do painel
        AddHandler(ContextRequestedEvent, OnContext, RoutingStrategies.Tunnel);
    }

    private void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control c) return;
        object? ctx = null;
        for (Control? x = c; x != null && ctx == null; x = x.Parent as Control)
        {
            if (x is PartitionRibbon rb && e.TryGetPosition(rb, out var pos)) ctx = rb.HitTest(pos);
            else if (x.DataContext is PartitionVM or DiskVM && x is Button) ctx = x.DataContext;
        }
        if (ctx == null) return;
        var page = MainViewModel.Instance.Page<DisksPageViewModel>("discos");
        IEnumerable<ActionGroup> groups;
        if (ctx is PartitionVM p) { page.Select(p); groups = page.Actions; }
        else if (ctx is DiskVM d) { page.Select(d); groups = page.Actions; }
        else return;
        var menu = new ContextMenu();
        bool first = true;
        foreach (var g in groups)
        {
            if (!first) menu.Items.Add(new Separator());
            first = false;
            menu.Items.Add(new MenuItem { Header = g.Title, IsEnabled = false, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.Bold });
            foreach (var a in g.Items)
                menu.Items.Add(new MenuItem
                {
                    Header = a.Title, Command = a.Command, IsEnabled = a.Enabled,
                    Icon = new PathIcon { Data = a.Icon, Width = 15, Height = 15 },
                });
        }
        menu.Open(c);
        e.Handled = true;
    }
}
