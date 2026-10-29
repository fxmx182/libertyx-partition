using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LibertyX.Partition.ViewModels;

namespace LibertyX.Partition.Controls;

/// <summary>
/// O mapa do disco: cada partição é uma placa proporcional ao tamanho (com largura mínima para
/// as pequenas não sumirem). O usado é metal escovado no tom do sistema de arquivos — todos tons
/// de ouro, marfim, âmbar e cobre —, o livre é riscado fino. Clique seleciona.
/// </summary>
public sealed class PartitionRibbon : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty = AvaloniaProperty.Register<PartitionRibbon, IEnumerable?>(nameof(Items));
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<PartitionRibbon, bool>(nameof(Compact));

    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool Compact { get => GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    private readonly List<(PartitionVM vm, Rect rect)> _hit = [];
    private PartitionVM? _hover;
    private static readonly Typeface Bold = new(new FontFamily("avares://LibertyXPartition/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface Regular = new(new FontFamily("avares://LibertyXPartition/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.Medium);

    static PartitionRibbon()
    {
        AffectsRender<PartitionRibbon>(ItemsProperty, CompactProperty);
        ItemsProperty.Changed.AddClassHandler<PartitionRibbon>((r, e) =>
        {
            if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= r.OnCollection;
            if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += r.OnCollection;
            r.Hook();
        });
    }

    public PartitionRibbon() { Cursor = new Cursor(StandardCursorType.Hand); ClipToBounds = false; }

    private void OnCollection(object? s, NotifyCollectionChangedEventArgs e) { Hook(); InvalidateVisual(); }

    private void Hook()
    {
        if (Items == null) return;
        foreach (var vm in Items.OfType<PartitionVM>())
        {
            vm.PropertyChanged -= OnVm;
            vm.PropertyChanged += OnVm;
        }
    }

    private void OnVm(object? s, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(0, Compact ? 32 : 70);

    public override void Render(DrawingContext ctx)
    {
        _hit.Clear();
        var items = Items?.OfType<PartitionVM>().ToList() ?? [];
        var b = Bounds;
        if (items.Count == 0 || b.Width < 10) return;
        double gap = 4, minW = Compact ? 18 : 46;
        double total = items.Sum(i => (double)i.P.Length);
        double avail = b.Width - gap * (items.Count - 1);
        // largura mínima e o resto proporcional
        var w = items.Select(i => Math.Max(minW, avail * i.P.Length / total)).ToList();
        double over = w.Sum() - avail;
        if (over > 0)
        {
            double big = w.Where(x => x > minW).Sum();
            for (int i = 0; i < w.Count; i++) if (w[i] > minW) w[i] -= over * w[i] / big;
        }
        double x = 0, h = b.Height;
        double r = Compact ? 7 : 12;
        for (int i = 0; i < items.Count; i++)
        {
            var vm = items[i];
            var rect = new Rect(x, 0, Math.Max(2, w[i]), h);
            _hit.Add((vm, rect));
            var c = vm.Color;
            bool sel = vm.IsSelected, hov = vm == _hover;
            var shape = new RoundedRect(rect, r);

            // a seleção acende: um halo de ouro em camadas por fora da placa
            if (sel)
                for (int k = 3; k >= 1; k--)
                    ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(16 * (4 - k)), 0xFB, 0xC5, 0x01)), k * 2.2), rect.Inflate(k * 0.9), r + k, r + k);

            if (vm.IsUnallocated)
            {
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)), null, rect, r, r);
                using (ctx.PushClip(shape)) Hatch(ctx, rect, Color.FromArgb(16, 255, 255, 255), 7);
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 1, new DashStyle([3, 3], 0)), rect.Deflate(0.5), r, r);
            }
            else
            {
                // a placa: vidro morno com o tom do sistema de arquivos
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(16, c.R, c.G, c.B)), null, rect, r, r);
                double used = vm.HasUsage ? vm.UsedFraction : 0;
                using (ctx.PushClip(shape))
                {
                    // o espaço livre é riscado de leve, como planta de engenharia
                    var free = new Rect(rect.X + rect.Width * used, rect.Y, rect.Width * (1 - used), rect.Height);
                    using (ctx.PushClip(free)) Hatch(ctx, rect, Color.FromArgb(14, c.R, c.G, c.B), 6);
                    // o usado é metal escovado: degradê do tom e um reflexo horizontal
                    if (used > 0)
                    {
                        var fill = new Rect(rect.X, rect.Y, Math.Max(3, rect.Width * used), rect.Height);
                        ctx.DrawRectangle(new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                            GradientStops =
                            {
                                new GradientStop(Color.FromArgb(120, c.R, c.G, c.B), 0),
                                new GradientStop(Color.FromArgb(70, c.R, c.G, c.B), 0.45),
                                new GradientStop(Color.FromArgb(38, c.R, c.G, c.B), 1),
                            },
                        }, null, fill);
                        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, c.R, c.G, c.B)), null, new Rect(fill.Right - 1.5, fill.Y, 1.5, fill.Height));
                    }
                    // o fio do tom no topo: identifica a partição mesmo vazia
                    ctx.DrawRectangle(new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(c, 0), new GradientStop(Color.FromArgb(90, c.R, c.G, c.B), 1) },
                    }, null, new Rect(rect.X, rect.Y, rect.Width, Compact ? 2 : 2.5));
                }
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(hov ? 110 : 52), c.R, c.G, c.B)), 1), rect.Deflate(0.5), r, r);
            }
            if (sel)
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#FBC501")), 1.6), rect.Deflate(0.8), r, r);

            if (!Compact && rect.Width > 54)
            {
                var title = new FormattedText(vm.Short, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 13, new SolidColorBrush(Color.FromArgb((byte)(vm.IsUnallocated ? 160 : 245), 255, 250, 238)));
                var sub = new FormattedText(vm.SizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Regular, 11.5, new SolidColorBrush(Color.FromArgb(160, 235, 230, 220)));
                title.MaxTextWidth = rect.Width - 20; title.MaxLineCount = 1; title.Trimming = TextTrimming.CharacterEllipsis;
                sub.MaxTextWidth = rect.Width - 20; sub.MaxLineCount = 1; sub.Trimming = TextTrimming.CharacterEllipsis;
                ctx.DrawText(title, new Point(rect.X + 12, rect.Y + 15));
                ctx.DrawText(sub, new Point(rect.X + 12, rect.Y + 35));
                if (vm.HasUsage && rect.Width > 110)
                {
                    var pct = new FormattedText(vm.UsagePercent, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 11, new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B)));
                    ctx.DrawText(pct, new Point(rect.Right - pct.Width - 12, rect.Y + 15));
                }
            }
            x += w[i] + gap;
        }
    }

    /// <summary>Riscado diagonal fino, a 45°, cobrindo o retângulo.</summary>
    private static void Hatch(DrawingContext ctx, Rect rect, Color color, double step)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        for (double d = -rect.Height; d < rect.Width; d += step)
            ctx.DrawLine(pen, new Point(rect.X + d, rect.Bottom), new Point(rect.X + d + rect.Height, rect.Y));
    }

    public PartitionVM? HitTest(Point p) => _hit.FirstOrDefault(t => t.rect.Contains(p)).vm;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var h = _hit.FirstOrDefault(t => t.rect.Contains(p)).vm;
        if (h != _hover)
        {
            _hover = h;
            ToolTip.SetTip(this, h == null ? null : $"{h.Title}\n{h.FsText} · {h.SizeText}" + (h.HasUsage ? L.T($" · {h.UsagePercent} usado") : ""));
            InvalidateVisual();
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e) { _hover = null; InvalidateVisual(); base.OnPointerExited(e); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        var h = _hit.FirstOrDefault(t => t.rect.Contains(p)).vm;
        if (h != null) { h.SelectCommand.Execute(null); e.Handled = true; }
        base.OnPointerPressed(e);
    }
}

/// <summary>Anel de uso, desenhado como um mostrador: o arco em degradê do tom e, com
/// <see cref="Ticks"/>, uma coroa de marcações que acendem até o quanto está usado.</summary>
public sealed class UsageRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<UsageRing, double>(nameof(Value));
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<UsageRing, Color>(nameof(Color), Color.Parse("#FBC501"));
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<UsageRing, double>(nameof(Thickness), 10);
    public static readonly StyledProperty<bool> IndeterminateProperty = AvaloniaProperty.Register<UsageRing, bool>(nameof(Indeterminate));
    public static readonly StyledProperty<bool> TicksProperty = AvaloniaProperty.Register<UsageRing, bool>(nameof(Ticks));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool Indeterminate { get => GetValue(IndeterminateProperty); set => SetValue(IndeterminateProperty, value); }
    public bool Ticks { get => GetValue(TicksProperty); set => SetValue(TicksProperty, value); }

    static UsageRing() => AffectsRender<UsageRing>(ValueProperty, ColorProperty, ThicknessProperty, IndeterminateProperty, TicksProperty);

    private double _spin;
    private Avalonia.Threading.DispatcherTimer? _timer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IndeterminateProperty) UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); UpdateTimer(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer?.Stop(); }

    private void UpdateTimer()
    {
        if (Indeterminate && VisualRoot != null)
        {
            _timer ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render, (_, _) => { _spin = (_spin + 4) % 360; InvalidateVisual(); });
            _timer.Start();
        }
        else _timer?.Stop();
    }

    public override void Render(DrawingContext ctx)
    {
        double s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0) return;
        double t = Thickness, rad = (s - t) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var col = Color;
        double v = Math.Clamp(Value, 0, 1);
        if (Ticks && !Indeterminate)
        {
            // a coroa: 48 marcações por fora, as do usado acesas no tom
            double outer = s / 2, inner = outer - 5;
            rad = inner - 4 - t / 2;
            const int n = 48;
            for (int i = 0; i < n; i++)
            {
                double a = (-90 + i * 360.0 / n) * Math.PI / 180;
                bool lit = (i + 0.5) / n <= v;
                bool major = i % 6 == 0;
                double ri = major ? inner - 2 : inner;
                var pen0 = new Pen(new SolidColorBrush(lit ? Color.FromArgb(230, col.R, col.G, col.B) : Color.FromArgb((byte)(major ? 60 : 30), 255, 255, 255)), major ? 1.6 : 1.1, lineCap: PenLineCap.Round);
                ctx.DrawLine(pen0, new Point(c.X + ri * Math.Cos(a), c.Y + ri * Math.Sin(a)), new Point(c.X + outer * Math.Cos(a), c.Y + outer * Math.Sin(a)));
            }
        }
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), t), c, rad, rad);
        double start = Indeterminate ? _spin : -90, sweep = Indeterminate ? 90 : v * 360;
        if (sweep <= 0.2) return;
        var pen = new Pen(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Lighten(col), 0), new GradientStop(col, 0.5), new GradientStop(Darken(col), 1) },
        }, t, lineCap: PenLineCap.Round);
        if (sweep >= 359.9) { ctx.DrawEllipse(null, pen, c, rad, rad); return; }
        ctx.DrawGeometry(null, pen, Arc(c, rad, start, sweep));
    }

    public static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using var gc = g.Open();
        gc.BeginFigure(p0, false);
        gc.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
        gc.EndFigure(false);
        return g;
    }

    private static Color Lighten(Color c) => Color.FromRgb((byte)Math.Min(255, c.R + 50), (byte)Math.Min(255, c.G + 50), (byte)Math.Min(255, c.B + 50));
    private static Color Darken(Color c) => Color.FromRgb((byte)(c.R * 0.72), (byte)(c.G * 0.72), (byte)(c.B * 0.72));
}
