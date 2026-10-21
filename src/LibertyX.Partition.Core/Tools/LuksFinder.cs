using System.Security.Cryptography;
using System.Text.Json;
using LibertyX.Partition.Core.FileSystems;
using LibertyX.Partition.Core.IO;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Platform;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.Tools;

public enum LuksScanMode { Quick, Full }

/// <summary>Ativo = o contêiner em uso; Abre = resto que ainda abre com a senha; Morto = só o cabeçalho, sem chave inteira;
/// Arquivo = guardado num arquivo de hoje (contêiner em arquivo ou backup de cabeçalho), que não se apaga por fora.</summary>
public enum LuksState { Active, Openable, Dead, InFile }

/// <summary>Um pedaço do disco como ele está hoje: partição (com o sistema de arquivos atual) ou espaço livre.</summary>
public sealed record LuksArea(long Offset, long Length, string Name, FsKind Fs, bool IsPartition, bool Critical, bool Whole = false)
{
    public string Of => Whole ? "do disco" : IsPartition ? $"da {Name}" : $"do {Name}";
    public string In => Whole ? "no disco" : IsPartition ? $"na {Name}" : $"no {Name}";
    public long End => Offset + Length;
    public bool Contains(long o) => o >= Offset && o < End;
}

/// <summary>Área de uma chave: o material “AF-split” — basta um setor dele estragar para a chave não abrir mais.</summary>
public sealed record LuksSlot(int Index, long Offset, long AreaLength, long MaterialLength, int Sectors, int Damaged)
{
    public bool Intact => Sectors > 0 && Damaged == 0;
}

public sealed record LuksProgress(long Done, long Total, int Found, double BytesPerSecond);

public sealed class LuksFinding
{
    public long Start { get; init; }
    public int Version { get; init; }
    public string? Uuid { get; set; }
    public string? Label { get; set; }
    public string? Cipher { get; set; }
    /// <summary>LUKS2: tamanho de cada cópia do cabeçalho (binário + JSON). LUKS1: a área antes da 1ª chave.</summary>
    public long HeaderSize { get; set; }
    /// <summary>Cópias achadas: deslocamento → íntegra (no LUKS2, o checksum confere).</summary>
    public SortedDictionary<long, bool> Headers { get; } = [];
    public bool PrimaryOk => Headers.TryGetValue(Start, out var ok) && ok;
    public bool SecondaryOk => Version == 2 && Headers.TryGetValue(Start + HeaderSize, out var ok) && ok;
    public bool AnyHeaderOk => Headers.Values.Any(v => v);
    public List<LuksSlot> Slots { get; } = [];
    public int IntactSlots => Slots.Count(s => s.Intact);
    public long DataOffset { get; set; }
    /// <summary>Fração das amostras da área de dados com cara de dado cifrado (-1 = não medido).</summary>
    public double DataRandom { get; set; } = -1;
    public LuksState State { get; set; }
    public string Where { get; set; } = "";
    public LuksArea? Area { get; set; }

    public string Title => $"LUKS{Version}" + (Cipher is { Length: > 0 } c ? $" · {c}" : "") + (Label is { Length: > 0 } l ? $" · “{l}”" : "");

    public string StateText => State switch
    {
        LuksState.Active => "Contêiner em uso",
        LuksState.Openable => "Ainda abre com a senha",
        LuksState.InFile => "Dentro de um arquivo",
        _ => "Resto que não abre mais",
    };

    public string Explain()
    {
        if (State == LuksState.InFile)
            return "Está guardado num arquivo que existe hoje nessa partição — um contêiner em arquivo ou um backup de cabeçalho (que, com a senha, reabre o volume original). "
                   + "Por fora não se apaga sem estragar o sistema de arquivos: apague o arquivo pelo sistema e esvazie a lixeira.";
        string hdr = Version == 1 ? (PrimaryOk ? "cabeçalho inteiro" : "cabeçalho danificado")
            : PrimaryOk && SecondaryOk ? "as duas cópias do cabeçalho inteiras"
            : PrimaryOk ? "cabeçalho principal inteiro (a cópia reserva sumiu)"
            : SecondaryOk ? "só a cópia reserva do cabeçalho — a principal foi sobrescrita, mas o cryptsetup usa a reserva"
            : "cabeçalho danificado (o checksum não confere)";
        string keys = Slots.Count == 0 ? "nenhuma chave registrada" : $"{IntactSlots} de {Slots.Count} {(Slots.Count == 1 ? "chave" : "chaves")} com o material inteiro";
        string data = DataRandom < 0 ? "" : DataRandom > 0.9 ? "; os dados cifrados ainda estão lá" : DataRandom > 0.2 ? "; parte dos dados cifrados ainda está lá" : "; a área de dados já foi reescrita";
        string tail = State switch
        {
            LuksState.Active => ". É um volume criptografado de verdade, em uso.",
            LuksState.Openable => ". Quem tiver a senha (ou o arquivo de chave) ainda abre isso.",
            _ => ". Sem chave inteira, não abre nem com a senha — mas ainda revela que o disco foi criptografado (UUID, rótulo, cifra).",
        };
        return $"{hdr}; {keys}{data}{tail}";
    }

    /// <summary>O que apagar para não sobrar nada: as cópias do cabeçalho e as áreas das chaves (os dados sem chave já são ruído).</summary>
    public List<(long off, long len)> Regions()
    {
        var r = new List<(long, long)>();
        if (Version == 2) { r.Add((Start, HeaderSize)); r.Add((Start + HeaderSize, HeaderSize)); }
        else r.Add((Start, HeaderSize));
        foreach (var s in Slots) r.Add((s.Offset, s.AreaLength));
        foreach (var h in Headers.Keys) if (!r.Any(x => h >= x.Item1 && h < x.Item1 + x.Item2)) r.Add((h, 4096));
        return Merge(r);
    }

    internal static List<(long, long)> Merge(IEnumerable<(long off, long len)> src)
    {
        var res = new List<(long, long)>();
        foreach (var (o, l) in src.Where(x => x.len > 0).OrderBy(x => x.off))
        {
            if (res.Count > 0 && o <= res[^1].Item1 + res[^1].Item2)
                res[^1] = (res[^1].Item1, Math.Max(res[^1].Item1 + res[^1].Item2, o + l) - res[^1].Item1);
            else res.Add((o, l));
        }
        return res;
    }
}

public sealed class LuksEraseResult
{
    public long Written;
    public List<string> Skipped { get; } = [];
    public LuksFinding? After;
}

/// <summary>
/// Acha LUKS no disco — inclusive o que sobrou depois de formatar por cima. O LUKS2 guarda o cabeçalho
/// duas vezes (a reserva logo depois da principal, a 16 KiB, 32 KiB… até 4 MiB) e as chaves vêm depois;
/// uma formatação rápida reescreve só o começo, então a reserva e as chaves sobrevivem e, com a senha,
/// o cryptsetup abre tudo de novo. Aqui se acha cada cópia pela assinatura, confere o checksum, mede se o
/// material de cada chave continua inteiro e diz onde ele está em relação às partições de hoje.
/// </summary>
public static class LuksFinder
{
    private static ReadOnlySpan<byte> Magic1 => [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE];
    private static ReadOnlySpan<byte> Magic2 => [(byte)'S', (byte)'K', (byte)'U', (byte)'L', 0xBA, 0xBE];
    /// <summary>Onde o LUKS2 põe a cópia reserva (= tamanho do cabeçalho, de 16 KiB a 4 MiB).</summary>
    public static readonly long[] SecondaryOffsets = [0x4000, 0x8000, 0x10000, 0x20000, 0x40000, 0x80000, 0x100000, 0x200000, 0x400000];

    /// <summary>O disco como está hoje, com o espaço livre entre as partições.</summary>
    public static List<LuksArea> Layout(DiskInfo d, long length)
    {
        var res = new List<LuksArea>();
        foreach (var p in d.Partitions.OrderBy(p => p.Offset))
            res.Add(new LuksArea(p.Offset, p.Length, p.IsUnallocated ? "espaço não alocado" : p.IsWholeDisk ? "disco" : p.DisplayName == "*" ? $"partição em {DiskOperations.Size(p.Offset)}" : $"partição {p.DisplayName}",
                p.FileSystem, !p.IsUnallocated, p.IsSystemCritical, p.IsWholeDisk));
        if (res.Count == 0) res.Add(new LuksArea(0, length, "disco", FsKind.Unknown, false, false));
        return res;
    }

    public static List<LuksFinding> Find(IBlockDevice dev, IReadOnlyList<LuksArea> layout, long start, long length, LuksScanMode mode,
                                         IProgress<LuksProgress>? progress, CancellationToken ct, Func<LuksArea, List<(long off, long len)>?>? usedRangesOf = null)
    {
        long end = Math.Min(dev.Length, start + length);
        var hits = new SortedSet<long>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (mode == LuksScanMode.Quick)
        {
            // onde o LUKS costuma estar: começo do trecho, de cada partição e de cada espaço livre
            var bases = new SortedSet<long> { start };
            foreach (var a in layout) if (a.Offset >= start && a.Offset < end) bases.Add(a.Offset);
            var buf = new byte[512];
            int i = 0;
            foreach (var b in bases)
            {
                foreach (var o in SecondaryOffsets.Prepend(0))
                {
                    ct.ThrowIfCancellationRequested();
                    if (b + o + 512 > end) break;
                    try { if (dev.ReadBytes(b + o, buf) == 512 && IsMagic(buf, 0)) hits.Add(b + o); }
                    catch (MediaReadException) { }
                }
                progress?.Report(new(++i, bases.Count, hits.Count, 0));
            }
        }
        else
        {
            int chunk = 4 << 20;
            var buf = new byte[chunk];
            long done = 0;
            DateTime last = default;
            for (long p = start; p < end; p += chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(chunk, end - p);
                int got;
                try { got = dev.Read(p, buf.AsSpan(0, n)); }
                catch (MediaReadException) { got = 0; } // setor ruim: segue adiante
                for (int o = 0; o + 8 <= got; o += 512)
                    if (buf[o + 4] == 0xBA && IsMagic(buf, o)) hits.Add(p + o);
                done += n;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 150 || p + chunk >= end)
                {
                    last = DateTime.UtcNow;
                    progress?.Report(new(done, end - start, hits.Count, done / Math.Max(0.001, sw.Elapsed.TotalSeconds)));
                }
            }
        }
        var res = Analyze(dev, layout, hits, ct, usedRangesOf);
        progress?.Report(new(1, 1, res.Count, 0));
        return res;
    }

    private static bool IsMagic(byte[] b, int o)
    {
        var s = b.AsSpan(o);
        if (!s.StartsWith(Magic1) && !s.StartsWith(Magic2)) return false;
        int v = U16BE(s, 6);
        return v is 1 or 2;
    }

    /// <summary>Lê e confere cada assinatura achada e junta as cópias do mesmo contêiner.</summary>
    /// <param name="usedRangesOf">mapa de uso de uma partição (relativo a ela): com ele, o que cai dentro de arquivo de hoje vira <see cref="LuksState.InFile"/>.</param>
    public static List<LuksFinding> Analyze(IBlockDevice dev, IReadOnlyList<LuksArea> layout, IEnumerable<long> hits, CancellationToken ct = default,
                                            Func<LuksArea, List<(long off, long len)>?>? usedRangesOf = null)
    {
        var found = new List<LuksFinding>();
        foreach (var off in hits)
        {
            ct.ThrowIfCancellationRequested();
            Parsed? h;
            try { h = Parse(dev, off); } catch (MediaReadException) { h = null; } catch (IOException) { h = null; }
            if (h == null) continue;
            var f = found.FirstOrDefault(x => x.Start == h.Start && x.Version == h.Version);
            if (f == null)
            {
                f = new LuksFinding { Start = h.Start, Version = h.Version, HeaderSize = h.HeaderSize, DataOffset = h.DataOffset };
                found.Add(f);
            }
            f.Headers[off] = h.Ok;
            // os dados do cabeçalho vêm da cópia íntegra (a principal, se as duas estiverem boas)
            if (h.Ok && (f.Slots.Count == 0 || off == f.Start))
            {
                f.Uuid = h.Uuid; f.Label = h.Label; f.Cipher = h.Cipher; f.HeaderSize = h.HeaderSize; f.DataOffset = h.DataOffset;
                f.Slots.Clear();
                f.Slots.AddRange(h.Slots);
            }
            else if (f.Uuid == null) { f.Uuid = h.Uuid; f.Label = h.Label; f.Cipher = h.Cipher; }
        }
        foreach (var f in found)
        {
            for (int i = 0; i < f.Slots.Count; i++)
            {
                var s = f.Slots[i];
                f.Slots[i] = s with { Sectors = 0, Damaged = 0 };
                try { f.Slots[i] = MeasureSlot(dev, s); } catch (MediaReadException) { f.Slots[i] = s with { Sectors = 1, Damaged = 1 }; }
            }
            f.Area = layout.FirstOrDefault(a => a.Contains(f.Start)) ?? layout.FirstOrDefault(a => a.Contains(f.Headers.Keys.First()));
            long extentEnd = f.Area?.End ?? dev.Length;
            f.DataRandom = f.DataOffset > f.Start && f.DataOffset < extentEnd ? SampleData(dev, f.DataOffset, extentEnd) : -1;
            bool live = f.Area is { IsPartition: true } a && a.Offset == f.Start && a.Fs == FsKind.Luks && f.PrimaryOk;
            f.State = live ? LuksState.Active : f.AnyHeaderOk && f.IntactSlots > 0 ? LuksState.Openable : LuksState.Dead;
        }
        // dentro de partição formatada, fora do começo: se o cabeçalho cai em área em uso, é um arquivo de hoje
        var maps = new Dictionary<LuksArea, List<(long off, long len)>?>();
        foreach (var f in found)
        {
            if (usedRangesOf == null || f.Area is not { IsPartition: true } a || a.Offset == f.Start || a.Fs is FsKind.Luks or FsKind.Unknown or FsKind.Empty) continue;
            if (!maps.TryGetValue(a, out var used)) maps[a] = used = usedRangesOf(a);
            if (used != null && f.Headers.Keys.Any(h => used.Any(u => h - a.Offset >= u.off && h - a.Offset < u.off + u.len))) f.State = LuksState.InFile;
        }
        // arquivo fragmentado: as cópias do cabeçalho caem em lugares que não batem; vira um achado só
        foreach (var g in found.Where(f => f.State == LuksState.InFile && f.Uuid is { Length: > 0 }).GroupBy(f => (f.Area, f.Uuid)).Where(g => g.Count() > 1).ToList())
        {
            var keep = g.OrderBy(f => f.Start).First();
            foreach (var o in g.Where(o => o != keep)) { foreach (var h in o.Headers) keep.Headers[h.Key] = h.Value; found.Remove(o); }
        }
        foreach (var f in found) f.Where = Describe(f);
        return found;
    }

    private static string Describe(LuksFinding f)
    {
        var a = f.Area;
        if (a == null) return $"a {DiskOperations.Size(f.Start)} do começo do disco";
        if (!a.IsPartition) return $"{a.Name}, a {DiskOperations.Size(f.Start)} do começo do disco";
        if (a.Offset == f.Start) return a.Fs == FsKind.Luks ? $"começo {a.Of}" : $"começo {a.Of}, que hoje é {FsDetect.Name(a.Fs)}";
        return $"dentro {a.Of} ({FsDetect.Name(a.Fs)}), a {DiskOperations.Size(f.Start - a.Offset)} do começo dela";
    }

    private sealed record Parsed(long Start, int Version, bool Ok, long HeaderSize, string? Uuid, string? Label, string? Cipher, long DataOffset, List<LuksSlot> Slots);

    private static Parsed? Parse(IBlockDevice dev, long off)
    {
        var b = dev.ReadBytes(off, 4096);
        if (b.Length < 1024) return null;
        bool primary = b.AsSpan().StartsWith(Magic1), secondary = b.AsSpan().StartsWith(Magic2);
        int ver = U16BE(b, 6);
        if (ver == 1 && primary) return ParseV1(off, b);
        if (ver != 2 || !(primary || secondary)) return null;

        long hdrSize = (long)U64BE(b, 8), hdrOffset = (long)U64BE(b, 256);
        if (hdrSize < 0x4000 || hdrSize > 0x400000 || (hdrSize & (hdrSize - 1)) != 0) return null;
        if (primary ? hdrOffset != 0 : hdrOffset != hdrSize) return null;
        long start = off - hdrOffset;
        if (start < 0) return null;
        string uuid = Ascii(b, 168, 40), label = Ascii(b, 24, 48), alg = Ascii(b, 72, 32);

        var full = dev.ReadBytes(off, (int)hdrSize);
        bool ok = full.Length == hdrSize && ChecksumOk(full, alg);
        string? cipher = null;
        long data = 0;
        var slots = new List<LuksSlot>();
        if (full.Length == hdrSize)
        {
            int jl = Array.IndexOf(full, (byte)0, 4096);
            if (jl < 0) jl = full.Length;
            try
            {
                using var j = JsonDocument.Parse(full.AsMemory(4096, jl - 4096));
                var root = j.RootElement;
                if (root.TryGetProperty("segments", out var segs))
                    foreach (var s in segs.EnumerateObject())
                    {
                        if (s.Value.TryGetProperty("encryption", out var enc)) cipher = enc.GetString();
                        if (s.Value.TryGetProperty("offset", out var so) && long.TryParse(so.GetString(), out long sol)) data = start + sol;
                        break;
                    }
                if (root.TryGetProperty("keyslots", out var ks))
                    foreach (var k in ks.EnumerateObject())
                    {
                        if (!int.TryParse(k.Name, out int idx) || !k.Value.TryGetProperty("area", out var area)) continue;
                        if (!long.TryParse(area.GetProperty("offset").GetString(), out long ao) || !long.TryParse(area.GetProperty("size").GetString(), out long asz)) continue;
                        long mat = asz;
                        if (k.Value.TryGetProperty("key_size", out var ksz) && k.Value.TryGetProperty("af", out var af) && af.TryGetProperty("stripes", out var st))
                            mat = Math.Min(asz, (long)ksz.GetInt32() * st.GetInt32());
                        slots.Add(new LuksSlot(idx, start + ao, asz, mat, 0, 0));
                    }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { ok = false; }
        }
        return new Parsed(start, 2, ok, hdrSize, uuid, label.Length > 0 ? label : null, cipher, data, slots);
    }

    private static Parsed? ParseV1(long off, byte[] b)
    {
        string cipher = Ascii(b, 8, 32), mode = Ascii(b, 40, 32), uuid = Ascii(b, 168, 40);
        long payload = (long)U32BE(b, 104) * 512;
        int keyBytes = (int)U32BE(b, 108);
        bool ok = keyBytes is 16 or 24 or 32 or 48 or 64 && cipher.Length > 0 && cipher.All(char.IsAsciiLetterOrDigit) && uuid.Length == 36;
        var slots = new List<LuksSlot>();
        long first = 4096;
        for (int i = 0; i < 8; i++)
        {
            int o = 208 + 48 * i;
            bool active = U32BE(b, o) == 0x00AC71F3;
            long km = (long)U32BE(b, o + 40) * 512, stripes = U32BE(b, o + 44);
            if (km <= 0 || stripes is <= 0 or > 100_000) continue;
            first = Math.Min(first, km);
            long mat = keyBytes * stripes;
            // só as chaves ligadas guardam material; as desligadas o cryptsetup já zerou
            if (active) slots.Add(new LuksSlot(i, off + km, (mat + 4095) / 4096 * 4096, mat, 0, 0));
        }
        return new Parsed(off, 1, ok, Math.Max(1024, first), uuid, null, ok ? $"{cipher}-{mode}" : null, off + payload, slots);
    }

    private static bool ChecksumOk(byte[] full, string alg)
    {
        HashAlgorithmName? h = alg switch { "sha256" => HashAlgorithmName.SHA256, "sha512" => HashAlgorithmName.SHA512, "sha1" => HashAlgorithmName.SHA1, _ => null };
        if (h == null) return false;
        var copy = (byte[])full.Clone();
        int n = h == HashAlgorithmName.SHA256 ? 32 : h == HashAlgorithmName.SHA512 ? 64 : 20;
        var want = full.AsSpan(448, n).ToArray();
        copy.AsSpan(448, 64).Clear();
        using var ih = IncrementalHash.CreateHash(h.Value);
        ih.AppendData(copy);
        return ih.GetHashAndReset().AsSpan().SequenceEqual(want);
    }

    /// <summary>Material da chave é aleatório: setor zerado, repetido ou com cara de texto quer dizer que foi sobrescrito.</summary>
    private static LuksSlot MeasureSlot(IBlockDevice dev, LuksSlot s)
    {
        long len = Math.Min(s.MaterialLength, 8L << 20) / 512 * 512;
        int sectors = 0, damaged = 0;
        var buf = new byte[64 * 1024];
        for (long p = 0; p < len; p += buf.Length)
        {
            int n = (int)Math.Min(buf.Length, len - p);
            int got = dev.ReadBytes(s.Offset + p, buf.AsSpan(0, n));
            for (int o = 0; o + 512 <= n; o += 512)
            {
                sectors++;
                if (o + 512 > got || !LooksRandom(buf.AsSpan(o, 512))) damaged++;
            }
        }
        return s with { Sectors = sectors, Damaged = damaged };
    }

    private static double SampleData(IBlockDevice dev, long from, long to)
    {
        const int samples = 24;
        long span = to - from;
        if (span < 4096) return -1;
        var buf = new byte[4096];
        int total = 0, rnd = 0;
        for (int i = 0; i < samples; i++)
        {
            long p = from + span / samples * i / 4096 * 4096;
            int got;
            try { got = dev.ReadBytes(p, buf); } catch (MediaReadException) { continue; }
            for (int o = 0; o + 512 <= got; o += 512) { total++; if (LooksRandom(buf.AsSpan(o, 512))) rnd++; }
        }
        return total == 0 ? -1 : (double)rnd / total;
    }

    /// <summary>512 bytes aleatórios têm ~221 valores distintos (desvio ~5); texto, zeros e estruturas ficam muito abaixo.</summary>
    internal static bool LooksRandom(ReadOnlySpan<byte> s)
    {
        Span<bool> seen = stackalloc bool[256];
        int distinct = 0;
        foreach (var x in s) if (!seen[x]) { seen[x] = true; distinct++; }
        return distinct >= 185;
    }

    /// <summary>
    /// Apaga (zera) as cópias do cabeçalho e as áreas das chaves de um achado. Só escreve onde não estraga nada
    /// de hoje: espaço não alocado, partição sem sistema de arquivos, o próprio contêiner (quando é ele que se
    /// quer destruir) e, dentro de uma partição formatada, só nos clusters que ela marca como livres.
    /// </summary>
    public static LuksEraseResult Erase(IBlockDevice dev, IReadOnlyList<LuksArea> layout, LuksFinding f,
                                        Func<LuksArea, List<(long off, long len)>?> usedRangesOf, Action<string> log, CancellationToken ct)
    {
        var res = new LuksEraseResult();
        if (f.State == LuksState.InFile) { res.Skipped.Add("está dentro de um arquivo de hoje: apague o arquivo pelo sistema"); res.After = f; return res; }
        var plan = new List<(long, long)>();
        var used = new Dictionary<LuksArea, List<(long off, long len)>?>();
        foreach (var (ro, rl) in f.Regions())
        {
            long re = Math.Min(dev.Length, ro + rl);
            for (long p = ro; p < re;)
            {
                var a = layout.FirstOrDefault(x => x.Contains(p));
                long pe = a == null ? re : Math.Min(re, a.End);
                if (a == null || !a.IsPartition) plan.Add((p, pe - p));
                else if (a.Critical) res.Skipped.Add($"{DiskOperations.Size(pe - p)} {a.In}, que está em uso pelo sistema");
                // o próprio contêiner (ou o que sobrou dele no começo da partição) e partição vazia: pode
                else if ((a.Offset == f.Start && a.Fs is FsKind.Luks or FsKind.Unknown) || a.Fs == FsKind.Empty) plan.Add((p, pe - p));
                else if (a.Fs == FsKind.Unknown) res.Skipped.Add($"{DiskOperations.Size(pe - p)} dentro {a.Of}, com um conteúdo que o app não reconhece: nada foi tocado ali");
                else
                {
                    if (!used.TryGetValue(a, out var u)) { log($"Lendo o mapa de uso {a.Of} ({FsDetect.Name(a.Fs)})…"); used[a] = u = usedRangesOf(a); }
                    if (u == null) res.Skipped.Add($"{DiskOperations.Size(pe - p)} dentro {a.Of}: não deu para ler o mapa de uso de {FsDetect.Name(a.Fs)}, então nada foi tocado ali");
                    else
                    {
                        long busy = 0;
                        foreach (var (fo, fl) in Subtract((p, pe - p), u.Select(x => (x.off + a.Offset, x.len))))
                            plan.Add((fo, fl));
                        foreach (var x in u)
                        {
                            long s = Math.Max(p, x.off + a.Offset), e = Math.Min(pe, x.off + a.Offset + x.len);
                            if (e > s) busy += e - s;
                        }
                        if (busy > 0) res.Skipped.Add($"{DiskOperations.Size(busy)} dentro {a.Of} já pertencem a {FsDetect.Name(a.Fs)} (arquivos ou estruturas dele): ficaram como estão");
                    }
                }
                p = pe;
            }
        }
        var zeros = new byte[1 << 20];
        foreach (var (o, l) in LuksFinding.Merge(plan))
            for (long p = o; p < o + l; p += zeros.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(zeros.Length, o + l - p);
                dev.WriteBytes(p, zeros.AsSpan(0, n));
                res.Written += n;
            }
        dev.Flush();
        // confere o que sobrou nos mesmos lugares
        var again = Analyze(dev, layout, f.Headers.Keys.Concat([f.Start, f.Start + f.HeaderSize]).Distinct().Where(x => x >= 0 && x < dev.Length), ct);
        res.After = again.FirstOrDefault(x => x.Start == f.Start);
        if (res.After == null && f.Slots.Count > 0)
        {
            // sem cabeçalho: mede só as chaves antigas, para o relatório dizer se ficou material
            var probe = new LuksFinding { Start = f.Start, Version = f.Version, HeaderSize = f.HeaderSize };
            foreach (var s in f.Slots) probe.Slots.Add(MeasureSlot(dev, s));
            if (probe.IntactSlots > 0) { probe.State = LuksState.Dead; probe.Where = f.Where; res.After = probe; }
        }
        return res;
    }

    /// <summary>O que o sistema de arquivos da partição usa hoje (metadados e arquivos), sem juntar vizinhos: nulo = não deu para ler.</summary>
    public static List<(long off, long len)>? UsedRanges(IBlockDevice vol)
    {
        try
        {
            var map = Recovery.FileSystemScanner.Scan(vol, new Recovery.ScanOptions { IncludeLive = false, OrphanMftRecords = false }, null, CancellationToken.None).Allocation;
            return map?.UsedRanges(vol.Length).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private static IEnumerable<(long, long)> Subtract((long off, long len) r, IEnumerable<(long off, long len)> busy)
    {
        long p = r.off, end = r.off + r.len;
        foreach (var (bo, bl) in busy.Where(b => b.off < end && b.off + b.len > r.off).OrderBy(b => b.off))
        {
            if (bo > p) yield return (p, bo - p);
            p = Math.Max(p, bo + bl);
            if (p >= end) yield break;
        }
        if (p < end) yield return (p, end - p);
    }
}
