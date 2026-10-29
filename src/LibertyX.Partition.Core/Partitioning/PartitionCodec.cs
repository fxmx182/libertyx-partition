using System.Text;
using LibertyX.Partition.Core.IO;
using static LibertyX.Partition.Core.IO.Bin;

namespace LibertyX.Partition.Core.Partitioning;

/// <summary>Lê e escreve MBR (com lógicas) e GPT (com cópia de segurança e CRC), direto nos setores.</summary>
public static class PartitionCodec
{
    private static ReadOnlySpan<byte> GptSig => "EFI PART"u8;

    public static PartitionTable Read(IBlockDevice dev)
    {
        int ss = dev.SectorSize;
        long sectors = dev.Length / ss;
        var t = new PartitionTable { SectorSize = ss, DiskSectors = sectors, Style = TableStyle.None };
        if (sectors < 2) return t;

        var mbr = new byte[ss];
        dev.Read(0, mbr);
        bool hasMbrSig = mbr[510] == 0x55 && mbr[511] == 0xAA;
        bool protective = false, mbrHasParts = false;
        if (hasMbrSig)
            for (int i = 0; i < 4; i++)
            {
                byte ty = mbr[446 + i * 16 + 4];
                if (ty == 0xEE) protective = true;
                else if (ty != 0 && U32(mbr, 446 + i * 16 + 12) != 0) mbrHasParts = true;
            }

        // GPT vence quando existe: há discos com MBR "híbrido" ou sem a protetora.
        if (TryReadGpt(dev, 1, out var gpt, out var primaryOk) && primaryOk)
        {
            if (!protective && hasMbrSig) gpt!.Warnings.Add(L.T("GPT encontrada, mas o MBR não tem a entrada protetora (0xEE)."));
            CheckBackup(dev, gpt!);
            return gpt!;
        }
        // a cópia da GPT no fim só vale se o MBR não tem partições de verdade: um pendrive que já foi
        // GPT e virou MBR guarda a cópia velha lá no fim, e ela não pode vencer a tabela atual
        if (protective || (!mbrHasParts && TryReadGpt(dev, sectors - 1, out _, out _)))
        {
            // Primária danificada: tenta a reserva no fim do disco.
            if (TryReadGpt(dev, sectors - 1, out var backup, out var ok2) && ok2)
            {
                backup!.GptPrimaryDamaged = true;
                backup.Warnings.Add(L.T("Cabeçalho GPT primário danificado — usando a cópia de segurança do fim do disco."));
                return backup;
            }
            if (gpt != null) { gpt.Warnings.Add(L.T("Tabela GPT com CRC inválido.")); return gpt; }
        }
        if (!hasMbrSig) return t;

        t.Style = TableStyle.Mbr;
        t.BootCode = mbr[..440];
        t.DiskSignature = U32(mbr, 440);
        int slot = 1;
        PartitionEntry? ext = null;
        for (int i = 0; i < 4; i++)
        {
            var e = ParseMbrEntry(mbr, 446 + i * 16, 0);
            if (e == null) { slot++; continue; }
            e.Slot = slot++;
            if (PartitionTypes.IsExtended(e.MbrType)) { ext ??= e; continue; }
            t.Entries.Add(e);
        }
        if (ext != null) ReadLogicals(dev, t, ext);
        if (Overlaps(t)) t.Warnings.Add(L.T("Há partições sobrepostas na tabela MBR."));
        return t;
    }

    private static PartitionEntry? ParseMbrEntry(ReadOnlySpan<byte> s, int o, long baseLba)
    {
        byte type = s[o + 4];
        uint start = U32(s, o + 8), count = U32(s, o + 12);
        if (type == 0 || count == 0) return null;
        return new PartitionEntry
        {
            Active = s[o] == 0x80, MbrType = type, StartLba = baseLba + start, SectorCount = count,
        };
    }

    private static void ReadLogicals(IBlockDevice dev, PartitionTable t, PartitionEntry ext)
    {
        int ss = dev.SectorSize;
        var sec = new byte[ss];
        long next = ext.StartLba;
        var seen = new HashSet<long>();
        int slot = 5;
        while (seen.Add(next) && seen.Count < 256 && next < t.DiskSectors)
        {
            dev.Read(next * ss, sec);
            if (sec[510] != 0x55 || sec[511] != 0xAA) { t.Warnings.Add(L.T($"EBR inválido no setor {next}.")); break; }
            var logical = ParseMbrEntry(sec, 446, next);
            if (logical != null)
            {
                logical.IsLogical = true;
                logical.Slot = slot++;
                logical.EbrLba = next;
                t.Entries.Add(logical);
            }
            var link = ParseMbrEntry(sec, 446 + 16, ext.StartLba);
            if (link == null || !PartitionTypes.IsExtended(link.MbrType)) break;
            next = link.StartLba;
        }
    }

    private static bool TryReadGpt(IBlockDevice dev, long headerLba, out PartitionTable? t, out bool valid)
    {
        t = null; valid = false;
        int ss = dev.SectorSize;
        long sectors = dev.Length / ss;
        if (headerLba <= 0 || headerLba >= sectors) return false;
        var h = new byte[ss];
        dev.Read(headerLba * ss, h);
        if (!Match(h, 0, GptSig)) return false;
        uint hsize = U32(h, 12);
        if (hsize < 92 || hsize > ss) return false;
        uint crc = U32(h, 16);
        var hc = h[..(int)hsize];
        W32(hc, 16, 0);
        bool headerOk = Crc32.Compute(hc) == crc;

        long entriesLba = (long)U64(h, 72);
        uint count = U32(h, 80), esize = U32(h, 84);
        if (esize < 128 || esize > 4096 || count == 0 || count > 16384) return false;
        long bytes = (long)count * esize;
        if (entriesLba <= 0 || entriesLba * ss + bytes > dev.Length) return false;
        var arr = new byte[(bytes + ss - 1) / ss * ss];
        dev.Read(entriesLba * ss, arr);
        bool arrOk = Crc32.Compute(arr.AsSpan(0, (int)bytes)) == U32(h, 88);

        t = new PartitionTable
        {
            Style = TableStyle.Gpt, SectorSize = ss, DiskSectors = sectors,
            FirstUsableLba = (long)U64(h, 40), LastUsableLba = (long)U64(h, 48),
            DiskGuid = new Guid(h.AsSpan(56, 16)), GptEntryCount = (int)count,
        };
        if (!arrOk) t.Warnings.Add(L.T("CRC da lista de partições GPT não confere."));
        for (int i = 0; i < count; i++)
        {
            var e = arr.AsSpan((int)(i * esize), (int)esize);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty) continue;
            long first = (long)U64(e, 32), last = (long)U64(e, 40);
            if (last < first) continue;
            t.Entries.Add(new PartitionEntry
            {
                TypeGuid = type, UniqueGuid = new Guid(e.Slice(16, 16)), StartLba = first, SectorCount = last - first + 1,
                Attributes = U64(e, 48), Name = Utf16(e, 56, 36), Slot = i + 1,
            });
        }
        if (t.LastUsableLba >= sectors || t.FirstUsableLba < 2)
        {
            t.Warnings.Add(L.T("Limites do GPT não batem com o tamanho do disco (disco clonado de outro tamanho?)."));
            t.LastUsableLba = Math.Min(t.LastUsableLba, sectors - 34);
        }
        valid = headerOk && arrOk;
        return true;
    }

    private static void CheckBackup(IBlockDevice dev, PartitionTable t)
    {
        long last = dev.Length / dev.SectorSize - 1;
        if (!TryReadGpt(dev, last, out _, out var ok) || !ok)
        {
            t.GptBackupDamaged = true;
            t.Warnings.Add(L.T("A cópia de segurança do GPT (fim do disco) está ausente ou danificada."));
        }
    }

    private static bool Overlaps(PartitionTable t)
    {
        var o = t.Ordered.ToList();
        for (int i = 1; i < o.Count; i++) if (o[i].StartLba <= o[i - 1].EndLba) return true;
        return false;
    }

    // ───────────────────────────── escrita ─────────────────────────────

    /// <summary>Valida a tabela antes de escrever: limites, sobreposição, limite de 2 TiB no MBR.</summary>
    public static List<string> Validate(PartitionTable t)
    {
        var err = new List<string>();
        var o = t.Ordered.ToList();
        long lo = t.Style == TableStyle.Gpt ? GptFirstUsable(t) : 1;
        long hi = t.Style == TableStyle.Gpt ? GptLastUsable(t) : t.DiskSectors - 1;
        for (int i = 0; i < o.Count; i++)
        {
            var e = o[i];
            if (e.SectorCount <= 0) err.Add(L.T($"Partição {e.Slot}: tamanho zero."));
            if (e.StartLba < lo || e.EndLba > hi) err.Add(L.T($"Partição {e.Slot}: fora da área utilizável ({lo}–{hi})."));
            if (i > 0 && e.StartLba <= o[i - 1].EndLba) err.Add(L.T($"Duas partições se sobrepõem (setores {o[i - 1].StartLba:N0}–{o[i - 1].EndLba:N0} e {e.StartLba:N0}–{e.EndLba:N0}). Desmarque uma delas."));
            if (t.Style == TableStyle.Mbr && (e.EndLba > uint.MaxValue)) err.Add(L.T($"Partição {e.Slot}: além de 2 TiB, impossível em MBR."));
        }
        if (t.Style == TableStyle.Mbr)
        {
            int primaries = o.Count(e => !e.IsLogical) + (o.Any(e => e.IsLogical) ? 1 : 0);
            if (primaries > 4) err.Add(L.T("MBR comporta no máximo 4 partições primárias (ou 3 + estendida)."));
        }
        if (t.Style == TableStyle.Gpt && o.Count > t.GptEntryCount) err.Add(L.T("Partições demais para a tabela GPT."));
        return err;
    }

    public static int GptEntrySectors(PartitionTable t) => (Math.Max(128, t.GptEntryCount) * 128 + t.SectorSize - 1) / t.SectorSize;
    public static long GptFirstUsable(PartitionTable t) => 2 + GptEntrySectors(t);
    public static long GptLastUsable(PartitionTable t) => t.DiskSectors - 2 - GptEntrySectors(t);

    /// <summary>Monta os setores a escrever: (lba, bytes). Separado de <see cref="Write"/> para dar para salvar backup e testar.</summary>
    public static List<(long lba, byte[] data)> Build(PartitionTable t)
    {
        var errs = Validate(t);
        if (errs.Count > 0) throw new InvalidOperationException(string.Join("\n", errs));
        return t.Style switch
        {
            TableStyle.Gpt => BuildGpt(t),
            TableStyle.Mbr => BuildMbr(t),
            _ => throw new InvalidOperationException(L.T("estilo de tabela indefinido")),
        };
    }

    public static void Write(IBlockDevice dev, PartitionTable t)
    {
        foreach (var (lba, data) in Build(t)) dev.Write(lba * dev.SectorSize, data);
        dev.Flush();
    }

    private static List<(long, byte[])> BuildGpt(PartitionTable t)
    {
        int ss = t.SectorSize;
        int count = Math.Max(128, t.GptEntryCount);
        int entSectors = GptEntrySectors(t);
        long last = t.DiskSectors - 1;
        t.FirstUsableLba = 2 + entSectors;
        t.LastUsableLba = last - entSectors - 1;
        if (t.DiskGuid == Guid.Empty) t.DiskGuid = Guid.NewGuid();

        var arr = new byte[entSectors * ss];
        // cada partição fica na posição em que já estava (no Linux, a posição é o número: /dev/sda3);
        // compactar renumeraria as vizinhas de uma excluída e quebraria montagens e o fstab
        var taken = new HashSet<int>();
        var placed = new List<(PartitionEntry e, int idx)>();
        foreach (var e in t.Entries.Where(x => x.Slot >= 1 && x.Slot <= count).OrderBy(x => x.Slot))
            if (taken.Add(e.Slot - 1)) placed.Add((e, e.Slot - 1));
        int nextFree = 0;
        foreach (var e in t.Entries.Where(x => !placed.Any(p => p.e == x)).OrderBy(x => x.StartLba))
        {
            while (taken.Contains(nextFree)) nextFree++;
            taken.Add(nextFree);
            placed.Add((e, nextFree));
        }
        foreach (var (e, i) in placed)
        {
            if (e.UniqueGuid == Guid.Empty) e.UniqueGuid = Guid.NewGuid();
            var s = arr.AsSpan(i * 128, 128);
            e.TypeGuid.TryWriteBytes(s[..16]);
            e.UniqueGuid.TryWriteBytes(s.Slice(16, 16));
            W64(s, 32, (ulong)e.StartLba);
            W64(s, 40, (ulong)e.EndLba);
            W64(s, 48, e.Attributes);
            var name = Encoding.Unicode.GetBytes(e.Name.Length > 36 ? e.Name[..36] : e.Name);
            name.CopyTo(s[56..]);
            e.Slot = i + 1;
        }
        uint arrCrc = Crc32.Compute(arr.AsSpan(0, count * 128));

        byte[] Header(long my, long alt, long entriesLba)
        {
            var h = new byte[ss];
            GptSig.CopyTo(h);
            W32(h, 8, 0x00010000);
            W32(h, 12, 92);
            W64(h, 24, (ulong)my);
            W64(h, 32, (ulong)alt);
            W64(h, 40, (ulong)t.FirstUsableLba);
            W64(h, 48, (ulong)t.LastUsableLba);
            t.DiskGuid.TryWriteBytes(h.AsSpan(56, 16));
            W64(h, 72, (ulong)entriesLba);
            W32(h, 80, (uint)count);
            W32(h, 84, 128);
            W32(h, 88, arrCrc);
            W32(h, 16, Crc32.Compute(h.AsSpan(0, 92)));
            return h;
        }

        var pmbr = new byte[ss];
        if (t.BootCode != null) t.BootCode.AsSpan(0, Math.Min(440, t.BootCode.Length)).CopyTo(pmbr);
        W32(pmbr, 440, t.DiskSignature);
        pmbr[446 + 1] = 0x00; pmbr[446 + 2] = 0x02; pmbr[446 + 3] = 0x00;
        pmbr[446 + 4] = 0xEE;
        pmbr[446 + 5] = 0xFF; pmbr[446 + 6] = 0xFF; pmbr[446 + 7] = 0xFF;
        W32(pmbr, 446 + 8, 1);
        W32(pmbr, 446 + 12, (uint)Math.Min(t.DiskSectors - 1, uint.MaxValue));
        pmbr[510] = 0x55; pmbr[511] = 0xAA;

        return
        [
            (0, pmbr),
            (1, Header(1, last, 2)),
            (2, arr),
            (last - entSectors, arr),
            (last, Header(last, 1, last - entSectors)),
        ];
    }

    private static void Chs(Span<byte> d, long lba)
    {
        if (lba >= 1024L * 255 * 63) { d[0] = 0xFE; d[1] = 0xFF; d[2] = 0xFF; return; }
        long c = lba / (255 * 63), h = lba / 63 % 255, s = lba % 63 + 1;
        d[0] = (byte)h; d[1] = (byte)(s | ((c >> 2) & 0xC0)); d[2] = (byte)(c & 0xFF);
    }

    private static void PutMbrEntry(Span<byte> sec, int o, byte type, long start, long count, bool active)
    {
        sec[o] = active ? (byte)0x80 : (byte)0;
        Chs(sec.Slice(o + 1, 3), start);
        sec[o + 4] = type;
        Chs(sec.Slice(o + 5, 3), start + count - 1);
        W32(sec, o + 8, (uint)start);
        W32(sec, o + 12, (uint)count);
    }

    private static List<(long, byte[])> BuildMbr(PartitionTable t)
    {
        int ss = t.SectorSize;
        var res = new List<(long, byte[])>();
        var mbr = new byte[ss];
        if (t.BootCode != null) t.BootCode.AsSpan(0, Math.Min(440, t.BootCode.Length)).CopyTo(mbr);
        if (t.DiskSignature == 0) t.DiskSignature = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        W32(mbr, 440, t.DiskSignature);
        mbr[510] = 0x55; mbr[511] = 0xAA;

        var prim = t.Entries.Where(e => !e.IsLogical).OrderBy(e => e.StartLba).ToList();
        var logi = t.Entries.Where(e => e.IsLogical).OrderBy(e => e.StartLba).ToList();
        // primárias mantêm a posição (1–4) que tinham; as novas e a estendida ocupam as livres
        var taken = new bool[4];
        var slotOf = new Dictionary<PartitionEntry, int>();
        foreach (var e in prim.Where(e => e.Slot is >= 1 and <= 4).OrderBy(e => e.Slot))
            if (!taken[e.Slot - 1]) { taken[e.Slot - 1] = true; slotOf[e] = e.Slot - 1; }
        int FreeSlot()
        {
            for (int k = 0; k < 4; k++) if (!taken[k]) { taken[k] = true; return k; }
            throw new InvalidOperationException(L.T("MBR comporta só 4 entradas primárias (contando a estendida)."));
        }
        foreach (var e in prim.Where(e => !slotOf.ContainsKey(e))) slotOf[e] = FreeSlot();
        foreach (var e in prim) { PutMbrEntry(mbr, 446 + slotOf[e] * 16, e.MbrType, e.StartLba, e.SectorCount, e.Active); e.Slot = slotOf[e] + 1; }

        if (logi.Count > 0)
        {
            // Cada lógica precisa de um EBR antes dela; reaproveita a posição antiga quando há.
            long prevEnd = prim.Where(p => p.EndLba < logi[0].StartLba).Select(p => p.EndLba).DefaultIfEmpty(0).Max();
            foreach (var l in logi)
            {
                long ebr = l.EbrLba > prevEnd && l.EbrLba < l.StartLba ? l.EbrLba
                         : Math.Max(prevEnd + 1, l.StartLba - t.AlignSectors);
                if (ebr >= l.StartLba) throw new InvalidOperationException(L.T($"Sem espaço para o EBR antes da partição lógica em {l.StartLba}."));
                l.EbrLba = ebr;
                prevEnd = l.EndLba;
            }
            long extStart = logi[0].EbrLba, extEnd = logi[^1].EndLba;
            PutMbrEntry(mbr, 446 + FreeSlot() * 16, 0x0F, extStart, extEnd - extStart + 1, false);
            int ls = 5;
            for (int i = 0; i < logi.Count; i++)
            {
                var l = logi[i];
                var ebr = new byte[ss];
                ebr[510] = 0x55; ebr[511] = 0xAA;
                PutMbrEntry(ebr, 446, l.MbrType, l.StartLba - l.EbrLba, l.SectorCount, l.Active);
                if (i + 1 < logi.Count)
                {
                    var n = logi[i + 1];
                    PutMbrEntry(ebr, 446 + 16, 0x05, n.EbrLba - extStart, n.EndLba - n.EbrLba + 1, false);
                }
                l.Slot = ls++;
                res.Add((l.EbrLba, ebr));
            }
        }
        res.Insert(0, (0, mbr));
        // Restos de GPT confundem o Windows e outros programas: apaga os cabeçalhos, se nenhuma partição os cobre.
        long last = t.DiskSectors - 1;
        if (!t.Entries.Any(e => e.StartLba <= 1)) res.Add((1, new byte[ss]));
        if (!t.Entries.Any(e => e.EndLba >= last)) res.Add((last, new byte[ss]));
        return res;
    }

    /// <summary>Setores que uma escrita de tabela vai tocar — é isso que se salva antes, para dar para desfazer.</summary>
    public static List<(long lba, int count)> FootprintForBackup(IBlockDevice dev, PartitionTable current)
    {
        long last = dev.Length / dev.SectorSize - 1;
        var r = new List<(long, int)> { (0, 34) , (Math.Max(34, last - 32), (int)Math.Min(33, last + 1)) };
        foreach (var e in current.Entries.Where(e => e.IsLogical)) r.Add((e.EbrLba, 1));
        return r;
    }
}
