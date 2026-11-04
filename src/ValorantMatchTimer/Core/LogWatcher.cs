using System.IO;
using System.Text;

namespace ValorantMatchTimer.Core;

public sealed class LogWatcher : IDisposable
{
    private const long TailBytes = 8 * 1024 * 1024;

    private readonly string _path;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private MatchParser _parser = new();
    private string _buffer = "";
    private long _position;
    private bool _tailDone;
    private bool _missingLogged;
    private DateTime _lastCheck = DateTime.MinValue;

    public event Action<MatchEvent>? EventReceived;

    public LogWatcher(string path)
    {
        _path = path;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Valorant log watcher"
        };
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _stopping = true;
        if (_thread.IsAlive)
        {
            _thread.Join(2000);
        }
    }

    private void Emit(MatchEvent evt) => EventReceived?.Invoke(evt);

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (!_missingLogged)
                    {
                        _missingLogged = true;
                        Emit(new StatusEvent("Valorant log not found. Manual timers still work."));
                    }
                    Sleep(5000);
                    continue;
                }

                if (!_tailDone)
                {
                    _tailDone = true;
                    TailScan();
                    continue;
                }

                var size = new FileInfo(_path).Length;
                if (size < _position)
                {
                    _parser = new MatchParser();
                    _position = 0;
                    _buffer = "";
                    TailScan();
                    continue;
                }

                if (size > _position)
                {
                    ReadNew();
                }
                else
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastCheck).TotalSeconds > 30)
                    {
                        _lastCheck = now;
                        if (_parser.InMatch && _parser.Last.HasValue)
                        {
                            var idle = (DateTimeOffset.Now - _parser.Last.Value).TotalSeconds;
                            if (idle > 600)
                            {
                                foreach (var evt in _parser.ForceEnd(_parser.Last))
                                {
                                    Emit(evt);
                                }
                            }
                        }
                    }
                    Sleep(400);
                }
            }
            catch
            {
                Sleep(1000);
            }
        }
    }

    private void Sleep(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!_stopping && DateTime.UtcNow < end)
        {
            Thread.Sleep(Math.Min(200, Math.Max(1, (int)(end - DateTime.UtcNow).TotalMilliseconds)));
        }
    }

    private void TailScan()
    {
        FileInfo info;
        try
        {
            info = new FileInfo(_path);
        }
        catch
        {
            return;
        }

        _position = info.Length;
        var recent = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalMinutes < 5;
        if (!recent)
        {
            return;
        }

        var startPosition = Math.Max(0, info.Length - TailBytes);
        string data;
        try
        {
            using var stream = OpenRead();
            stream.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: false);
            data = reader.ReadToEnd();
        }
        catch
        {
            return;
        }

        if (startPosition > 0)
        {
            var newline = data.IndexOf('\n');
            data = newline >= 0 ? data[(newline + 1)..] : "";
        }

        foreach (var line in data.Split('\n'))
        {
            _parser.Feed(line.TrimEnd('\r'), emit: false);
        }

        if (_parser.InMatch && _parser.Start.HasValue)
        {
            Emit(new MatchStartEvent(
                _parser.Start.Value,
                _parser.MatchMap,
                _parser.MatchBroad,
                Resumed: true));
        }
    }

    private void ReadNew()
    {
        string data;
        using (var stream = OpenRead())
        {
            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: false);
            data = reader.ReadToEnd();
            _position = stream.Position;
        }

        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        _buffer += data;
        if (!_buffer.Contains('\n'))
        {
            return;
        }

        var parts = _buffer.Split('\n');
        _buffer = parts[^1];
        for (var i = 0; i < parts.Length - 1; i++)
        {
            foreach (var evt in _parser.Feed(parts[i].TrimEnd('\r')))
            {
                Emit(evt);
            }
        }
    }

    private FileStream OpenRead() => new(
        _path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    public void Dispose() => Stop();
}
