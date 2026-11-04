using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ValorantMatchTimer.Core;
using ValorantMatchTimer.UI;

namespace ValorantMatchTimer;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _owner;
    private readonly AppSettings _settings;
    private bool _loading = true;

    public SettingsWindow(MainWindow owner)
    {
        InitializeComponent();
        WindowTheme.EnableDarkTitleBar(this);

        _owner = owner;
        _settings = owner.Settings;
        Owner = owner;

        LoadSettingsIntoUi();
        ActivityList.ItemsSource = _owner.ActivityLog;

        _loading = false;
        ShowPage("General");
        UpdatePreview();

        Closing += (_, _) =>
        {
            if (!_loading)
            {
                ApplyWarningMinutes(silent: true);
                _owner.ApplySettings();
            }
        };
    }

    private void LoadSettingsIntoUi()
    {
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        LogPathBox.Text = _settings.EffectiveLogPath;

        OverlayEnabledCheck.IsChecked = _settings.ShowOverlay;
        OverlayAutoGameCheck.IsChecked = _settings.OverlayAutoGame;
        OverlayClickThroughCheck.IsChecked = _settings.OverlayClickThrough;
        OpacitySlider.Value = _settings.OverlayOpacity;
        ScaleSlider.Value = _settings.OverlayScale;
        OpacityValueText.Text = $"{_settings.OverlayOpacity:P0}";
        ScaleValueText.Text = $"{_settings.OverlayScale:0.00}x";

        ShowTimeCheck.IsChecked = _settings.OverlayShowTime;
        ShowMatchCheck.IsChecked = _settings.OverlayShowMatch;
        ShowEndCheck.IsChecked = _settings.OverlayShowEnd;
        ShowClockCheck.IsChecked = _settings.OverlayShowClock;

        AccentBox.Text = _settings.OverlayAccent;
        SetPosition(_settings.OverlayPosition);
        PosXBox.Text = _settings.OverlayPos is { Length: 2 }
            ? Math.Round(_settings.OverlayPos[0]).ToString(CultureInfo.InvariantCulture)
            : "1200";
        PosYBox.Text = _settings.OverlayPos is { Length: 2 }
            ? Math.Round(_settings.OverlayPos[1]).ToString(CultureInfo.InvariantCulture)
            : "40";
        UpdateCustomControls();

        ShutdownAfterMatchCheck.IsChecked = _settings.ShutdownAfterMatch;
        DeferShutdownCheck.IsChecked = _settings.DeferShutdownInMatch;
        WarningMinutesBox.Text = _settings.WarningMinutes.ToString(CultureInfo.InvariantCulture);
    }

    private void ShowPage(string page)
    {
        if (PageGeneral is null)
        {
            return;
        }

        PageGeneral.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageOverlay.Visibility = page == "Overlay" ? Visibility.Visible : Visibility.Collapsed;
        PageShutdown.Visibility = page == "Shutdown" ? Visibility.Visible : Visibility.Collapsed;
        PageActivity.Visibility = page == "Activity" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string page })
        {
            return;
        }
        ShowPage(page);
    }

    private void SetPosition(string position)
    {
        foreach (var radio in PositionPanel.Children.OfType<RadioButton>())
        {
            if (string.Equals(radio.Tag as string, position, StringComparison.OrdinalIgnoreCase))
            {
                radio.IsChecked = true;
                return;
            }
        }
    }

    private void UpdateCustomControls()
    {
        var custom = string.Equals(_settings.OverlayPosition, "custom", StringComparison.OrdinalIgnoreCase);
        PosXBox.IsEnabled = custom;
        PosYBox.IsEnabled = custom;
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _owner.SetStartWithWindows(StartWithWindowsCheck.IsChecked == true);
        _loading = true;
        StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
        _loading = false;
        Saved();
    }

    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Log files (*.log)|*.log|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            LogPathBox.Text = dialog.FileName;
            ApplyLogPath();
        }
    }

    private void LogPathBox_LostFocus(object sender, RoutedEventArgs e) => ApplyLogPath();

    private void ApplyLogPath()
    {
        if (_loading)
        {
            return;
        }

        var path = LogPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            LogPathBox.Text = _settings.EffectiveLogPath;
            return;
        }

        if (!string.Equals(path, _settings.EffectiveLogPath, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LogPath = path;
            SettingsStore.Save(_settings);
            _owner.RestartLogWatcher();
            Saved();
        }
    }

    private void OverlayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ShowOverlay = OverlayEnabledCheck.IsChecked == true;
        _settings.OverlayAutoGame = OverlayAutoGameCheck.IsChecked == true;
        _settings.OverlayClickThrough = OverlayClickThroughCheck.IsChecked == true;
        _settings.OverlayShowTime = ShowTimeCheck.IsChecked == true;
        _settings.OverlayShowMatch = ShowMatchCheck.IsChecked == true;
        _settings.OverlayShowEnd = ShowEndCheck.IsChecked == true;
        _settings.OverlayShowClock = ShowClockCheck.IsChecked == true;

        _owner.ApplySettings();
        UpdatePreview();
        Saved();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.OverlayOpacity = Math.Round(OpacitySlider.Value, 2);
        OpacityValueText.Text = $"{_settings.OverlayOpacity:P0}";
        _owner.ApplySettings();
        Saved();
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.OverlayScale = Math.Round(ScaleSlider.Value, 2);
        ScaleValueText.Text = $"{_settings.OverlayScale:0.00}x";
        _owner.ApplySettings();
        Saved();
    }

    private void Position_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string position })
        {
            return;
        }

        _settings.OverlayPosition = position;
        if (position == "custom"
            && double.TryParse(PosXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(PosYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            _settings.OverlayPos = new[] { x, y };
        }

        UpdateCustomControls();
        _owner.ApplySettings();
        UpdatePreview();
        Saved();
    }

    private void ApplyPosition_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(PosXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(PosYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            SavedText.Text = "Enter a valid X and Y position.";
            return;
        }

        _settings.OverlayPosition = "custom";
        _settings.OverlayPos = new[] { x, y };

        _loading = true;
        SetPosition("custom");
        _loading = false;

        UpdateCustomControls();
        _owner.ApplySettings();
        UpdatePreview();
        Saved();
    }

    private void AccentPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string accent })
        {
            AccentBox.Text = accent;
            ApplyAccent();
        }
    }

    private void AccentApply_Click(object sender, RoutedEventArgs e) => ApplyAccent();

    private void ApplyAccent()
    {
        var value = AccentBox.Text.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        try
        {
            _ = (Color)ColorConverter.ConvertFromString(value)!;
            _settings.OverlayAccent = value.ToUpperInvariant();
            AccentBox.Text = _settings.OverlayAccent;
            _owner.ApplySettings();
            UpdatePreview();
            Saved();
        }
        catch
        {
            AccentBox.Text = _settings.OverlayAccent;
            SavedText.Text = "Enter a valid hex color, e.g. #FF4655.";
        }
    }

    private void ShutdownToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ShutdownAfterMatch = ShutdownAfterMatchCheck.IsChecked == true;
        _settings.DeferShutdownInMatch = DeferShutdownCheck.IsChecked == true;
        SettingsStore.Save(_settings);
        Saved();
    }

    private void WarningMinutes_LostFocus(object sender, RoutedEventArgs e) => ApplyWarningMinutes();

    private void ApplyWarningMinutes(bool silent = false)
    {
        if (_loading)
        {
            return;
        }

        if (!int.TryParse(WarningMinutesBox.Text, out var minutes))
        {
            WarningMinutesBox.Text = _settings.WarningMinutes.ToString(CultureInfo.InvariantCulture);
            return;
        }

        minutes = Math.Clamp(minutes, 1, 15);
        WarningMinutesBox.Text = minutes.ToString(CultureInfo.InvariantCulture);
        _settings.WarningMinutes = minutes;
        SettingsStore.Save(_settings);
        if (!silent)
        {
            Saved();
        }
    }

    private void TestWarning_Click(object sender, RoutedEventArgs e)
    {
        _owner.RequestTestWarning();
        Saved();
    }

    private void ShutdownNow_Click(object sender, RoutedEventArgs e)
    {
        _owner.RequestManualShutdown();
        Saved();
    }

    private void CancelPending_Click(object sender, RoutedEventArgs e)
    {
        _owner.CancelPendingShutdown();
        Saved();
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e) => _owner.ActivityLog.Clear();

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _owner.ClearHistoryAndRefresh();
        Saved();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdatePreview()
    {
        PreviewBorder.BorderBrush = ParseBrush(_settings.OverlayAccent);

        if (_owner.Cutoff is { } cutoff)
        {
            var remaining = (cutoff - DateTimeOffset.Now).TotalSeconds;
            PreviewTime.Text = remaining > 0 ? DurationParser.FormatHms(remaining) : "00:00:00";
            PreviewSub.Text = $"Get off at {cutoff:HH:mm}";
            PreviewSub.Visibility = _settings.OverlayShowTime ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            PreviewTime.Text = "SET TIME";
            PreviewSub.Text = "No get-off time set";
            PreviewSub.Visibility = _settings.OverlayShowTime ? Visibility.Visible : Visibility.Collapsed;
        }

        PreviewMatch.Text = _settings.OverlayShowMatch ? "Competitive · ends 21:40" : "";
        PreviewMatch.Visibility = _settings.OverlayShowMatch ? Visibility.Visible : Visibility.Collapsed;
        PreviewTime.Visibility = _settings.OverlayShowTime ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Saved()
    {
        SavedText.Text = "Saved.";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SavedText.Text = "Changes apply immediately.";
        };
        timer.Start();
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
