using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KidControl.UiHost.Services;

namespace KidControl.UiHost.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const double DefaultCycleSeconds = 60d * 60d;
    private readonly DispatcherTimer _localCountdownTimer;
    private readonly object _timeSync = new();
    private TimeSpan _lastKnownRemaining = TimeSpan.Zero;
    private bool _hasSnapshot;

    [ObservableProperty]
    private string timeRemaining = "00:00:00";

    [ObservableProperty]
    private bool isBlocked;

    [ObservableProperty]
    private bool isNightBlocked;

    [ObservableProperty]
    private bool isNightModeActive;

    [ObservableProperty]
    private double progressPercent = 100;

    [ObservableProperty]
    private string sessionLabel = "Игровая сессия";

    public MainViewModel(NamedPipeClient pipeClient)
    {
        _localCountdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _localCountdownTimer.Tick += (_, _) => ApplyLocalCountdownTick();
        _localCountdownTimer.Start();

        pipeClient.OnStateReceived += state =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                lock (_timeSync)
                {
                    _lastKnownRemaining = state.TimeRemaining < TimeSpan.Zero ? TimeSpan.Zero : state.TimeRemaining;
                    _hasSnapshot = true;
                }

                TimeRemaining = _lastKnownRemaining.ToString(@"hh\:mm\:ss");
                IsNightModeActive = state.IsNightMode;
                IsBlocked = string.Equals(state.Status, "Blocked", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(state.Status, "ForceBlocked", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(state.Status, "NightBlock", StringComparison.OrdinalIgnoreCase);
                IsNightBlocked = IsNightModeActive && IsBlocked;
                SessionLabel = IsBlocked ? "Ограниченный режим" : "Игровая сессия";
                var percent = _lastKnownRemaining.TotalSeconds <= 0
                    ? 0
                    : Math.Clamp(_lastKnownRemaining.TotalSeconds / DefaultCycleSeconds * 100d, 0, 100);
                ProgressPercent = percent;
            });
        };
    }

    private void ApplyLocalCountdownTick()
    {
        lock (_timeSync)
        {
            if (!_hasSnapshot || _lastKnownRemaining <= TimeSpan.Zero)
            {
                return;
            }

            _lastKnownRemaining = _lastKnownRemaining - TimeSpan.FromSeconds(1);
            if (_lastKnownRemaining < TimeSpan.Zero)
            {
                _lastKnownRemaining = TimeSpan.Zero;
            }
        }

        TimeRemaining = _lastKnownRemaining.ToString(@"hh\:mm\:ss");
        var percent = _lastKnownRemaining.TotalSeconds <= 0
            ? 0
            : Math.Clamp(_lastKnownRemaining.TotalSeconds / DefaultCycleSeconds * 100d, 0, 100);
        ProgressPercent = percent;
    }
}
