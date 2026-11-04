using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ValorantMatchTimer.Core;

namespace ValorantMatchTimer;

public partial class OverlayWindow : Window
{
    private readonly MainWindow _owner;
    private readonly AppSettings _settings;
    private readonly Brush _textBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF5));
    private readonly Brush _mutedBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA7));
    private readonly Brush _warnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
    private Brush _accentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x46, 0x55));
    private IntPtr _hwnd;

    public OverlayWindow(MainWindow owner, AppSettings settings)
    {
        InitializeComponent();
        _owner = owner;
        _settings = settings;

        Root.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x13, 0x1A));
        _textBrush.Freeze();
        _mutedBrush.Freeze();
        _warnBrush.Freeze();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough();
            Reposition();
        };
        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    public void ApplySettings()
    {
        Opacity = Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);
        RootScale.ScaleX = Math.Clamp(_settings.OverlayScale, 0.5, 2.0);
        RootScale.ScaleY = RootScale.ScaleX;
        _accentBrush = ParseBrush(_settings.OverlayAccent);
        Root.BorderBrush = _accentBrush;

        TimeLabel.Visibility = _settings.OverlayShowTime ? Visibility.Visible : Visibility.Collapsed;
        TimeText.Visibility = TimeLabel.Visibility;
        ClockText.Visibility = _settings.OverlayShowClock ? Visibility.Visible : Visibility.Collapsed;
        MatchText.Visibility = _settings.OverlayShowMatch ? Visibility.Visible : Visibility.Collapsed;
        SubText.Visibility = _settings.OverlayShowTime || _settings.OverlayShowMatch
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyClickThrough();
        Reposition();
    }

    public void Refresh(OverlaySnapshot snapshot)
    {
        ClockText.Text = snapshot.Now.ToString("HH:mm");

        if (snapshot.Cutoff.HasValue)
        {
            var remaining = (snapshot.Cutoff.Value - snapshot.Now).TotalSeconds;
            if (remaining > 0)
            {
                TimeText.Text = DurationParser.FormatHms(remaining);
                TimeText.Foreground = snapshot.InOverTime ? _accentBrush : _textBrush;
            }
            else
            {
                TimeText.Text = "00:00:00";
                TimeText.Foreground = _warnBrush;
            }
        }
        else
        {
            TimeText.Text = "SET TIME";
            TimeText.Foreground = _mutedBrush;
        }

        if (snapshot.MatchLabel is null)
        {
            MatchText.Text = snapshot.PendingShutdown
                ? "Waiting for the match to end"
                : "No match detected";
            MatchText.Foreground = snapshot.PendingShutdown ? _warnBrush : _mutedBrush;
        }
        else
        {
            var map = string.IsNullOrWhiteSpace(snapshot.MatchMap) ? "" : $" · {snapshot.MatchMap}";
            if (snapshot.InOverTime)
            {
                var overtime = DurationParser.FormatMatch(-snapshot.MatchRemainingSeconds);
                MatchText.Text = $"{snapshot.MatchLabel}{map} · OT +{overtime}";
                MatchText.Foreground = _accentBrush;
            }
            else
            {
                var end = snapshot.MatchEnd?.ToString("HH:mm") ?? "--:--";
                MatchText.Text = _settings.OverlayShowEnd
                    ? $"{snapshot.MatchLabel}{map} · ends {end}"
                    : $"{snapshot.MatchLabel}{map} · {DurationParser.FormatMatch(snapshot.MatchRemainingSeconds)} left";
                MatchText.Foreground = _mutedBrush;
            }
        }

        var lines = new List<string>();
        if (snapshot.Cutoff.HasValue)
        {
            lines.Add($"Get off at {snapshot.Cutoff.Value:HH:mm}");
        }
        if (snapshot.PendingShutdown)
        {
            lines.Add("Shutdown after the current match/OT");
        }
        SubText.Text = string.Join("\n", lines);
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        NativeMethods.ApplyOverlayStyles(_hwnd, _settings.OverlayClickThrough);
    }

    private void Reposition()
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : 300;
        var height = ActualHeight > 0 ? ActualHeight : 120;
        var work = SystemParameters.WorkArea;
        const double margin = 16;

        double x;
        double y;
        switch (_settings.OverlayPosition)
        {
            case "top-left":
                x = work.Left + margin;
                y = work.Top + margin;
                break;
            case "top-center":
                x = work.Left + (work.Width - width) / 2;
                y = work.Top + margin;
                break;
            case "bottom-left":
                x = work.Left + margin;
                y = work.Bottom - height - margin;
                break;
            case "bottom-center":
                x = work.Left + (work.Width - width) / 2;
                y = work.Bottom - height - margin;
                break;
            case "bottom-right":
                x = work.Right - width - margin;
                y = work.Bottom - height - margin;
                break;
            case "custom" when _settings.OverlayPos is { Length: 2 }:
                x = _settings.OverlayPos[0];
                y = _settings.OverlayPos[1];
                break;
            default:
                x = work.Right - width - margin;
                y = work.Top + margin;
                break;
        }

        Left = Math.Max(work.Left, Math.Min(x, work.Right - width));
        Top = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.OverlayClickThrough)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            return;
        }

        _settings.OverlayPos = new[] { Left, Top };
        _settings.OverlayPosition = "custom";
        SettingsStore.Save(_settings);
        _owner.ApplySettings();
    }

    private static Brush ParseBrush(string value)
    {
        try
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(value)!;
            brush.Freeze();
            return brush;
        }
        catch
        {
            return new SolidColorBrush(Color.FromRgb(0xFF, 0x46, 0x55));
        }
    }
}
