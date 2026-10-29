using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.Platform;

public enum HealthLevel { Unknown, Good, Caution, Bad }

public sealed record SmartAttribute(int Id, string Name, int Current, int Worst, int Threshold, long Raw, bool Critical, HealthLevel Level);

public sealed class SmartReport
{
    public HealthLevel Health { get; set; } = HealthLevel.Unknown;
    public string Summary { get; set; } = L.T("S.M.A.R.T. indisponível neste disco (comum em adaptadores USB).");
    public int? TemperatureC { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public int? LifeRemainingPercent { get; set; }
    public long? HostBytesWritten { get; set; }
    public long? HostBytesRead { get; set; }
    public long? MediaErrors { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public List<SmartAttribute> Attributes { get; } = [];
    public string Source { get; set; } = "";
}

/// <summary>Interpretação dos dados S.M.A.R.T. (ATA) e do log de saúde do NVMe.</summary>
public static class SmartParser
{
    private static readonly Dictionary<int, (string name, bool critical)> Names = new()
    {
        [1] = (L.T("Taxa de erros de leitura"), false), [3] = (L.T("Tempo para girar"), false), [4] = ("Partidas/paradas", false),
        [5] = (L.T("Setores realocados"), true), [7] = (L.T("Taxa de erros de busca"), false), [9] = (L.T("Horas ligado"), false),
        [10] = (L.T("Tentativas de giro"), true), [12] = (L.T("Ciclos de energia"), false), [170] = (L.T("Blocos reserva disponíveis"), false),
        [171] = (L.T("Falhas de programação"), true), [172] = (L.T("Falhas de apagamento"), true), [173] = (L.T("Desgaste médio"), false),
        [174] = (L.T("Desligamentos inesperados"), false), [177] = (L.T("Contagem de desgaste"), false), [179] = (L.T("Blocos reserva usados"), false),
        [181] = (L.T("Falhas de programação"), true), [182] = (L.T("Falhas de apagamento"), true), [183] = (L.T("Erros de downshift SATA"), false),
        [184] = (L.T("Erros fim a fim"), true), [187] = (L.T("Erros incorrigíveis reportados"), true), [188] = (L.T("Comandos expirados"), false),
        [189] = (L.T("Escritas com a cabeça alta"), false), [190] = (L.T("Temperatura do fluxo de ar"), false), [191] = (L.T("Impactos (G-sense)"), false),
        [192] = (L.T("Desligamentos de emergência"), false), [193] = (L.T("Ciclos de carga"), false), [194] = (L.T("Temperatura"), false),
        [195] = (L.T("ECC recuperados"), false), [196] = (L.T("Eventos de realocação"), true), [197] = (L.T("Setores pendentes"), true),
        [198] = (L.T("Setores incorrigíveis"), true), [199] = (L.T("Erros de CRC no cabo"), false), [200] = (L.T("Erros de escrita"), false),
        [202] = (L.T("Vida útil usada"), false), [220] = (L.T("Deslocamento do disco"), false), [222] = (L.T("Horas carregado"), false),
        [226] = (L.T("Tempo de carga"), false), [230] = (L.T("Amplitude da cabeça"), false), [231] = (L.T("Vida útil restante"), false),
        [232] = (L.T("Reserva disponível"), false), [233] = (L.T("Desgaste da mídia"), false), [234] = (L.T("Média de apagamentos"), false),
        [235] = (L.T("Blocos bons"), false), [240] = (L.T("Horas de voo da cabeça"), false), [241] = (L.T("Total escrito (LBAs)"), false),
        [242] = (L.T("Total lido (LBAs)"), false), [249] = (L.T("Escritas NAND (GiB)"), false),
    };

    public static SmartReport ParseAta(byte[] values, byte[]? thresholds)
    {
        var r = new SmartReport { Source = "ATA S.M.A.R.T." };
        var thr = new Dictionary<int, int>();
        if (thresholds != null)
            for (int i = 0; i < 30; i++) { int o = 2 + i * 12; if (o + 2 <= thresholds.Length && thresholds[o] != 0) thr[thresholds[o]] = thresholds[o + 1]; }
        bool failing = false, caution = false;
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            if (o + 12 > values.Length) break;
            int id = values[o];
            if (id == 0) continue;
            int cur = values[o + 3], worst = values[o + 4];
            long raw = 0;
            for (int b = 0; b < 6; b++) raw |= (long)values[o + 5 + b] << (8 * b);
            thr.TryGetValue(id, out int t);
            var (name, critical) = Names.TryGetValue(id, out var n) ? n : (L.T($"Atributo {id}"), false);
            var level = HealthLevel.Good;
            if (t > 0 && cur <= t) { level = HealthLevel.Bad; failing = true; }
            else if (critical && (raw & 0xFFFFFFFF) > 0) { level = HealthLevel.Caution; caution = true; }
            r.Attributes.Add(new SmartAttribute(id, name, cur, worst, t, raw, critical, level));
            switch (id)
            {
                case 9: r.PowerOnHours = raw & 0xFFFFFFFF; break;
                case 12: r.PowerCycles = raw & 0xFFFFFFFF; break;
                case 194 or 190 when r.TemperatureC == null: { int c = (int)(raw & 0xFF); if (c is > 0 and < 120) r.TemperatureC = c; break; }
                case 231 or 202 or 233 when r.LifeRemainingPercent == null: if (cur is > 0 and <= 100) r.LifeRemainingPercent = cur; break;
                case 241: r.HostBytesWritten = (raw & 0xFFFFFFFFFFFF) * 512; break;
                case 242: r.HostBytesRead = (raw & 0xFFFFFFFFFFFF) * 512; break;
            }
        }
        r.Health = failing ? HealthLevel.Bad : caution ? HealthLevel.Caution : r.Attributes.Count > 0 ? HealthLevel.Good : HealthLevel.Unknown;
        long realloc = r.Attributes.FirstOrDefault(a => a.Id == 5)?.Raw & 0xFFFFFFFF ?? 0;
        long pending = r.Attributes.FirstOrDefault(a => a.Id == 197)?.Raw & 0xFFFFFFFF ?? 0;
        r.Summary = r.Health switch
        {
            HealthLevel.Bad => L.T("Falha prevista pelo próprio disco. Copie os dados agora (Backup → imagem de disco) antes de qualquer outra coisa."),
            HealthLevel.Caution => L.T($"Atenção: {realloc} setores realocados, {pending} pendentes. O disco está gastando a reserva — faça uma imagem dele."),
            HealthLevel.Good => L.T("Saudável: nenhum indicador crítico."),
            _ => r.Summary,
        };
        return r;
    }

    public static SmartReport ParseNvme(byte[] log)
    {
        var r = new SmartReport { Source = L.T("Log de saúde NVMe") };
        if (log.Length < 512) return r;
        int crit = log[0];
        int kelvin = U16(log, 1);
        if (kelvin > 200) r.TemperatureC = kelvin - 273;
        int spare = log[3], spareThr = log[4], used = log[5];
        r.LifeRemainingPercent = Math.Max(0, 100 - used);
        static long U128Low(byte[] b, int o) => (long)Math.Min(U64(b, o), long.MaxValue / 1000);
        r.HostBytesRead = U128Low(log, 32) * 512_000;
        r.HostBytesWritten = U128Low(log, 48) * 512_000;
        r.PowerCycles = U128Low(log, 112);
        r.PowerOnHours = U128Low(log, 128);
        r.UnsafeShutdowns = U128Low(log, 144);
        r.MediaErrors = U128Low(log, 160);
        long errLog = U128Low(log, 176);

        void Add(int id, string name, long raw, bool critical, HealthLevel lv) => r.Attributes.Add(new SmartAttribute(id, name, 0, 0, 0, raw, critical, lv));
        Add(1, L.T("Aviso crítico (bits)"), crit, true, crit != 0 ? HealthLevel.Bad : HealthLevel.Good);
        Add(2, L.T("Temperatura (°C)"), r.TemperatureC ?? 0, false, r.TemperatureC > 70 ? HealthLevel.Caution : HealthLevel.Good);
        Add(3, L.T("Reserva disponível (%)"), spare, true, spare < spareThr ? HealthLevel.Bad : HealthLevel.Good);
        Add(4, L.T("Limite da reserva (%)"), spareThr, false, HealthLevel.Good);
        Add(5, L.T("Vida útil usada (%)"), used, false, used >= 100 ? HealthLevel.Bad : used >= 90 ? HealthLevel.Caution : HealthLevel.Good);
        Add(6, L.T("Dados lidos (GB)"), r.HostBytesRead.Value / 1_000_000_000, false, HealthLevel.Good);
        Add(7, L.T("Dados escritos (GB)"), r.HostBytesWritten.Value / 1_000_000_000, false, HealthLevel.Good);
        Add(8, L.T("Ciclos de energia"), r.PowerCycles.Value, false, HealthLevel.Good);
        Add(9, L.T("Horas ligado"), r.PowerOnHours.Value, false, HealthLevel.Good);
        Add(10, L.T("Desligamentos inesperados"), r.UnsafeShutdowns.Value, false, HealthLevel.Good);
        Add(11, L.T("Erros de mídia/integridade"), r.MediaErrors.Value, true, r.MediaErrors > 0 ? HealthLevel.Caution : HealthLevel.Good);
        Add(12, L.T("Entradas no log de erros"), errLog, false, HealthLevel.Good);

        r.Health = crit != 0 || spare < spareThr || used >= 100 ? HealthLevel.Bad
                 : r.MediaErrors > 0 || used >= 90 ? HealthLevel.Caution : HealthLevel.Good;
        r.Summary = r.Health switch
        {
            HealthLevel.Bad => L.T("O SSD reporta estado crítico. Faça uma imagem dele já — SSD costuma morrer de uma vez."),
            HealthLevel.Caution => r.MediaErrors > 0 ? L.T($"{r.MediaErrors} erros de integridade de dados registrados. Vale fazer backup.") : L.T($"Vida útil quase no fim ({used}% usado)."),
            _ => L.T($"Saudável — {r.LifeRemainingPercent}% de vida útil restante."),
        };
        return r;
    }
}
