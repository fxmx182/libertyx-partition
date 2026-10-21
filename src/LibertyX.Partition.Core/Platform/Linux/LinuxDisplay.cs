using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Tamanho da interface no Linux. O Avalonia só aumenta a janela se a área de trabalho disser
/// (variáveis do Qt ou Xft.dpi); num monitor 4K com o desktop em 100% — ou com a escala só nas
/// configurações do GNOME/KDE, que o root não enxerga — tudo sai minúsculo. Aqui a escala vem,
/// em ordem: da escolha da pessoa, das variáveis do GTK/Qt, do Xft.dpi e, por último, do tamanho
/// físico de cada monitor (pixels ÷ milímetros, como o Windows sugere 150% num 4K de 27″).
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxDisplay
{
    public static readonly double[] Choices = [1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    /// <summary>O que vai para o Avalonia: um número (todas as telas) ou "DP-1=1.5;HDMI-1=1" (cada tela).</summary>
    public sealed record Scale(string Spec, string Source)
    {
        public bool PerScreen => Spec.Contains('=');
        public double Max => Spec.Split(';').Select(p => Parse(p.Contains('=') ? p[(p.IndexOf('=') + 1)..] : p) ?? 1).DefaultIfEmpty(1).Max();
        public string Percent => PerScreen
            ? string.Join(" / ", Spec.Split(';').Select(p => p.Split('=')).Select(p => $"{p[0]} {Pct(Parse(p[1]) ?? 1)}"))
            : Pct(Parse(Spec) ?? 1);
    }

    public static string Pct(double s) => $"{Math.Round(s * 100):0}%";

    // ───────── escolha da pessoa ─────────

    private static string ConfigFile => Path.Combine(LinuxSession.Home, ".config", "libertyx-partition", "interface.conf");

    /// <summary>Tamanho escolhido à mão; nulo = automático.</summary>
    public static double? Saved
    {
        get
        {
            try
            {
                foreach (var line in File.ReadAllLines(ConfigFile))
                    if (line.StartsWith("escala=")) return Parse(line["escala=".Length..]) is { } v && v is >= 0.5 and <= 4 ? v : null;
            }
            catch { }
            return null;
        }
    }

    public static void Save(double? scale)
    {
        string dir = Path.GetDirectoryName(ConfigFile)!;
        LinuxSession.EnsureUserDir(dir);
        File.WriteAllText(ConfigFile, scale is { } v ? "escala=" + v.ToString(CultureInfo.InvariantCulture) + "\n" : "escala=auto\n");
        LinuxSession.GiveBack(ConfigFile, false);
    }

    // ───────── na abertura ─────────

    /// <summary>A escala automática desta sessão (mostrada como "Automático (150%)" e repassada ao reabrir como root).</summary>
    public static Scale? Auto { get; private set; }
    /// <summary>A escala em uso nesta janela.</summary>
    public static Scale? Applied { get; private set; }

    /// <summary>
    /// Decide e aplica antes de o Avalonia abrir. <paramref name="hint"/> é a escala automática que a
    /// instância sem root calculou (com as variáveis do GTK/Qt que o pkexec não repassa).
    /// </summary>
    public static void Apply(string? hint)
    {
        // quem já mandou no Avalonia pelas variáveis dele manda (menos que a escolha feita no app)
        Auto = hint is { Length: > 0 } && Valid(hint) ? new Scale(hint, "repassada")
            : Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") is { Length: > 0 } g && Valid(g) ? new Scale(g, "AVALONIA_GLOBAL_SCALE_FACTOR")
            : Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS") is { Length: > 0 } sf && Valid(sf) ? new Scale(sf, "AVALONIA_SCREEN_SCALE_FACTORS")
            : Detect();
        Applied = Saved is { } s ? new Scale(Fmt(s), "escolhida") : Auto;
        bool per = Applied.PerScreen;
        Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", per ? null : Applied.Spec);
        Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", per ? Applied.Spec : null);
    }

    /// <summary>Os argumentos para reabrir o app: sem a escala antiga, com a automática desta sessão.</summary>
    public static List<string> RelaunchArgs(IEnumerable<string> args)
    {
        var l = args.Where(a => !a.StartsWith("--escala=")).ToList();
        if (Auto != null) l.Add("--escala=" + Auto.Spec);
        return l;
    }

    // ───────── detecção ─────────

    public static Scale Detect()
    {
        // GTK e Qt: quem usa variáveis de ambiente para a escala
        double gdk = (int.TryParse(Environment.GetEnvironmentVariable("GDK_SCALE"), out var gi) && gi > 0 ? gi : 1)
            * (Parse(Environment.GetEnvironmentVariable("GDK_DPI_SCALE")) is { } gd and > 0 ? gd : 1);
        if (gdk > 1.01) return new Scale(Fmt(gdk), "GDK_SCALE");
        if (Parse(Environment.GetEnvironmentVariable("QT_SCALE_FACTOR")) is { } q and > 1.01) return new Scale(Fmt(q), "QT_SCALE_FACTOR");

        IntPtr dpy = IntPtr.Zero;
        try
        {
            dpy = XOpenDisplay(IntPtr.Zero);
            if (dpy == IntPtr.Zero) return new Scale("1", "sem tela");
            // Xft.dpi: o "tamanho do texto" do GNOME, KDE, Xfce, Cinnamon… (96 = 100%)
            if (XftDpi(dpy) is { } dpi && dpi > 100) return new Scale(Fmt(Snap(dpi / 96.0, 0.05)), $"Xft.dpi {dpi:0}");
            // nada configurado: o tamanho físico de cada monitor decide
            var mons = Monitors(dpy);
            var guess = mons.Select(m => (m.Name, s: Physical(m))).ToList();
            if (guess.Count == 0 || guess.All(g => g.s <= 1)) return new Scale("1", "monitor comum");
            if (guess.Select(g => g.s).Distinct().Count() == 1 || guess.Any(g => string.IsNullOrEmpty(g.Name)))
                return new Scale(Fmt(guess.Max(g => g.s)), Describe(mons));
            return new Scale(string.Join(';', guess.Select(g => $"{g.Name}={Fmt(g.s)}")), Describe(mons));
        }
        catch { return new Scale("1", "sem tela"); }
        finally { if (dpy != IntPtr.Zero) XCloseDisplay(dpy); }
    }

    /// <summary>Texto de diagnóstico (--escala-info): de onde viria cada escala.</summary>
    public static string Report()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var k in new[] { "GDK_SCALE", "GDK_DPI_SCALE", "QT_SCALE_FACTOR", "QT_SCREEN_SCALE_FACTORS", "AVALONIA_GLOBAL_SCALE_FACTOR", "AVALONIA_SCREEN_SCALE_FACTORS", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP" })
            sb.AppendLine($"{k}={Environment.GetEnvironmentVariable(k)}");
        IntPtr dpy = XOpenDisplay(IntPtr.Zero);
        if (dpy == IntPtr.Zero) sb.AppendLine("sem conexão com o X");
        else
        {
            try
            {
                sb.AppendLine($"Xft.dpi={XftDpi(dpy)?.ToString(CultureInfo.InvariantCulture) ?? "(não definido)"}");
                foreach (var m in Monitors(dpy))
                    sb.AppendLine($"monitor {m.Name}: {m.Width}×{m.Height} px, {m.WidthMm}×{m.HeightMm} mm → {Pct(Physical(m))}");
            }
            finally { XCloseDisplay(dpy); }
        }
        sb.AppendLine($"escolhida no app: {(Saved is { } s ? Pct(s) : "automático")}");
        var d = Detect();
        sb.AppendLine($"automático: {d.Spec} ({d.Source})");
        return sb.ToString();
    }

    public sealed record Monitor(string Name, int Width, int Height, int WidthMm, int HeightMm);

    /// <summary>
    /// A escala que um monitor pede pelo tamanho físico: 4K de 27″ (163 dpi) → 150%, 4K de 15,6″ → 250%,
    /// 1440p de 27″ → 100%. O fator 0,88 é o que o Windows recomenda (um pouco menor que o dpi puro).
    /// Um compositor que já amplia o app (XWayland em 200%) mostra a tela com metade dos pixels — fica 100%.
    /// </summary>
    public static double Physical(Monitor m)
    {
        if (m.WidthMm < 200 || m.HeightMm < 110 || m.Width <= 0) return 1;      // projetor, TV sem tamanho, só a proporção (16×9 cm)
        double ratioPx = (double)m.Width / m.Height, ratioMm = (double)m.WidthMm / m.HeightMm;
        if (Math.Abs(ratioPx / ratioMm - 1) > 0.2) return 1;                       // mm não batem com a imagem (girada ou falso)
        double dpi = m.Width * 25.4 / m.WidthMm;
        if (dpi < 125) return 1;
        return Math.Clamp(Snap(dpi / 96.0 * 0.88, 0.25), 1, 3);
    }

    private static string Describe(List<Monitor> mons) => string.Join(", ", mons.Select(m =>
        $"{m.Name} {m.Width}×{m.Height} {Math.Sqrt(m.WidthMm * m.WidthMm + m.HeightMm * m.HeightMm) / 25.4:0.#}″"));

    private static double Snap(double v, double step) => Math.Round(v / step) * step;
    private static string Fmt(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
    private static double? Parse(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static bool Valid(string spec) => spec.Split(';').All(p => Parse(p.Contains('=') ? p[(p.IndexOf('=') + 1)..] : p) is >= 0.5 and <= 4);

    // ───────── X11 ─────────

    private static double? XftDpi(IntPtr dpy)
    {
        var p = XResourceManagerString(dpy);
        if (p == IntPtr.Zero) return null;
        foreach (var line in (Marshal.PtrToStringUTF8(p) ?? "").Split('\n'))
        {
            var kv = line.Split(':', 2);
            if (kv.Length == 2 && kv[0].Trim() == "Xft.dpi" && Parse(kv[1].Trim()) is { } d && d > 0) return d;
        }
        return null;
    }

    /// <summary>Os monitores pelo RandR 1.5 (os mesmos nomes que o Avalonia usa para a escala por tela).</summary>
    public static List<Monitor> Monitors(IntPtr dpy)
    {
        var list = new List<Monitor>();
        var arr = XRRGetMonitors(dpy, XDefaultRootWindow(dpy), 1, out int n);
        if (arr == IntPtr.Zero) return list;
        try
        {
            int size = Marshal.SizeOf<XRRMonitorInfo>();
            for (int i = 0; i < n; i++)
            {
                var m = Marshal.PtrToStructure<XRRMonitorInfo>(arr + i * size);
                string name = "";
                var np = XGetAtomName(dpy, m.name);
                if (np != IntPtr.Zero) { name = Marshal.PtrToStringUTF8(np) ?? ""; XFree(np); }
                list.Add(new Monitor(name, m.width, m.height, m.mwidth, m.mheight));
            }
        }
        finally { XRRFreeMonitors(arr); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRMonitorInfo
    {
        public nuint name;
        public int primary, automatic, noutput, x, y, width, height, mwidth, mheight;
        public IntPtr outputs;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr dpy);
    [DllImport("libX11.so.6")] private static extern IntPtr XResourceManagerString(IntPtr dpy);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(IntPtr dpy);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetAtomName(IntPtr dpy, nuint atom);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr p);
    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetMonitors(IntPtr dpy, nuint window, int getActive, out int nmonitors);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(IntPtr monitors);
}
