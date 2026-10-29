using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibertyX.Partition.Core.Operations;

namespace LibertyX.Partition.ViewModels;

/// <summary>A janela de progresso de qualquer operação longa: arco de progresso, log, pausa, cancelar.</summary>
public sealed partial class OperationViewModel : ViewModelBase
{
    public OpSink Sink { get; } = new();
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = L.T("Preparando…");
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _indeterminate = true;
    [ObservableProperty] private bool _running = true;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private string? _resultText;
    public bool CanPause { get; init; }
    public ObservableCollection<string> Log { get; } = [];
    public Func<Task>? AfterClose { get; set; }
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private DateTime _lastUi;

    public string PercentText => Indeterminate ? "" : $"{Fraction * 100:0}%";
    public bool Done => !Running;
    partial void OnFractionChanged(double value) => OnPropertyChanged(nameof(PercentText));
    partial void OnIndeterminateChanged(bool value) => OnPropertyChanged(nameof(PercentText));
    public bool Succeeded => !Running && !Failed;
    partial void OnRunningChanged(bool value) { OnPropertyChanged(nameof(Done)); OnPropertyChanged(nameof(Succeeded)); }
    partial void OnFailedChanged(bool value) => OnPropertyChanged(nameof(Succeeded));

    public OperationViewModel()
    {
        Sink.LogLine += line => Ui.Post(() => { Log.Add($"{DateTime.Now:HH:mm:ss}  {line}"); });
        Sink.ProgressChanged += (f, s, d) =>
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUi).TotalMilliseconds < 120 && f < 1 && f >= 0) return;
            _lastUi = now;
            Ui.Post(() =>
            {
                if (f >= 0) { Fraction = Math.Clamp(f, 0, 1); Indeterminate = false; } else Indeterminate = true;
                if (s != null) Status = s;
                Detail = d;
                Elapsed = Fmt.Duration(_sw.Elapsed);
                if (f > 0.01 && f < 1)
                {
                    var eta = TimeSpan.FromSeconds(_sw.Elapsed.TotalSeconds * (1 - f) / f);
                    Elapsed += L.T($" · faltam ~{Fmt.Duration(eta)}");
                }
            });
        };
    }

    public void Finish(bool ok, string? message)
    {
        Ui.Post(() =>
        {
            Running = false;
            Failed = !ok;
            Indeterminate = false;
            if (ok) Fraction = 1;
            Status = ok ? L.T("Concluído") : L.T("Não foi possível concluir");
            ResultText = message;
            Elapsed = Fmt.Duration(_sw.Elapsed);
            if (message != null) Log.Add(message);
        });
    }

    [RelayCommand]
    private void Cancel()
    {
        Sink.Resume();
        Sink.Cancel();
        Status = L.T("Cancelando…");
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (Sink.Paused) Sink.Resume(); else Sink.Pause();
        Paused = Sink.Paused;
    }

    [RelayCommand]
    private async Task Close()
    {
        MainViewModel.Instance.Operation = null;
        if (AfterClose != null) await AfterClose();
    }
}
