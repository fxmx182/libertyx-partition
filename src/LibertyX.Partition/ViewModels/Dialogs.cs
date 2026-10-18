using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LibertyX.Partition.ViewModels;

/// <summary>Diálogo sobreposto à janela (não abre janela nova): escurece o fundo e espera a resposta.</summary>
public abstract partial class DialogViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    public Task<bool> Result => _tcs.Task;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private string _iconKey = "Info";
    public Avalonia.Media.Geometry? Icon => Ui.Icon(IconKey);

    public void Close(bool ok) => _tcs.TrySetResult(ok);
}

public abstract partial class FormField : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string? _hint;
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class TextField : FormField
{
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string? _watermark;
    public int MaxLength { get; init; } = 260;
    public bool Password { get; init; }
    public char PasswordChar => Password ? '•' : '\0';
}

public sealed partial class ChoiceField : FormField
{
    public ObservableCollection<string> Options { get; } = [];
    [ObservableProperty] private int _selectedIndex;
    public event Action<int>? Changed;
    partial void OnSelectedIndexChanged(int value) => Changed?.Invoke(value);
}

public sealed partial class ToggleField : FormField
{
    [ObservableProperty] private bool _value;
    public event Action<bool>? Changed;
    partial void OnValueChanged(bool value) => Changed?.Invoke(value);
}

/// <summary>Tamanho em MB com barra deslizante — redimensionar, dividir, criar.</summary>
public sealed partial class SizeField : FormField
{
    [ObservableProperty] private double _minMb;
    [ObservableProperty] private double _maxMb;
    [ObservableProperty] private double _valueMb;
    [ObservableProperty] private string _caption = "";
    public event Action<double>? Changed;
    partial void OnValueMbChanged(double value)
    {
        double v = Math.Round(Math.Clamp(value, MinMb, MaxMb));
        if (Math.Abs(v - value) > 0.001) { ValueMb = v; return; }
        Changed?.Invoke(v);
        OnPropertyChanged(nameof(ValueText));
    }
    public long Bytes => (long)ValueMb * 1024 * 1024;
    public string ValueText => Fmt.Size(Bytes);
    partial void OnValueMbChanging(double value) { }
}

public sealed partial class InfoField : FormField
{
    /// <summary>"", "warn", "danger", "good", "gold"</summary>
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _text = "";
    public bool HasLabel => !string.IsNullOrEmpty(Label);
    public bool IsNotice => !HasLabel;
    public bool IsWarn => Kind == "warn";
    public bool IsDanger => Kind == "danger";
    public bool IsGood => Kind == "good";
    public bool IsGold => Kind == "gold";
}

public sealed partial class FormDialogViewModel : DialogViewModel
{
    public ObservableCollection<FormField> Fields { get; } = [];
    [ObservableProperty] private string _confirmText = "Aplicar";
    [ObservableProperty] private string _cancelText = "Cancelar";
    [ObservableProperty] private bool _danger;
    /// <summary>Se definido, o botão só libera depois de digitar esta palavra.</summary>
    [ObservableProperty] private string? _confirmPhrase;
    [ObservableProperty] private string _typed = "";
    [ObservableProperty] private string? _error;
    public Func<string?>? Validate { get; set; }

    public bool PhraseRequired => !string.IsNullOrEmpty(ConfirmPhrase);
    public bool CanConfirm => !PhraseRequired || string.Equals(Typed.Trim(), ConfirmPhrase, StringComparison.OrdinalIgnoreCase);
    partial void OnTypedChanged(string value) => OnPropertyChanged(nameof(CanConfirm));
    partial void OnConfirmPhraseChanged(string? value) { OnPropertyChanged(nameof(CanConfirm)); OnPropertyChanged(nameof(PhraseRequired)); }

    public T Add<T>(T f) where T : FormField { Fields.Add(f); return f; }
    public InfoField Note(string text, string kind = "") => Add(new InfoField { Text = text, Kind = kind });

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm) return;
        Error = Validate?.Invoke();
        if (Error == null) Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}

/// <summary>Caminho com botão "Escolher…" (pasta ou arquivo).</summary>
public sealed partial class PathField : FormField
{
    [ObservableProperty] private string _value = "";
    public bool Folder { get; init; } = true;
    public string PickerTitle { get; init; } = "Escolher";

    [RelayCommand]
    private async Task Browse()
    {
        var p = Folder ? await MainViewModel.PickFolder(PickerTitle) : await MainViewModel.PickFile(PickerTitle);
        if (p != null) Value = p;
    }
}
