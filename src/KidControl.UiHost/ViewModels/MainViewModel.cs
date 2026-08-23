using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KidControl.Contracts;
using KidControl.UiHost.Services;

namespace KidControl.UiHost.ViewModels;

/// <summary>
/// Presentation state for the overlay. Fed by the service over the state pipe, and kept
/// visually smooth between pushes by a local 1-second countdown that decrements the last
/// known remaining time. Status strings mirror the Domain <c>SessionStatus</c> enum:
/// "Playing", "Resting", "ForceBlocked", "Paused", "NightBlocked".
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const double DefaultCycleSeconds = 60d * 60d;

    private readonly DispatcherTimer _localCountdownTimer;
    private readonly object _timeSync = new();
    private TimeSpan _lastKnownRemaining = TimeSpan.Zero;
    private bool _hasSnapshot;

    [ObservableProperty]
    private string timeRemaining = "00:00:00";

    /// <summary>Alias binding used by the widget timer text.</summary>
    public string TimeRemainingStr => TimeRemaining;

    [ObservableProperty]
    private bool isBlocked;

    [ObservableProperty]
    private bool isNightBlocked;

    [ObservableProperty]
    private bool isNightModeActive;

    [ObservableProperty]
    private bool isUnlimited;

    [ObservableProperty]
    private string sessionLabel = "Игровая сессия";

    [ObservableProperty]
    private double progressPercent = 100;

    public MainViewModel(StatePipeClient pipeClient)
    {
        _localCountdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _localCountdownTimer.Tick += (_, _) => ApplyLocalCountdownTick();
        _localCountdownTimer.Start();

        pipeClient.OnStateReceived += OnStateReceived;
    }

    private void OnStateReceived(SessionStateDto state)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            lock (_timeSync)
            {
                _lastKnownRemaining = state.TimeRemaining < TimeSpan.Zero ? TimeSpan.Zero : state.TimeRemaining;
                _hasSnapshot = true;
            }

            IsNightModeActive = state.IsNightMode;
            IsUnlimited = state.IsUnlimited;
            IsBlocked = IsBlockingStatus(state.Status);
            IsNightBlocked = IsNightModeActive && IsBlocked;

            if (IsUnlimited && !IsBlocked)
            {
                TimeRemaining = "∞";
                SessionLabel = "Без ограничений";
                ProgressPercent = 100;
            }
            else
            {
                TimeRemaining = _lastKnownRemaining.ToString(@"hh\:mm\:ss");
                SessionLabel = IsBlocked ? "Ограниченный режим" : "Игровая сессия";
                ProgressPercent = ComputeProgress(_lastKnownRemaining);
            }
        });
    }

    private static bool IsBlockingStatus(string? status) =>
        string.Equals(status, "Resting", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "ForceBlocked", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "NightBlocked", StringComparison.OrdinalIgnoreCase);

    private static double ComputeProgress(TimeSpan remaining) =>
        remaining.TotalSeconds <= 0
            ? 0
            : Math.Clamp(remaining.TotalSeconds / DefaultCycleSeconds * 100d, 0, 100);

    partial void OnTimeRemainingChanged(string value)
    {
        OnPropertyChanged(nameof(TimeRemainingStr));
    }

    private void ApplyLocalCountdownTick()
    {
        if (IsUnlimited)
        {
            return; // no countdown when unlimited
        }

        lock (_timeSync)
        {
            if (!_hasSnapshot || _lastKnownRemaining <= TimeSpan.Zero)
            {
                return;
            }

            _lastKnownRemaining -= TimeSpan.FromSeconds(1);
            if (_lastKnownRemaining < TimeSpan.Zero)
            {
                _lastKnownRemaining = TimeSpan.Zero;
            }
        }

        TimeRemaining = _lastKnownRemaining.ToString(@"hh\:mm\:ss");
        ProgressPercent = ComputeProgress(_lastKnownRemaining);
    }
}
