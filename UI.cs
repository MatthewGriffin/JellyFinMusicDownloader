using System.Diagnostics;
using Timer = System.Threading.Timer;

namespace JFSI_MissingMusic_Downloader;

internal enum Outcome { Downloaded, AlreadyHad, NoMatch, NeedsTrim, Failed }

/// <summary>
///     Console output that keeps a single progress bar pinned to the bottom line.
///     All output must go through <see cref="WriteLine" /> so log lines scroll above the bar.
/// </summary>
internal static class UI
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = new();
    private static Timer? _timer;
    private static bool _enabled;

    private static int _total, _done, _active, _downloaded, _already, _noMatch, _trim, _failed;
    private static string _playlist = "";

    public static void Start(int totalTracks)
    {
        lock (Gate)
        {
            _total = totalTracks;
            Clock.Restart();
            _enabled = !Console.IsOutputRedirected && totalTracks > 0;
            if (!_enabled) return;
            Draw();
        }

        _timer = new Timer(_ => { lock (Gate) { if (_enabled) Draw(); } }, null, 1000, 1000); // keeps ETA/clock moving
    }

    public static void Stop()
    {
        _timer?.Dispose();
        lock (Gate)
        {
            if (!_enabled) return;
            Draw();
            Console.WriteLine();
            _enabled = false;
        }
    }

    public static void SetPlaylist(string name)
    {
        lock (Gate)
        {
            _playlist = name;
            if (_enabled) Draw();
        }
    }

    public static void TrackStart()
    {
        lock (Gate)
        {
            _active++;
            if (_enabled) Draw();
        }
    }

    public static void TrackEnd(Outcome outcome)
    {
        lock (Gate)
        {
            _active--;
            _done++;
            switch (outcome)
            {
                case Outcome.Downloaded: _downloaded++; break;
                case Outcome.AlreadyHad: _already++; break;
                case Outcome.NoMatch: _noMatch++; break;
                case Outcome.NeedsTrim: _trim++; break;
                default: _failed++; break;
            }

            if (_enabled) Draw();
        }
    }

    public static void WriteLine(string format, params object?[] args)
    {
        var msg = args.Length == 0 ? format : string.Format(format, args);
        lock (Gate)
        {
            if (_enabled) Console.Write("\r\e[2K"); // wipe the bar line
            Console.WriteLine(msg);
            if (_enabled) Draw();                   // redraw bar beneath the new line
        }
    }

    private static void Draw() => Console.Write("\r\e[2K" + Render());

    private static string Render()
    {
        int width;
        try { width = Console.WindowWidth; } catch { width = 100; }
        if (width < 40) width = 40;

        var pct = _total == 0 ? 0 : (double)_done / _total;
        var worked = _downloaded + _noMatch + _trim + _failed; // items that took real time (not instant skips)
        var eta = "";
        if (worked > 0 && _done < _total)
        {
            var remaining = TimeSpan.FromSeconds(Clock.Elapsed.TotalSeconds / worked * (_total - _done));
            eta = " ETA ~" + Fmt(remaining);
        }

        var stats = $" {_done}/{_total} {pct:P0} | ok {_downloaded} had {_already} miss {_noMatch} trim {_trim} err {_failed} | " +
                    $"active {_active} | {Fmt(Clock.Elapsed)}{eta}";
        var tail = _playlist.Length > 0 ? $" | {_playlist}" : "";

        var barWidth = Math.Clamp(width - stats.Length - 4, 8, 40);
        var filled = (int)Math.Round(pct * barWidth);
        var bar = "[" + new string('#', filled) + new string('-', barWidth - filled) + "]";

        var line = bar + stats + tail;
        return line.Length >= width ? line[..(width - 1)] : line;
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m" : $"{t.Minutes}m{t.Seconds:D2}s";
}
