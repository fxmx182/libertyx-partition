using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LibertyX.Partition.Core.Operations;
using static LibertyX.Partition.Core.Platform.Linux.LinuxNative;

namespace LibertyX.Partition.Core.Platform.Linux;

/// <summary>
/// Apagar SSD do jeito certo. Sobrescrever não alcança as células de reserva do SSD; o próprio
/// controlador sabe apagar tudo: TRIM em todos os blocos (segundos, e o SSD volta ao desempenho de
/// novo) ou o apagamento seguro do fabricante — NVMe Format com "User Data Erase", ATA SECURITY
/// ERASE UNIT no SATA, descarte seguro no eMMC.
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxErase
{
    public static string? CannotSanitize(DiskInfo d)
    {
        if (d.IsSystem) return L.T("Disco do sistema em uso.");
        if (d.IsImage || d.IsVirtual) return L.T("Só discos físicos.");
        if (d.Bus == "USB") return L.T("Pelo USB o apagamento do fabricante pode travar o disco: ligue-o direto na placa (SATA/NVMe), ou use TRIM.");
        if (d.Bus is not ("NVMe" or "SATA" or "MMC" or "SD")) return L.T("O apagamento do fabricante vale para SSD NVMe, SATA e eMMC.");
        if (d.Bus == "SATA" && !LinuxTools.Has("hdparm")) return LinuxTools.Missing("hdparm");
        return null;
    }

    /// <summary>TRIM de uma faixa inteira, em pedaços (com progresso e cancelamento).</summary>
    public static void Discard(DiskInfo d, PartitionInfo? p, OpSink s)
    {
        using var dev = new LinuxDevice(p?.DeviceNode ?? d.DevicePath, true, d.SectorSize);
        long total = dev.Length, done = 0;
        const long chunk = 1L << 30;
        while (done < total)
        {
            s.Token.ThrowIfCancellationRequested();
            long n = Math.Min(chunk, total - done);
            if (!dev.Discard(done, n))
            {
                int e = Errno;
                throw new InvalidOperationException(e == EOPNOTSUPP || e == ENOTTY || e == EINVAL
                    ? L.T("Este disco não aceita TRIM (HD mecânico, adaptador USB sem suporte ou cartão antigo). Use “Preencher com zeros”.")
                    : L.T($"O TRIM falhou: {ErrText(e)}."));
            }
            done += n;
            s.Progress((double)done / total, L.T("Descartando blocos (TRIM)"), $"{DiskOperations.Size(done)} de {DiskOperations.Size(total)}");
        }
        dev.Flush();
        s.Log(L.T("Todos os blocos foram descartados. O SSD devolve zeros nessas posições e reaproveita as células."));
    }

    private const int EOPNOTSUPP = 95;

    public static async Task Sanitize(DiskInfo d, OpSink s)
    {
        if (CannotSanitize(d) is { } why) throw new InvalidOperationException(why);
        s.Progress(-1, L.T("Apagamento seguro do fabricante"));
        if (d.Bus == "NVMe")
        {
            s.Log(L.T("NVMe: Format NVM com “User Data Erase” (o controlador apaga todas as células, inclusive as de reserva)…"));
            if (LinuxTools.Has("nvme"))
            {
                var r = await Proc.Run("nvme", ["format", d.DevicePath, "--ses=1", "--force", "--timeout=600000"], s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); });
                if (!r.Ok) throw new InvalidOperationException(L.T("O SSD recusou o apagamento: ") + r.Message);
            }
            else NvmeFormatNative(d.DevicePath);
        }
        else if (d.Bus == "SATA")
        {
            var id = await Proc.Run("hdparm", ["-I", d.DevicePath], s.Token);
            if (!id.Output.Contains("Security:")) throw new InvalidOperationException(L.T("Este disco não tem o conjunto de segurança ATA (sem SECURITY ERASE)."));
            if (id.Output.Contains("\tfrozen") && !id.Output.Contains("not\tfrozen"))
                throw new InvalidOperationException(L.T("A BIOS congelou a segurança do SSD (“frozen”). Suspenda o computador e acorde (o SATA volta destravado) e tente de novo — ou use TRIM."));
            s.Log(L.T("SATA: ATA SECURITY ERASE UNIT (senha temporária, apagada pelo próprio comando)…"));
            var a = await Proc.Run("hdparm", ["--user-master", "u", "--security-set-pass", "LibertyX", d.DevicePath], s.Token);
            if (!a.Ok) throw new InvalidOperationException(L.T("Não foi possível preparar o apagamento: ") + a.Message);
            bool enhanced = id.Output.Contains("supported: enhanced erase");
            var b = await Proc.Run("hdparm", ["--user-master", "u", enhanced ? "--security-erase-enhanced" : "--security-erase", "LibertyX", d.DevicePath], s.Token, l => { if (l.Trim().Length > 0) s.Log(l.Trim()); }, timeout: TimeSpan.FromHours(6));
            if (!b.Ok)
            {
                await Proc.Run("hdparm", ["--user-master", "u", "--security-disable", "LibertyX", d.DevicePath]);
                throw new InvalidOperationException(L.T("O apagamento não terminou: ") + b.Message);
            }
        }
        else
        {
            using var dev = new LinuxDevice(d.DevicePath, true, d.SectorSize);
            if (!dev.Discard(0, dev.Length, secure: true)) throw new InvalidOperationException(L.T("Este cartão/eMMC não aceita descarte seguro: ") + ErrText(Errno));
        }
        DiskSystem.Native.Rescan(d);
        s.Log(L.T("Apagamento seguro concluído. O disco aparece agora sem tabela de partições."));
    }

    /// <summary>Format NVM direto pelo ioctl, mantendo o formato de LBA atual (Identify Namespace → FLBAS).</summary>
    private static unsafe void NvmeFormatNative(string dev)
    {
        int fd = Open(dev, O_RDWR | O_CLOEXEC);
        if (fd < 0) throw new IOException(L.T($"Não foi possível abrir {dev}: {ErrText(Errno)}"));
        byte* buf = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            new Span<byte>(buf, 4096).Clear();
            var idc = new NvmeAdminCmd { Opcode = 0x06, Nsid = 1, Addr = (ulong)buf, DataLen = 4096, Cdw10 = 0, TimeoutMs = 10000 };
            if (Ioctl(fd, NVME_IOCTL_ADMIN_CMD, &idc) != 0) throw new IOException(L.T("O SSD não respondeu à identificação: ") + ErrText(Errno));
            uint lbaf = (uint)(buf[26] & 0x0F);
            var fmt = new NvmeAdminCmd { Opcode = 0x80, Nsid = 1, Cdw10 = lbaf | (1u << 9), TimeoutMs = 600000 };
            if (Ioctl(fd, NVME_IOCTL_ADMIN_CMD, &fmt) != 0) throw new IOException(L.T("O SSD recusou o Format NVM: ") + ErrText(Errno));
        }
        finally { NativeMemory.AlignedFree(buf); Close(fd); }
    }
}
