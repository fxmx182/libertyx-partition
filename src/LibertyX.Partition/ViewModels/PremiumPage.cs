using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.Licensing;

namespace LibertyX.Partition.ViewModels;

/// <summary>Uma linha da comparação: o que o Básico faz e o que o Premium faz.</summary>
public sealed record FeatureRow(string Name, string Basic, string Premium, bool BasicFull, bool BasicNone)
{
    public bool BasicPartial => !BasicFull && !BasicNone;
}

/// <summary>A tela do Premium: comparação, ativação da chave e onde comprar.</summary>
public sealed partial class PremiumViewModel : PageViewModel
{
    /// <summary>Onde se compra. Vazio = canal ainda não aberto (o botão aparece como "em breve").</summary>
    public const string CardUrl = "";
    public const string TelegramUrl = "https://t.me/libertx_partition_bot?start=partition";

    public override string Title => L.T("LibertyX Premium");
    public override string IconKey => "Crown";

    public List<FeatureRow> Rows { get; } =
    [
        new(L.T("Criar, formatar, redimensionar, mover e excluir partições"), "✓", "✓", true, false),
        new(L.T("Saúde, teste de superfície, benchmark, espaço e duplicados"), "✓", "✓", true, false),
        new(L.T("Procurar e pré-visualizar arquivos apagados"), "✓", "✓", true, false),
        new(L.T("Recuperar arquivos"), L.T("até 1 GB"), L.T("sem limite"), false, false),
        new(L.T("Achar partições perdidas e explorar os arquivos"), "✓", "✓", true, false),
        new(L.T("Restaurar partições perdidas"), "—", "✓", false, true),
        new(L.T("Converter MBR ⇄ GPT sem perder dados"), L.T("disco vazio"), L.T("com os dados"), false, false),
        new(L.T("Migrar o sistema para outro SSD/HD"), "—", "✓", false, true),
        new(L.T("Copiar disco inteiro"), "—", "✓", false, true),
        new(L.T("Criar imagem de disco"), "✓", "✓", true, false),
        new(L.T("Restaurar imagem de disco"), "—", "✓", false, true),
        new(L.T("Pendrive de boot e gravar imagens (como Rufus e balenaEtcher)"), "—", "✓", false, true),
    ];

    [ObservableProperty] private string _keyText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _isPremium;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _quotaUsed;
    [ObservableProperty] private string _quotaText = "";

    public bool CanBuyCard => CardUrl.Length > 0;
    public bool CanBuyCrypto => TelegramUrl.Length > 0;
    public string CardLabel => CanBuyCard ? L.T("Comprar com cartão") : L.T("Cartão — em breve");
    public string CryptoLabel => CanBuyCrypto ? L.T("Bitcoin ou USDT pelo Telegram") : L.T("Bitcoin ou USDT — em breve");

    public PremiumViewModel() => Refresh();
    public override void OnShown(object? arg) => Refresh();

    public void Refresh()
    {
        var lic = Main.License;
        IsPremium = lic.IsPremium;
        var st = lic.State;
        if (IsPremium)
        {
            StatusTitle = L.T("Premium ativo");
            string how = st.Kind == "loja" ? L.T("comprada na loja") : L.T("chave LibertyX");
            StatusText = L.T($"Licença {lic.ShortKey} ({how})") + (st.Holder is { Length: > 0 } h ? $" · {h}" : "")
                         + (st.ActivatedAt is { } a ? L.T($" · ativada em {a.ToLocalTime():dd/MM/yyyy}") : "") + L.T(". Obrigado por apoiar o LibertyX.");
        }
        else
        {
            StatusTitle = L.T("Você está no Básico");
            StatusText = L.T("Tudo para cuidar dos discos, e a recuperação mostra tudo o que dá para salvar. O Premium leva sem limite e libera as ferramentas de resgate completas.");
        }
        QuotaUsed = Math.Min(1, (double)st.FreeRecoveredBytes / LicenseManager.FreeLimit);
        QuotaText = L.T($"{Fmt.Size(Math.Min(st.FreeRecoveredBytes, LicenseManager.FreeLimit))} de {Fmt.Size(LicenseManager.FreeLimit)} já recuperados no Básico");
    }

    [RelayCommand]
    private async Task Activate()
    {
        Error = null;
        Busy = true;
        try
        {
            var err = await Main.License.Activate(KeyText);
            if (err != null) { Error = err; return; }
            KeyText = "";
            Main.LicenseChanged();
            Main.ShowToast(L.T("Premium ativado. Bom proveito!"));
        }
        finally { Busy = false; Refresh(); }
    }

    [RelayCommand]
    private async Task Paste()
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime l ? l.MainWindow : null;
        var clip = top?.Clipboard;
        if (clip != null && await clip.GetTextAsync() is { } t) KeyText = t.Trim();
    }

    [RelayCommand]
    private async Task Deactivate()
    {
        if (!await Main.Confirm(L.T("Desativar neste computador"),
            L.T("O Premium sai deste computador e você pode ativar a mesma chave em outro. Guarde a chave antes de continuar."), L.T("Desativar"), icon: "Key")) return;
        await Main.License.Deactivate();
        Main.LicenseChanged();
        Refresh();
    }

    [RelayCommand] private void BuyCard() { if (CanBuyCard) Open(CardUrl); }
    [RelayCommand] private void BuyCrypto() { if (CanBuyCrypto) Open(TelegramUrl); }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
