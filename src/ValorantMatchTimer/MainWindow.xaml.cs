using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ValorantMatchTimer.Core;
using ValorantMatchTimer.UI;

namespace ValorantMatchTimer;

public partial class MainWindow : Window
{
    private sealed class ActiveMatch
    {
        public string ModeKey { get; init; } = "custom";
        public string Label { get; init; } = "Custom";
        public int ExpectedSeconds { get; init; }
        public DateTimeOffset Start { get; init; }
        public string Source { get; init; } = "manual";
        public string Map { get; init; } = "";
        public string Broad { get; init; } = "";
        public bool Done { get; set; }
    }

    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly DispatcherTimer _tickTimer;
    private readonly Dictionary<string, Button> _modeButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Brush _accentBrush;
    private readonly Brush _card2Brush;
    private readonly Brush _textBrush;
    private readonly Brush _mutedBrush;
    private readonly Brush _warnBrush;
    private readonly Brush _greenBrush;

    private LogWatcher? _watcher;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private ShutdownWarningWindow? _warning;
    private DateTimeOffset? _cutoff;
    private bool _cutoffFired;
    private ActiveMatch? _active;
    private string? _pendingShutdownReason;
    private string _selectedMode = "competitive";
    private bool _closing;
    private bool _overlayIsGame;
    private DateTime _lastAutoCheck = DateTime.MinValue;

    public ObservableCollection<string> ActivityLog { get; } = new();
    public AppSettings Settings => _settings;
    public DateTimeOffset? Cutoff => _cutoff;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.EnableDarkTitleBar(this);

        _accentBrush = (Brush)FindResource("AccentBrush");
        _card2Brush = (Brush)FindResource("Card2Brush");
        _textBrush = (Brush)FindResource("TextBrush");
        _mutedBrush = (Brush)FindResource("MutedBrush");
        _warnBrush = (Brush)FindResource("WarnBrush");
        _greenBrush = (Brush)FindResource("GreenBrush");

        _settings = SettingsStore.Load();
        _history = new HistoryStore();
        _selectedMode = GameModes.IsKnown(_settings.SelectedMode) ? _settings.SelectedMode : "competitive";

        BuildModeButtons();
        CustomDurationBox.Text = _settings.CustomDuration;

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tickTimer.Tick += (_, _) => Tick();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadCutoff();
        SelectMode(_selectedMode);
        RestartWatcher();

        if (_settings.StartWithWindows)
        {
            var (ok, message) = StartupManager.SetEnabled(true);
            Log(ok ? message : $"Startup change failed: {message}");
        }
        else
        {
            StartupManager.RemoveLegacyShortcut();
        }

        if (_settings.ShowOverlay)
        {
            EnsureOverlay();
            _overlay?.ApplySettings();
        }

        _tickTimer.Start();
        Log("Ready. Set a get-off time or start a manual timer.");
        Log("Valorant auto-detect watches ShooterGame.log; the get-off timer works over any game.");
    }

    private void BuildModeButtons()
    {
        ModeGrid.Children.Clear();
        _modeButtons.Clear();

        foreach (var mode in GameModes.All)
        {
            var button = new Button
            {
                Tag = mode.Key,
                Content = mode.Key == "custom"
                    ? "Custom\nset time"
                    : $"{mode.Label}\n{mode.Minutes}m",
                Style = (Style)FindResource("BaseButton"),
                FontSize = 10.5,
                Padding = new Thickness(4, 6, 4, 6),
                Margin = new Thickness(2),
                Height = 46
            };

            button.Click += (_, _) => SelectMode(mode.Key);
            ModeGrid.Children.Add(button);
            _modeButtons[mode.Key] = button;
        }
    }

    private void SelectMode(string key)
    {
        if (!GameModes.IsKnown(key))
        {
            return;
        }

        _selectedMode = key;
        foreach (var (modeKey, button) in _modeButtons)
        {
            var selected = string.Equals(modeKey, key, StringComparison.OrdinalIgnoreCase);
            button.Background = selected ? _accentBrush : _card2Brush;
            button.Foreground = selected ? Brushes.White : _textBrush;
        }

        if (key == "custom")
        {
            CustomDurationBox.Focus();
            CustomDurationBox.SelectAll();
        }

        UpdatePrediction();
        SaveSettings();
    }

    private void UpdatePrediction()
    {
        if (_selectedMode == "custom")
        {
            var seconds = DurationParser.ParseDuration(CustomDurationBox.Text) ?? GameModes.Get("custom").Seconds;
            PredictionText.Text = $"Custom prediction: {DurationParser.FormatDuration(seconds)}";
            return;
        }

        var mode = GameModes.Get(_selectedMode);
        var (predicted, count, average) = _history.Predicted(mode.Key, mode.Seconds);
        PredictionText.Text = count > 0
            ? $"{mode.Label} prediction: {DurationParser.FormatDuration(predicted)}  (avg of {count} past game{(count == 1 ? "" : "s")}, {DurationParser.FormatDuration(average)})"
            : $"{mode.Label} prediction: {DurationParser.FormatDuration(mode.Seconds)}  (default — gets smarter after games)";
    }

    private int PredictionFor(string key)
    {
        if (key == "custom")
        {
            return DurationParser.ParseDuration(CustomDurationBox.Text) ?? GameModes.Get("custom").Seconds;
        }

        var mode = GameModes.Get(key);
        var (predicted, _, _) = _history.Predicted(key, mode.Seconds);
        return predicted;
    }

    private void StartSelected()
    {
        if (_selectedMode == "custom")
        {
            var seconds = DurationParser.ParseDuration(CustomDurationBox.Text);
            if (!seconds.HasValue)
            {
                Log("Custom duration is invalid. Try 35m, 1h 10m, or 90.");
                Beep();
                return;
            }
            if (seconds.Value > 6 * 3600)
            {
                Log("Custom duration is too long.");
                return;
            }
            StartMatch("custom", seconds.Value, "manual", DateTimeOffset.Now, "", "");
            return;
        }

        StartMatch(_selectedMode, PredictionFor(_selectedMode), "manual", DateTimeOffset.Now, "", "");
    }

    private void StartMatch(
        string key,
        int expectedSeconds,
        string source,
        DateTimeOffset start,
        string map,
        string broad,
        bool resumed = false)
    {
        var mode = GameModes.Get(key);
        _active = new ActiveMatch
        {
            ModeKey = mode.Key,
            Label = mode.Label,
            ExpectedSeconds = expectedSeconds,
            Start = start,
            Source = source,
            Map = map,
            Broad = broad
        };

        var when = start.ToString("HH:mm");
        if (resumed)
        {
            Log($"Resumed {mode.Label} match on {(string.IsNullOrWhiteSpace(map) ? "map" : map)} (started {when}).");
        }
        else if (source == "manual")
        {
            Log($"Manual timer started: {mode.Label}, {DurationParser.FormatDuration(expectedSeconds)}.");
        }
        else
        {
            Log($"Auto-detected match: {mode.Label} on {(string.IsNullOrWhiteSpace(map) ? "map" : map)} (started {when}).");
        }

        MatchStatusText.Text = $"{mode.Label} · {(source == "auto" ? "auto" : "manual")}";
        UpdatePrediction();
    }

    private void StopActiveTimer()
    {
        if (_active is not null)
        {
            Log("Match timer stopped.");
        }

        var shouldShutdown = _active is null || _active.Source == "manual";
        _active = null;
        MatchStatusText.Text = "Not in a match";
        MatchText.Text = "00:00";
        MatchText.Foreground = _textBrush;
        MatchPredictionText.Text = "Pick a mode, then press Start for a manual timer.";
        MatchProgress.Value = 0;

        if (shouldShutdown && !string.IsNullOrEmpty(_pendingShutdownReason))
        {
            var reason = _pendingShutdownReason;
            _pendingShutdownReason = null;
            BeginShutdownWarning("Get-off time reached", reason, _settings.WarningMinutes * 60);
        }
    }

    private void ClearStats()
    {
        _history.Clear();
        UpdatePrediction();
        Log("Prediction history cleared.");
    }

    private void SetCutoff()
    {
        var target = DurationParser.ParseClock(ClockBox.Text);
        if (!target.HasValue)
        {
            Log("Get-off time is invalid. Use 24-hour HH:MM, e.g. 23:30.");
            Beep();
            return;
        }

        _cutoff = target.Value;
        _cutoffFired = false;
        ClockBox.Text = target.Value.ToString("HH:mm");
        Log($"Get-off time set for {target.Value:HH:mm}.");
        SaveSettings();
        RenderGetOff(DateTimeOffset.Now);
    }

    private void QuickCutoff(int minutes)
    {
        _cutoff = DateTimeOffset.Now.AddMinutes(minutes);
        _cutoffFired = false;
        ClockBox.Text = _cutoff.Value.ToString("HH:mm");
        Log($"Get-off time set for {_cutoff.Value:HH:mm} (+{minutes}m).");
        SaveSettings();
        RenderGetOff(DateTimeOffset.Now);
    }

    private void ClearCutoff()
    {
        _cutoff = null;
        _cutoffFired = false;
        _pendingShutdownReason = null;
        GetOffText.Text = "—:—:—";
        GetOffText.Foreground = _textBrush;
        GetOffHint.Text = "Set the clock time you need to stop.";
        Log("Get-off time cleared.");
        SaveSettings();
    }

    private void LoadCutoff()
    {
        if (string.IsNullOrWhiteSpace(_settings.CutoffIso))
        {
            return;
        }

        if (DateTimeOffset.TryParse(
                _settings.CutoffIso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var target)
            && target > DateTimeOffset.Now.AddSeconds(-60))
        {
            _cutoff = target;
            ClockBox.Text = target.ToString("HH:mm");
        }
        else
        {
            _settings.CutoffIso = null;
        }
    }

    private void Tick()
    {
        if (_closing)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        RenderGetOff(now);
        RenderMatch(now);

        if (_cutoff.HasValue && !_cutoffFired && now >= _cutoff.Value)
        {
            _cutoffFired = true;
            Log($"Get-off time reached ({_cutoff.Value:HH:mm}).");

            if (_settings.DeferShutdownInMatch && _active is not null)
            {
                _pendingShutdownReason = $"Your cutoff was {_cutoff.Value:HH:mm}. The match has finished, so time is up.";
                Log("Match still in progress. Shutdown deferred until the match/OT ends.");
                Beep();
            }
            else
            {
                BeginShutdownWarning(
                    "Get-off time reached",
                    $"Your cutoff was {_cutoff.Value:HH:mm}. Save and log off.",
                    _settings.WarningMinutes * 60);
            }
        }

        UpdateOverlay(now);
    }

    private void RenderGetOff(DateTimeOffset now)
    {
        if (!_cutoff.HasValue)
        {
            GetOffText.Text = "—:—:—";
            GetOffText.Foreground = _textBrush;
            GetOffHint.Text = "Set the clock time you need to stop.";
            return;
        }

        var remaining = (_cutoff.Value - now).TotalSeconds;
        if (_cutoffFired || remaining <= 0)
        {
            GetOffText.Text = "00:00:00";
            GetOffText.Foreground = _warnBrush;
            GetOffHint.Text = !string.IsNullOrEmpty(_pendingShutdownReason)
                ? $"Get-off time reached ({_cutoff.Value:HH:mm}). Waiting for the match/OT to finish."
                : $"Get-off time reached ({_cutoff.Value:HH:mm}). Shutdown warning active.";
            return;
        }

        GetOffText.Text = DurationParser.FormatHms(remaining);
        GetOffText.Foreground = _textBrush;
        GetOffHint.Text = $"Get off at {_cutoff.Value:HH:mm}. Works over any game.";
    }

    private void RenderMatch(DateTimeOffset now)
    {
        if (_active is null)
        {
            return;
        }

        var match = _active;
        var elapsed = (now - match.Start).TotalSeconds;
        var remaining = match.ExpectedSeconds - elapsed;

        if (remaining >= 0)
        {
            MatchText.Text = DurationParser.FormatMatch(remaining);
            MatchText.Foreground = _textBrush;
        }
        else
        {
            MatchText.Text = $"+{DurationParser.FormatMatch(-remaining)}";
            MatchText.Foreground = _accentBrush;
        }

        var endClock = match.Start.AddSeconds(match.ExpectedSeconds).ToString("HH:mm");
        var mapPart = string.IsNullOrWhiteSpace(match.Map) ? "" : $" · {match.Map}";
        MatchStatusText.Text = $"{match.Label}{mapPart} · {(match.Source == "auto" ? "auto" : "manual")}";
        MatchPredictionText.Text = remaining >= 0
            ? $"Predicted end {endClock} · {DurationParser.FormatDuration(match.ExpectedSeconds)} expected · elapsed {DurationParser.FormatMatch(elapsed)}"
            : $"Prediction ended at {endClock} · still in match · elapsed {DurationParser.FormatMatch(elapsed)}";
        MatchProgress.Value = Math.Max(0, Math.Min(1000, 1000.0 * elapsed / Math.Max(1, match.ExpectedSeconds)));

        if (remaining > 0 || match.Done)
        {
            return;
        }

        match.Done = true;
        if (match.Source != "manual")
        {
            return;
        }

        Log($"Manual timer finished: {match.Label} ({DurationParser.FormatDuration(match.ExpectedSeconds)}).");
        if (!string.IsNullOrEmpty(_pendingShutdownReason))
        {
            var reason = _pendingShutdownReason;
            _pendingShutdownReason = null;
            BeginShutdownWarning("Get-off time reached", reason, _settings.WarningMinutes * 60);
        }
        else if (_settings.ShutdownAfterMatch)
        {
            BeginShutdownWarning(
                "Timer finished",
                $"{match.Label} — {DurationParser.FormatDuration(match.ExpectedSeconds)} planned.",
                _settings.WarningMinutes * 60);
        }
        else
        {
            Log($"Timer finished: {match.Label} — {DurationParser.FormatDuration(match.ExpectedSeconds)} planned.");
            Beep();
        }
    }

    private void UpdateOverlay(DateTimeOffset now)
    {
        if (_overlay is null)
        {
            return;
        }

        _overlay.Refresh(BuildSnapshot(now));

        if (!_settings.ShowOverlay)
        {
            _overlay.Hide();
            return;
        }

        if (!_settings.OverlayAutoGame || !_settings.OverlayClickThrough)
        {
            ShowOverlayIfHidden();
            return;
        }

        if ((DateTime.UtcNow - _lastAutoCheck).TotalMilliseconds >= 450)
        {
            _lastAutoCheck = DateTime.UtcNow;
            _overlayIsGame = ForegroundGameDetector.IsFullscreenGame(out _);
        }

        if (_overlayIsGame)
        {
            ShowOverlayIfHidden();
        }
        else
        {
            _overlay.Hide();
        }
    }

    private void ShowOverlayIfHidden()
    {
        if (_overlay is not null && !_overlay.IsVisible)
        {
            _overlay.Show();
        }
    }

    private OverlaySnapshot BuildSnapshot(DateTimeOffset now)
    {
        if (_active is null)
        {
            return new OverlaySnapshot(
                now,
                _cutoff,
                null,
                null,
                0,
                false,
                null,
                !string.IsNullOrEmpty(_pendingShutdownReason));
        }

        var end = _active.Start.AddSeconds(_active.ExpectedSeconds);
        var remaining = (end - now).TotalSeconds;
        return new OverlaySnapshot(
            now,
            _cutoff,
            _active.Label,
            end,
            remaining,
            remaining <= 0,
            _active.Map,
            !string.IsNullOrEmpty(_pendingShutdownReason));
    }

    private void HandleEvent(MatchEvent evt)
    {
        switch (evt)
        {
            case MatchStartEvent start:
                OnMatchStart(start);
                break;
            case MatchEndEvent end:
                OnMatchEnd(end);
                break;
            case PhaseEvent phase:
                OnPhase(phase);
                break;
            case StatusEvent status:
                Log(status.Text);
                break;
        }
    }

    private void OnMatchStart(MatchStartEvent evt)
    {
        var key = GameModes.KeyForBroad(evt.Broad, _selectedMode);
        var predicted = PredictionFor(key);

        if (_active is { Source: "auto" } auto && Math.Abs((auto.Start - evt.Start).TotalSeconds) < 10)
        {
            return;
        }

        if (_active is { Source: "manual" })
        {
            Log("Auto-detect took over the manual timer.");
        }

        StartMatch(key, predicted, "auto", evt.Start, evt.Map, evt.Broad, evt.Resumed);
    }

    private void OnMatchEnd(MatchEndEvent evt)
    {
        var key = _active?.ModeKey ?? GameModes.KeyForBroad(evt.Broad, _selectedMode);
        var expected = _active?.ExpectedSeconds ?? PredictionFor(key);
        var label = GameModes.Get(key).Label;
        var recorded = false;

        if (_active is { Source: "auto" } && key != "custom")
        {
            _history.Add(key, evt.DurationSeconds, evt.Map, evt.Broad);
            recorded = true;
        }

        _active = null;
        MatchStatusText.Text = "Not in a match";

        var difference = evt.DurationSeconds - expected;
        var sign = difference >= 0 ? "+" : "-";
        var mapName = string.IsNullOrWhiteSpace(evt.Map) ? "map" : evt.Map;
        var detail = $"{label} on {mapName}\nPlayed {DurationParser.FormatDuration(evt.DurationSeconds)} · predicted {DurationParser.FormatDuration(expected)} ({sign}{DurationParser.FormatDuration(Math.Abs(difference))})";

        Log($"Match ended: {label} on {mapName} — actual {DurationParser.FormatDuration(evt.DurationSeconds)}, predicted {DurationParser.FormatDuration(expected)}.");
        if (recorded)
        {
            Log("Saved to prediction history.");
        }

        MatchText.Text = DurationParser.FormatDuration(evt.DurationSeconds);
        MatchText.Foreground = _greenBrush;
        MatchPredictionText.Text = $"Last match: {label} · {mapName} · {DurationParser.FormatDuration(evt.DurationSeconds)}";
        MatchProgress.Value = 1000;
        UpdatePrediction();
        Beep();

        if (!string.IsNullOrEmpty(_pendingShutdownReason))
        {
            var reason = _pendingShutdownReason;
            _pendingShutdownReason = null;
            Log("Get-off time already reached. Shutdown warning next.");
            BeginShutdownWarning("Get-off time reached", reason, _settings.WarningMinutes * 60);
        }
        else if (_settings.ShutdownAfterMatch)
        {
            BeginShutdownWarning($"{label} ended", detail, _settings.WarningMinutes * 60);
        }
    }

    private void OnPhase(PhaseEvent evt)
    {
        switch (evt.Phase.ToLowerInvariant())
        {
            case "pregame":
                SetStatus("Agent select", _warnBrush);
                break;
            case "ingame":
                SetStatus("In match", _greenBrush);
                break;
            case "mainmenu":
            case "transitiontomainmenu":
                SetStatus("Watching for Valorant", _mutedBrush);
                break;
            case "initialization":
                SetStatus("Valorant starting", _mutedBrush);
                break;
        }
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
    }

    private void BeginShutdownWarning(string title, string detail, int seconds)
    {
        if (_warning is not null)
        {
            return;
        }

        var warning = new ShutdownWarningWindow(
            title,
            detail,
            seconds,
            onCancel: () =>
            {
                Log("Shutdown cancelled. You can keep playing.");
                Beep();
            },
            onShutdown: ExecuteShutdown);

        warning.Owner = this;
        _warning = warning;
        warning.Closed += (_, _) =>
        {
            if (ReferenceEquals(_warning, warning))
            {
                _warning = null;
            }
        };

        warning.Show();
        Log($"Shutdown warning: {title}. {Math.Max(1, seconds / 60)} minute(s) to cancel.");
        Beep();
    }

    private void ExecuteShutdown()
    {
        Log("Shutting down the PC now.");
        SaveSettings();

        try
        {
            ShutdownExecutor.ShutdownNow();
        }
        catch (Exception ex)
        {
            Log($"Shutdown failed: {ex.Message}");
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void RestartWatcher()
    {
        _watcher?.Stop();
        _watcher = new LogWatcher(_settings.EffectiveLogPath);
        _watcher.EventReceived += evt => Dispatcher.Invoke(() => HandleEvent(evt));
        _watcher.Start();
        Log($"Watching {_settings.EffectiveLogPath}");
    }

    public void ApplySettings()
    {
        _settings.Clamp();

        if (_settings.ShowOverlay)
        {
            EnsureOverlay();
            _overlay?.ApplySettings();
        }
        else
        {
            _overlay?.Hide();
        }

        SaveSettings();
    }

    public void RestartLogWatcher() => RestartWatcher();

    public void SaveSettings()
    {
        _settings.SelectedMode = _selectedMode;
        _settings.CustomDuration = CustomDurationBox.Text.Trim();
        _settings.CutoffIso = _cutoff?.ToString("o");
        SettingsStore.Save(_settings);
    }

    public void LogMessage(string message) => Log(message);

    public void ClearHistoryAndRefresh()
    {
        _history.Clear();
        UpdatePrediction();
        Log("Prediction history cleared.");
    }

    public void SetStartWithWindows(bool enabled)
    {
        var (ok, message) = StartupManager.SetEnabled(enabled);
        _settings.StartWithWindows = ok ? enabled : !enabled;
        Log(ok ? message : $"Startup change failed: {message}");
        SettingsStore.Save(_settings);
    }

    public void RequestManualShutdown()
    {
        BeginShutdownWarning(
            "Manual shutdown",
            $"You chose to shut down in {_settings.WarningMinutes:00}:00.",
            _settings.WarningMinutes * 60);
    }

    public void RequestTestWarning()
    {
        BeginShutdownWarning(
            "Test warning",
            "This is a test. Nothing happens if you cancel.",
            10);
    }

    public void CancelPendingShutdown()
    {
        if (!string.IsNullOrEmpty(_pendingShutdownReason))
        {
            _pendingShutdownReason = null;
            Log("Pending shutdown canceled.");
        }
    }

    public bool HasPendingShutdown => !string.IsNullOrEmpty(_pendingShutdownReason);

    private void EnsureOverlay()
    {
        if (_overlay is { IsLoaded: true })
        {
            return;
        }

        _overlay = new OverlayWindow(this, _settings);
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
        _overlay.ApplySettings();
    }

    private void Log(string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        ActivityLog.Add(line);
        while (ActivityLog.Count > 200)
        {
            ActivityLog.RemoveAt(0);
        }
        ActivityText.Text = line;
    }

    private static void Beep()
    {
        try
        {
            SystemSounds.Exclamation.Play();
        }
        catch
        {
            // Sound is optional.
        }
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    public void ShowOverlayForTest()
    {
        _settings.ShowOverlay = true;
        _settings.OverlayAutoGame = false;
        EnsureOverlay();
        _overlay?.ApplySettings();
        _overlay?.Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void SetCutoff_Click(object sender, RoutedEventArgs e) => SetCutoff();

    private void Quick30_Click(object sender, RoutedEventArgs e) => QuickCutoff(30);

    private void Quick60_Click(object sender, RoutedEventArgs e) => QuickCutoff(60);

    private void ClearCutoff_Click(object sender, RoutedEventArgs e) => ClearCutoff();

    private void ClockBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SetCutoff();
        }
    }

    private void CustomDurationBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StartSelected();
        }
    }

    private void StartTimer_Click(object sender, RoutedEventArgs e) => StartSelected();

    private void StopTimer_Click(object sender, RoutedEventArgs e) => StopActiveTimer();

    private void ClearStats_Click(object sender, RoutedEventArgs e) => ClearStats();

    private void ManualShutdown_Click(object sender, RoutedEventArgs e) => RequestManualShutdown();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _tickTimer.Stop();
        SaveSettings();
        _watcher?.Stop();
        _overlay?.Close();
        _settingsWindow?.Close();
        _warning?.Close();
    }
}
