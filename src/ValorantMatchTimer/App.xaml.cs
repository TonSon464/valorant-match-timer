using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ValorantMatchTimer.Core;

namespace ValorantMatchTimer;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(arg => arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest(e.Args);
            Shutdown();
            return;
        }

        _mutex = new Mutex(true, @"Local\ValorantMatchTimer", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Valorant Match Timer is already running.",
                "Valorant Match Timer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (e.Args.Any(arg => arg.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            window.OpenSettings();
        }

        if (e.Args.Any(arg => arg.Equals("--overlay", StringComparison.OrdinalIgnoreCase)))
        {
            window.ShowOverlayForTest();
        }

        if (e.Args.Any(arg => arg.Equals("--warning", StringComparison.OrdinalIgnoreCase)))
        {
            window.RequestTestWarning();
        }

        if (e.Args.Any(arg => arg.Equals("--smoke", StringComparison.OrdinalIgnoreCase)))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Shutdown();
            };
            timer.Start();
        }
    }

    private static void RunSelfTest(string[] args)
    {
        var logPath = AppPaths.DefaultLogPath;
        var logIndex = Array.FindIndex(args, arg => arg.Equals("--log", StringComparison.OrdinalIgnoreCase));
        if (logIndex >= 0 && logIndex + 1 < args.Length)
        {
            logPath = args[logIndex + 1];
        }

        var outputPath = Path.Combine(Path.GetTempPath(), "valorant-match-timer-selftest.txt");
        var output = new StringBuilder();

        if (!File.Exists(logPath))
        {
            output.AppendLine($"Log not found: {logPath}");
        }
        else
        {
            var parser = new MatchParser();
            MatchStartEvent? open = null;
            var count = 0;

            foreach (var line in File.ReadLines(logPath, Encoding.UTF8))
            {
                foreach (var evt in parser.Feed(line))
                {
                    switch (evt)
                    {
                        case MatchStartEvent start:
                            open = start;
                            break;
                        case MatchEndEvent end:
                            count++;
                            output.AppendLine(
                                $"{count,2}. {end.Start:yyyy-MM-dd HH:mm:ss} -> {end.End:HH:mm:ss}  " +
                                $"{DurationParser.FormatDuration(end.DurationSeconds),9}  " +
                                $"{(string.IsNullOrWhiteSpace(end.Map) ? "?" : end.Map),-12} " +
                                $"{GameModes.ShortName(end.Broad)}");
                            open = null;
                            break;
                    }
                }
            }

            if (open is not null)
            {
                output.AppendLine(
                    $"OPEN match started {open.Start:yyyy-MM-dd HH:mm:ss} on " +
                    $"{open.Map} ({GameModes.ShortName(open.Broad)})");
            }

            output.AppendLine($"Total closed matches: {count}");
        }

        output.AppendLine($"Output: {outputPath}");
        File.WriteAllText(outputPath, output.ToString());

        if (!args.Any(arg => arg.Equals("--quiet", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                output.ToString(),
                "Valorant Match Timer self test",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // The mutex may have already been released.
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDir);
            File.AppendAllText(
                Path.Combine(AppPaths.AppDir, "error.log"),
                $"[{DateTimeOffset.Now:o}] {e.Exception}\n\n");
        }
        catch
        {
            // Never fail while reporting a failure.
        }

        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}",
            "Valorant Match Timer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
