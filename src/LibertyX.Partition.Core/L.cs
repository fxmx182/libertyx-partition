using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LibertyX.Partition.Core.Platform.Linux;

namespace LibertyX.Partition.Core;

/// <summary>
/// Idioma da interface. O texto em português fica no código e é a própria chave: <c>L.T("Cancelar")</c>
/// e <c>L.T($"Copiando {n} de {total}")</c> (a chave vira "Copiando {0} de {1}"). As traduções ficam
/// em Lang/en.json e Lang/es.json; o que não estiver lá aparece em português. O idioma é decidido
/// uma vez, na abertura (escolha salva &gt; o que a instância sem root repassou &gt; o do sistema), e
/// trocar reabre o app — como a escala da interface.
/// </summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages = [("pt", "Português"), ("en", "English"), ("es", "Español")];

    /// <summary>"pt", "en" ou "es".</summary>
    public static string Lang { get; private set; } = "pt";
    /// <summary>O idioma do sistema (o que vale no automático).</summary>
    public static string OsLang { get; private set; } = "pt";

    private static Dictionary<string, string> dict = new();
    private static string? missingLog;

    /// <summary>
    /// Decide o idioma. <paramref name="hint"/> é o do sistema que a instância sem root viu (o pkexec
    /// não repassa LANG); <paramref name="forced"/> passa por cima de tudo (o autoteste é em português).
    /// </summary>
    public static void Init(string? hint, string? forced = null)
    {
        OsLang = hint is { Length: > 0 } h && Known(h) ? h : Detect();
        Lang = forced ?? Saved ?? OsLang;
        dict = Lang == "pt" ? new() : Load(Lang);
        missingLog = Environment.GetEnvironmentVariable("LIBERTYX_LANG_MISSING");
    }

    private static bool Known(string code) => Languages.Any(l => l.Code == code);

    /// <summary>pt e es pelo prefixo; qualquer outro idioma do sistema abre em inglês.</summary>
    public static string Map(string? culture)
    {
        string c = (culture ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("pt")) return "pt";
        if (c.StartsWith("es")) return "es";
        return "en";
    }

    private static string Detect()
    {
        if (OperatingSystem.IsLinux())
        {
            // a mesma ordem do gettext: LANGUAGE (lista), LC_ALL, LC_MESSAGES, LANG
            foreach (var v in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" })
            {
                string s = Environment.GetEnvironmentVariable(v) ?? "";
                s = s.Split(':')[0];
                if (s.Length == 0 || s is "C" || s.StartsWith("C.") || s == "POSIX") continue;
                return Map(s);
            }
            return "en";
        }
        return Map(CultureInfo.CurrentUICulture.Name);
    }

    // ───────── escolha da pessoa ─────────

    private static string ConfigFile => OperatingSystem.IsLinux()
        ? Path.Combine(LinuxSession.Home, ".config", "libertyx-partition", "idioma.conf")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LibertyX Partition", "idioma.conf");

    /// <summary>Idioma escolhido à mão; nulo = o do sistema.</summary>
    public static string? Saved
    {
        get
        {
            try { return File.ReadAllText(ConfigFile).Trim() is var s && Known(s) ? s : null; }
            catch { return null; }
        }
    }

    public static void Save(string? code)
    {
        string dir = Path.GetDirectoryName(ConfigFile)!;
        if (OperatingSystem.IsLinux()) LinuxSession.EnsureUserDir(dir); else Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigFile, (code ?? "auto") + "\n");
        if (OperatingSystem.IsLinux()) LinuxSession.GiveBack(ConfigFile, false);
    }

    // ───────── tradução ─────────

    private static Dictionary<string, string> Load(string code)
    {
        var d = new Dictionary<string, string>();
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lang.{code}.json");
            if (s == null) return d;
            using var doc = JsonDocument.Parse(s);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.GetString() is { Length: > 0 } v) d[p.Name] = v;
        }
        catch { }
        return d;
    }

    /// <summary>O texto no idioma da interface (o português, se não houver tradução).</summary>
    public static string T(string pt)
    {
        if (dict.TryGetValue(pt, out var t)) return t;
        if (missingLog != null && Lang != "pt") Missing(pt);
        return pt;
    }

    /// <summary>Texto com valores: a chave é o modelo ("Copiando {0} de {1}"), a tradução pode mudar a ordem.</summary>
    public static string T(ref Text text)
    {
        string fmt = text.Format;
        if (!dict.TryGetValue(fmt, out var t))
        {
            if (missingLog != null && Lang != "pt") Missing(fmt);
            t = fmt;
        }
        try { return string.Format(CultureInfo.CurrentCulture, t, text.Args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, fmt, text.Args); }
    }

    private static readonly HashSet<string> logged = new();
    private static void Missing(string key)
    {
        lock (logged)
        {
            if (!logged.Add(key)) return;
            try { File.AppendAllText(missingLog!, key.Replace("\n", "\\n") + "\n"); } catch { }
        }
    }

    /// <summary>Monta o modelo "{0}…{1}" de uma string interpolada, guardando os valores.</summary>
    [InterpolatedStringHandler]
    public ref struct Text
    {
        private readonly StringBuilder sb;
        private readonly List<object?> args;

        public Text(int literalLength, int formattedCount)
        {
            sb = new StringBuilder(literalLength + formattedCount * 4);
            args = new List<object?>(formattedCount);
        }

        public readonly string Format => sb.ToString();
        public readonly object?[] Args => args.ToArray();

        public readonly void AppendLiteral(string s) => sb.Append(s.Replace("{", "{{").Replace("}", "}}"));

        public readonly void AppendFormatted<TValue>(TValue value) => Hole(value, null, null);
        public readonly void AppendFormatted<TValue>(TValue value, string? format) => Hole(value, null, format);
        public readonly void AppendFormatted<TValue>(TValue value, int alignment) => Hole(value, alignment, null);
        public readonly void AppendFormatted<TValue>(TValue value, int alignment, string? format) => Hole(value, alignment, format);
        public readonly void AppendFormatted(string? value) => Hole(value, null, null);

        private readonly void Hole(object? value, int? alignment, string? format)
        {
            sb.Append('{').Append(args.Count);
            if (alignment is { } a) sb.Append(',').Append(a);
            if (format != null) sb.Append(':').Append(format);
            sb.Append('}');
            args.Add(value);
        }
    }
}
