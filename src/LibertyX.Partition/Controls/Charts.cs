using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LibertyX.Partition.Controls;

/// <summary>Mapa de setores do teste de superfície: um quadradinho por bloco, colorido pela saúde.</summary>
public sealed class SectorMap : Control
{
    public static readonly StyledProperty<byte[]?> CellsProperty = AvaloniaProperty.Register<SectorMap, byte[]?>(nameof(Cells));
    public static readonly StyledProperty<int> VersionProperty = AvaloniaProperty.Register<SectorMap, int>(nameof(Version));
    public byte[]? Cells { get => GetValue(CellsProperty); set => SetValue(CellsProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    static SectorMap() => AffectsRender<SectorMap>(CellsProperty, VersionProperty);

    private static readonly IBrush[] Brushes =
    [
        new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
        new SolidColorBrush(Color.FromArgb(200, 0x35, 0xD0, 0xBE)),
        new SolidColorBrush(Color.Parse("#FBC501")),
        new SolidColorBrush(Color.Parse("#FFA24D")),
        new SolidColorBrush(Color.Parse("#FF5A5A")),
    ];

    public override void Render(DrawingContext ctx)
    {
        var cells = Cells;
        if (cells == null || cells.Length == 0 || Bounds.Width < 10) return;
        double gap = 3;
        // escolhe o lado do quadrado para caber tudo na área
        double area = Bounds.Width * Bounds.Height;
        double side = Math.Floor(Math.Sqrt(area / cells.Length)) - gap;
        side = Math.Clamp(side, 3, 22);
        int cols = Math.Max(1, (int)((Bounds.Width + gap) / (side + gap)));
        for (int i = 0; i < cells.Length; i++)
        {
            int r = i / cols, c = i % cols;
            var rect = new Rect(c * (side + gap), r * (side + gap), side, side);
            ctx.DrawRectangle(Brushes[Math.Min(cells[i], (byte)4)], null, rect, side / 4, side / 4);
        }
    }
}

/// <summary>Curva simples (velocidade ao longo do disco), com área preenchida em ouro.</summary>
public sealed class LineChart : Control
{
    public static readonly StyledProperty<IList<double>?> ValuesProperty = AvaloniaProperty.Register<LineChart, IList<double>?>(nameof(Values));
    public static readonly StyledProperty<int> VersionProperty = AvaloniaProperty.Register<LineChart, int>(nameof(Version));
    public static readonly StyledProperty<string> UnitProperty = AvaloniaProperty.Register<LineChart, string>(nameof(Unit), "MB/s");
    public IList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    static LineChart() => AffectsRender<LineChart>(ValuesProperty, VersionProperty);

    public override void Render(DrawingContext ctx)
    {
        var v = Values;
        double w = Bounds.Width, h = Bounds.Height;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1);
        for (int i = 1; i <= 3; i++) ctx.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        if (v == null || v.Count < 2) return;
        double max = Math.Max(1, v.Max()) * 1.12;
        var pts = v.Select((y, i) => new Point(w * i / (v.Count - 1), h - h * y / max)).ToList();
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(0, h), true);
            foreach (var p in pts) g.LineTo(p);
            g.LineTo(new Point(w, h));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(90, 0xFB, 0xC5, 0x01), 0), new GradientStop(Color.FromArgb(0, 0xFB, 0xC5, 0x01), 1) },
        }, null, area);
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(pts[0], false);
            foreach (var p in pts.Skip(1)) g.LineTo(p);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#FBC501")), 2, lineJoin: PenLineJoin.Round), line);
        var tf = new Typeface(new FontFamily("avares://LibertyXPartition/Assets/Fonts#Manrope"));
        var label = new FormattedText($"{max / 1.12:0} {Unit}", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, 11, new SolidColorBrush(Color.FromArgb(150, 235, 235, 245)));
        ctx.DrawText(label, new Point(4, 2));
    }
}

/// <summary>Treemap do analisador de espaço: cada retângulo é proporcional ao tamanho.</summary>
public sealed class TreemapView : Control
{
    public static readonly StyledProperty<IList<LibertyX.Partition.Core.Tools.SpaceNode>?> ItemsProperty = AvaloniaProperty.Register<TreemapView, IList<LibertyX.Partition.Core.Tools.SpaceNode>?>(nameof(Items));
    public static readonly StyledProperty<LibertyX.Partition.Core.Tools.SpaceNode?> SelectedProperty = AvaloniaProperty.Register<TreemapView, LibertyX.Partition.Core.Tools.SpaceNode?>(nameof(Selected), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<int> VersionProperty = AvaloniaProperty.Register<TreemapView, int>(nameof(Version));
    public IList<LibertyX.Partition.Core.Tools.SpaceNode>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public LibertyX.Partition.Core.Tools.SpaceNode? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public event Action<LibertyX.Partition.Core.Tools.SpaceNode>? Drill;
    static TreemapView() => AffectsRender<TreemapView>(ItemsProperty, SelectedProperty, VersionProperty);
    private List<(LibertyX.Partition.Core.Tools.SpaceNode node, Rect rect)> _rects = [];
    private static readonly Typeface Tf = new(new FontFamily("avares://LibertyXPartition/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.SemiBold);

    public static Color ColorFor(LibertyX.Partition.Core.Tools.SpaceNode n)
    {
        if (n.IsDir) return Color.Parse("#FBC501");
        return LibertyX.Partition.Core.Recovery.FileTypes.Categorize(n.Extension) switch
        {
            LibertyX.Partition.Core.Recovery.FileCategory.Image => Color.Parse("#EFE0B9"),
            LibertyX.Partition.Core.Recovery.FileCategory.Video => Color.Parse("#E3A23A"),
            LibertyX.Partition.Core.Recovery.FileCategory.Audio => Color.Parse("#D2783F"),
            LibertyX.Partition.Core.Recovery.FileCategory.Document => Color.Parse("#F7D774"),
            LibertyX.Partition.Core.Recovery.FileCategory.Archive => Color.Parse("#B08A62"),
            LibertyX.Partition.Core.Recovery.FileCategory.Executable => Color.Parse("#D3CABA"),
            _ => Color.Parse("#756C60"),
        };
    }

    public override void Render(DrawingContext ctx)
    {
        _rects = [];
        var items = Items;
        if (items == null || items.Count == 0 || Bounds.Width < 20) return;
        var layout = LibertyX.Partition.Core.Tools.Treemap.Layout(items.Where(i => i.Size > 0).Take(400).Select(i => (i, (double)i.Size)).ToList(),
            new LibertyX.Partition.Core.Tools.Treemap.Rect(0, 0, Bounds.Width, Bounds.Height));
        foreach (var (n, r) in layout)
        {
            var rect = new Rect(r.X, r.Y, r.W, r.H).Deflate(2);
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            _rects.Add((n, rect));
            var c = ColorFor(n);
            bool sel = n == Selected;
            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(sel ? (byte)120 : (byte)70, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(sel ? (byte)70 : (byte)28, c.R, c.G, c.B), 1) },
            };
            double rad = Math.Min(10, Math.Min(rect.Width, rect.Height) / 4);
            ctx.DrawRectangle(fill, new Pen(new SolidColorBrush(sel ? Color.Parse("#FBC501") : Color.FromArgb(70, c.R, c.G, c.B)), sel ? 2 : 1), rect, rad, rad);
            if (rect.Width > 70 && rect.Height > 34)
            {
                var t = new FormattedText(n.Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Tf, 12.5, new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)))
                { MaxTextWidth = rect.Width - 14, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                ctx.DrawText(t, new Point(rect.X + 8, rect.Y + 7));
                var s = new FormattedText(LibertyX.Partition.ViewModels.Fmt.Size(n.Size), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Tf, 11, new SolidColorBrush(Color.FromArgb(150, 235, 235, 245)))
                { MaxTextWidth = rect.Width - 14, MaxLineCount = 1 };
                ctx.DrawText(s, new Point(rect.X + 8, rect.Y + 24));
            }
        }
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        var hit = _rects.FirstOrDefault(r => r.rect.Contains(p)).node;
        if (hit != null)
        {
            Selected = hit;
            if (e.ClickCount >= 2 && hit.IsDir) Drill?.Invoke(hit);
        }
        base.OnPointerPressed(e);
    }
}
