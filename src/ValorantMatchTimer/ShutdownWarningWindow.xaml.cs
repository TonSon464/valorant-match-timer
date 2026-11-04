using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ValorantMatchTimer.Core;
using ValorantMatchTimer.UI;

namespace ValorantMatchTimer;

public partial class ShutdownWarningWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _deadline;
    private readonly Action _onCancel;
    private readonly Action _onShutdown;
    private bool _finished;

    public ShutdownWarningWindow(
        string title,
        string detail,
        int seconds,
        Action onCancel,
        Action onShutdown,
        bool test = false)
    {
        InitializeComponent();
        WindowTheme.EnableDarkTitleBar(this);

        _onCancel = onCancel;
        _onShutdown = onShutdown;
        _deadline = DateTimeOffset.Now.AddSeconds(Math.Max(1, seconds));

        TitleText.Text = title.ToUpperInvariant();
        DetailText.Text = detail;

        if (test)
        {
            CountdownLabel.Text = "TEST COUNTDOWN";
            CancelButton.Content = "Close";
            ShutdownButton.Visibility = Visibility.Collapsed;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) =>
        {
            _timer.Start();
            Tick();
        };
        Closing += OnClosing;
    }

    private void Tick()
    {
        var remaining = (_deadline - DateTimeOffset.Now).TotalSeconds;
        if (remaining <= 0)
        {
            Finish(shutdown: true);
            return;
        }

        CountdownText.Text = DurationParser.FormatMatch(remaining);
    }

    private void Finish(bool shutdown)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timer.Stop();

        if (shutdown)
        {
            _onShutdown();
        }
        else
        {
            _onCancel();
        }

        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timer.Stop();
        _onCancel();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(shutdown: false);

    private void Shutdown_Click(object sender, RoutedEventArgs e) => Finish(shutdown: true);
}
