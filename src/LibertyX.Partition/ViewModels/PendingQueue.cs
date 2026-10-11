using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.Operations;
using LibertyX.Partition.Core.Platform;

namespace LibertyX.Partition.ViewModels;

/// <summary>Uma alteração escolhida mas ainda não feita. Só vai para o disco no "Confirmar".</summary>
public sealed class PendingOp
{
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string IconKey { get; init; } = "Edit";
    public Geometry? Icon => Ui.Icon(IconKey);
    public bool Dangerous { get; init; }
    public bool CanPause { get; init; }
    /// <summary>Disco afetado (para o aviso no cartão do disco).</summary>
    public int? DiskNumber { get; init; }
    public required Func<OpSink, Task> Work { get; init; }
    public IRelayCommand Remove => new RelayCommand(() => MainViewModel.Instance.Pending.Remove(this));
}

public sealed partial class MainViewModel
{
    /// <summary>A fila de alterações — o mesmo modelo do MiniTool: escolhe, revisa, confirma.</summary>
    public ObservableCollection<PendingOp> Pending { get; } = [];
    public bool HasPending => Pending.Count > 0;
    public string PendingTitle => Pending.Count == 0 ? "ALTERAÇÕES PENDENTES" : $"{Pending.Count} PENDENTE{(Pending.Count == 1 ? "" : "S")}";

    private void HookPending()
    {
        Pending.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(PendingTitle));
            foreach (var p in _pages.Values) p.OnPendingChanged();
        };
    }

    /// <summary>Põe uma alteração na fila (nada é gravado ainda).</summary>
    public void Queue(string title, string detail, string icon, Func<OpSink, Task> work, DiskInfo? disk = null, bool dangerous = false, bool canPause = false)
    {
        Pending.Add(new PendingOp { Title = title, Detail = detail, IconKey = icon, Work = work, DiskNumber = disk?.Number, Dangerous = dangerous, CanPause = canPause });
        ShowToast("Adicionado às alterações pendentes — confira e clique em Confirmar, na barra à esquerda.");
    }

    public void Queue(string title, string detail, string icon, Action<OpSink> work, DiskInfo? disk = null, bool dangerous = false, bool canPause = false)
        => Queue(title, detail, icon, s => { work(s); return Task.CompletedTask; }, disk, dangerous, canPause);

    [RelayCommand]
    private async Task DiscardPending()
    {
        if (Pending.Count == 0) return;
        if (!await Confirm("Cancelar alterações", $"As {Pending.Count} alterações pendentes serão descartadas. Nada foi gravado no disco.", "Descartar", false, null, "Close")) return;
        Pending.Clear();
        ShowToast("Alterações descartadas. Nenhum disco foi tocado.");
    }

    [RelayCommand]
    private async Task ApplyPending()
    {
        if (Pending.Count == 0) return;
        var ops = Pending.ToList();
        var f = new FormDialogViewModel
        {
            Title = "Confirmar alterações", IconKey = "Check", Subtitle = $"{ops.Count} operação(ões), executadas nesta ordem",
            ConfirmText = "Aplicar agora", Danger = ops.Any(o => o.Dangerous),
        };
        int i = 1;
        foreach (var o in ops) f.Add(new InfoField { Label = $"{i++}. {o.Title}", Text = o.Detail });
        f.Note("Feche os programas que usam esses discos. Não desligue o computador durante a execução. A tabela de cada disco é salva antes em Documentos\\LibertyX Partition.", ops.Any(o => o.Dangerous) ? "danger" : "gold");
        if (ops.Any(o => o.Dangerous)) f.ConfirmPhrase = "CONFIRMAR";
        if (!await ShowDialog(f)) return;

        await Run(ops.Count == 1 ? ops[0].Title : $"Aplicando {ops.Count} alterações", async s =>
        {
            for (int k = 0; k < ops.Count; k++)
            {
                var o = ops[k];
                s.Log($"── {k + 1}/{ops.Count} · {o.Title}");
                s.Progress(-1, o.Title);
                await o.Work(s);
                Ui.Post(() => Pending.Remove(o));
                s.Log("   feito.");
            }
        }, refresh: true, canPause: ops.Any(o => o.CanPause), success: ops.Count == 1 ? null : $"As {ops.Count} alterações foram aplicadas.");
        if (Pending.Count > 0)
            ShowToast($"{Pending.Count} alteração(ões) não chegaram a rodar e continuam pendentes.");
    }

    // ───────── estado atualizado na hora de executar ─────────

    /// <summary>O disco como está agora (uma alteração anterior da fila pode ter mudado a tabela).</summary>
    public static DiskInfo Fresh(DiskInfo d)
    {
        var nd = DiskSystem.Enumerate().FirstOrDefault(x => x.Number == d.Number && x.IsImage == d.IsImage && (!d.IsImage || x.ImagePath == d.ImagePath));
        if (nd == null) throw new InvalidOperationException($"{d.Title} ({d.Model}) não está mais conectado.");
        if (d.IsImage) nd.ReadOnly = d.ReadOnly;
        return nd;
    }

    /// <summary>A partição como está agora: pelo GUID (sobrevive a mover), senão pelo início.</summary>
    public static PartitionInfo FreshPart(PartitionInfo p)
    {
        var nd = Fresh(p.Disk);
        PartitionInfo? np;
        if (p.IsUnallocated)
            np = nd.Partitions.FirstOrDefault(x => x.IsUnallocated && x.Offset <= p.Offset && p.Offset < x.Offset + x.Length);
        else if (p.Entry is { UniqueGuid: var g } && g != Guid.Empty)
            np = nd.Partitions.FirstOrDefault(x => x.Entry?.UniqueGuid == g) ?? nd.Partitions.FirstOrDefault(x => x.Offset == p.Offset && !x.IsUnallocated);
        else
            np = nd.Partitions.FirstOrDefault(x => x.Offset == p.Offset && !x.IsUnallocated);
        if (np == null)
            throw new InvalidOperationException($"A partição {p.Letter ?? p.Label ?? DiskOperations.Size(p.Offset)} não existe mais onde estava — uma alteração anterior da fila a removeu ou mudou. Confirme essa primeiro e escolha esta de novo.");
        return np;
    }
}
