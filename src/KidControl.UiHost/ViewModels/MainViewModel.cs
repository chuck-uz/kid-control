using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using KidControl.UiHost.Services;

namespace KidControl.UiHost.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string timeRemaining = "00:00:00";

    [ObservableProperty]
    private bool isBlocked;

    [ObservableProperty]
    private bool isNightBlocked;

    [ObservableProperty]
    private bool isNightModeActive;

    public MainViewModel(NamedPipeClient pipeClient)
    {
        pipeClient.OnStateReceived += state =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                TimeRemaining = state.TimeRemaining.ToString(@"hh\:mm\:ss");
                IsNightModeActive = state.IsNightMode;
                IsBlocked = string.Equals(state.Status, "Blocked", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(state.Status, "ForceBlocked", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(state.Status, "NightBlock", StringComparison.OrdinalIgnoreCase);
                IsNightBlocked = IsNightModeActive && IsBlocked;
            });
        };
    }
}
