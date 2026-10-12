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
/// O mapa do disco: cada partição é uma placa de vidro proporcional ao tamanho (com largura
/// mínima para as pequenas não sumirem). A cor do sistema de arquivos aparece só num ponto ao
/// lado do nome e num trilho fino de uso no rodapé, para o disco ler-se de longe sem gritar.
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

    protected override Size MeasureOverride(Size availableSize) => new(0, Compact ? 30 : 68);

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
        double r = Compact ? 6 : 10;
        for (int i = 0; i < items.Count; i++)
        {
            var vm = items[i];
            var rect = new Rect(x, 0, Math.Max(2, w[i]), h);
            _hit.Add((vm, rect));
            var c = vm.Color;
            bool sel = vm.IsSelected, hov = vm == _hover;
            byte bg = (byte)(sel ? 20 : hov ? 15 : 9);
            if (vm.IsUnallocated)
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(bg / 2), 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), 1, new DashStyle([2, 3], 0)), rect.Deflate(0.5), r, r);
            else
            {
                // vidro com um leve tom da cor do sistema de arquivos, quase imperceptível
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(bg, 255, 255, 255)), null, rect, r, r);
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(10, c.R, c.G, c.B)), new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1), rect.Deflate(0.5), r, r);
                // trilho de uso no rodapé
                double th = Compact ? 3 : 4, pad = Compact ? 5 : 10;
                if (rect.Width > pad * 2 + 6)
                {
                    var track = new Rect(rect.X + pad, rect.Bottom - pad - th + (Compact ? 1 : 0), rect.Width - pad * 2, th);
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), null, track, th / 2, th / 2);
                    double used = vm.HasUsage ? vm.UsedFraction : 0;
                    double fw = used > 0 ? Math.Max(th, track.Width * used) : th;
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(vm.HasUsage ? 230 : 120), c.R, c.G, c.B)), null, new Rect(track.X, track.Y, fw, th), th / 2, th / 2);
                }
            }
            if (sel)
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1.3), rect.Deflate(0.65), r, r);
            else if (hov)
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1), rect.Deflate(0.5), r, r);

            if (!Compact && rect.Width > 54)
            {
                bool dot = !vm.IsUnallocated && rect.Width > 70;
                double tx = rect.X + (dot ? 24 : 12);
                var title = new FormattedText(vm.Short, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 12.5, new SolidColorBrush(Color.FromArgb((byte)(vm.IsUnallocated ? 150 : 235), 237, 237, 240)));
                var sub = new FormattedText(vm.SizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Regular, 11.5, new SolidColorBrush(Color.FromArgb(130, 237, 237, 240)));
                title.MaxTextWidth = Math.Max(1, rect.Right - tx - 10); title.MaxLineCount = 1; title.Trimming = TextTrimming.CharacterEllipsis;
                sub.MaxTextWidth = Math.Max(1, rect.Right - rect.X - 22); sub.MaxLineCount = 1; sub.Trimming = TextTrimming.CharacterEllipsis;
                if (dot) ctx.DrawEllipse(new SolidColorBrush(c), null, new Point(rect.X + 15, rect.Y + 20.5), 3.5, 3.5);
                ctx.DrawText(title, new Point(tx, rect.Y + 12));
                ctx.DrawText(sub, new Point(rect.X + 12, rect.Y + 32));
                if (vm.HasUsage && rect.Width > 120)
                {
                    var pct = new FormattedText(vm.UsagePercent, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Regular, 11, new SolidColorBrush(Color.FromArgb(120, 237, 237, 240)));
                    ctx.DrawText(pct, new Point(rect.Right - pct.Width - 12, rect.Y + 12));
                }
            }
            x += w[i] + gap;
        }
    }

    public PartitionVM? HitTest(Point p) => _hit.FirstOrDefault(t => t.rect.Contains(p)).vm;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var h = _hit.FirstOrDefault(t => t.rect.Contains(p)).vm;
        if (h != _hover)
        {
            _hover = h;
            ToolTip.SetTip(this, h == null ? null : $"{h.Title}\n{h.FsText} · {h.SizeText}" + (h.HasUsage ? $" · {h.UsagePercent} usado" : ""));
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

/// <summary>Anel de uso: um traço fino e liso sobre um trilho discreto, na cor do sistema de arquivos.</summary>
public sealed class UsageRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<UsageRing, double>(nameof(Value));
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<UsageRing, Color>(nameof(Color), Color.Parse("#E7C064"));
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<UsageRing, double>(nameof(Thickness), 10);
    public static readonly StyledProperty<bool> IndeterminateProperty = AvaloniaProperty.Register<UsageRing, bool>(nameof(Indeterminate));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool Indeterminate { get => GetValue(IndeterminateProperty); set => SetValue(IndeterminateProperty, value); }

    static UsageRing() => AffectsRender<UsageRing>(ValueProperty, ColorProperty, ThicknessProperty, IndeterminateProperty);

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
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), t), c, rad, rad);
        double start = Indeterminate ? _spin : -90, sweep = Indeterminate ? 90 : Math.Clamp(Value, 0, 1) * 360;
        if (sweep <= 0.2) return;
        var pen = new Pen(new SolidColorBrush(Color), t, lineCap: PenLineCap.Round);
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

}
