using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows;
using System.Windows.Controls;
using KidControl.UiHost.ViewModels;

namespace KidControl.UiHost;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;
    private const double WidgetWidth = 260;
    private const double WidgetHeight = 140;
    private Storyboard? _blockedTimerStoryboard;

    private readonly MainViewModel _viewModel;
    private bool _isTransitioning;
    private bool _pendingBlockedState;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += (_, _) =>
        {
            MoveWidgetToCorner();
            StartBackgroundParticles();
            UpdateWidgetProgressRing();
            ApplyVisualMode(_viewModel.IsBlocked);
        };

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsBlocked) ||
                args.PropertyName == nameof(MainViewModel.IsNightBlocked))
            {
                Dispatcher.Invoke(async () => await TransitionToStateAsync(_viewModel.IsBlocked));
            }
            else if (args.PropertyName == nameof(MainViewModel.ProgressPercent))
            {
                Dispatcher.Invoke(UpdateWidgetProgressRing);
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
    }

    private void MoveWidgetToCorner()
    {
        Left = SystemParameters.WorkArea.Right - Width - 8;
        Top = SystemParameters.WorkArea.Top + 8;
    }

    private void UpdateWidgetProgressRing()
    {
        const double radius = 45;
        var center = new Point(49, 49);
        var progress = Math.Clamp(_viewModel.ProgressPercent / 100d, 0, 1);
        if (progress <= 0.001)
        {
            WidgetProgressPath.Data = null;
            return;
        }

        var endAngle = (Math.PI * 2 * progress) - (Math.PI / 2);
        var end = new Point(
            center.X + radius * Math.Cos(endAngle),
            center.Y + radius * Math.Sin(endAngle));
        var start = new Point(center.X, center.Y - radius);
        var isLargeArc = progress > 0.5;

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc
        });
        WidgetProgressPath.Data = new PathGeometry(new[] { figure });
    }

    private async Task TransitionToStateAsync(bool isBlocked)
    {
        _pendingBlockedState = isBlocked;
        if (_isTransitioning)
        {
            return;
        }

        _isTransitioning = true;
        try
        {
            while (true)
            {
                var targetState = _pendingBlockedState;
                await FadeToAsync(0.15, 140);
                ApplyVisualMode(targetState);
                await FadeToAsync(1.0, 170);

                if (_pendingBlockedState == targetState)
                {
                    break;
                }
            }
        }
        finally
        {
            _isTransitioning = false;
        }
    }

    private void ApplyVisualMode(bool isBlocked)
    {
        if (isBlocked)
        {
            ApplyClickThrough(isBlocked: true);
            Background = new SolidColorBrush(Colors.Black);
            WindowState = WindowState.Maximized;
            WidgetBorder.Visibility = Visibility.Collapsed;
            BlockOverlay.Visibility = Visibility.Visible;
            if (_viewModel.IsNightBlocked)
            {
                BlockReasonText.Text = "Спокойной ночи, увидимся завтра!";
                BlockedTimerText.Visibility = Visibility.Collapsed;
                StopBlockedTimerAnimation();
            }
            else
            {
                BlockReasonText.Text = "Время игры закончилось. Перерыв.";
                BlockedTimerText.Visibility = Visibility.Visible;
                StartBlockedTimerAnimation();
            }
        }
        else
        {
            WindowState = WindowState.Normal;
            Width = WidgetWidth;
            Height = WidgetHeight;
            Background = Brushes.Transparent;
            WidgetBorder.Visibility = Visibility.Visible;
            BlockOverlay.Visibility = Visibility.Collapsed;
            MoveWidgetToCorner();
            ApplyClickThrough(isBlocked: false);
            StopBlockedTimerAnimation();
            BlockedTimerText.Visibility = Visibility.Visible;
            BlockReasonText.Text = "Время игры закончилось. Перерыв.";
        }
    }

    private Task FadeToAsync(double to, int durationMs)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        animation.Completed += (_, _) => tcs.TrySetResult();
        BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        return tcs.Task;
    }

    private void ApplyClickThrough(bool isBlocked)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var exStyle = GetWindowLong(handle, GwlExStyle);
        if (isBlocked)
        {
            exStyle &= ~WsExTransparent;
        }
        else
        {
            exStyle |= WsExLayered | WsExTransparent;
        }

        SetWindowLong(handle, GwlExStyle, exStyle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void StartBlockedTimerAnimation()
    {
        if (_blockedTimerStoryboard is not null)
        {
            _blockedTimerStoryboard.Begin(this, true);
            return;
        }

        var opacityAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.72,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        var scaleXAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 1.035,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        var scaleYAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 1.035,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        _blockedTimerStoryboard = new Storyboard();
        _blockedTimerStoryboard.Children.Add(opacityAnimation);
        _blockedTimerStoryboard.Children.Add(scaleXAnimation);
        _blockedTimerStoryboard.Children.Add(scaleYAnimation);

        Storyboard.SetTarget(opacityAnimation, BlockedTimerText);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));

        Storyboard.SetTarget(scaleXAnimation, BlockedTimerText);
        Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("RenderTransform.(ScaleTransform.ScaleX)"));

        Storyboard.SetTarget(scaleYAnimation, BlockedTimerText);
        Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("RenderTransform.(ScaleTransform.ScaleY)"));

        _blockedTimerStoryboard.Begin(this, true);
    }

    private void StopBlockedTimerAnimation()
    {
        _blockedTimerStoryboard?.Stop(this);
        BlockedTimerText.Opacity = 1.0;
        if (BlockedTimerText.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
        }
    }

    private void StartBackgroundParticles()
    {
        AnimateStar(Star1, 0, 8);
        AnimateStar(Star2, 200, 10);
        AnimateStar(Star3, 450, 9);
        AnimateStar(Star4, 650, 12);
        AnimateStar(Star5, 900, 11);
    }

    private static void AnimateStar(UIElement star, int delayMs, int range)
    {
        var yAnimation = new DoubleAnimation
        {
            From = 0,
            To = range,
            Duration = TimeSpan.FromSeconds(5.5),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        var opacityAnimation = new DoubleAnimation
        {
            From = 0.3,
            To = 0.95,
            Duration = TimeSpan.FromSeconds(3.2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };

        star.BeginAnimation(Canvas.TopProperty, yAnimation, HandoffBehavior.SnapshotAndReplace);
        star.BeginAnimation(UIElement.OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
    }
}
