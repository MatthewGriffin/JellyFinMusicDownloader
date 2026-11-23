using System.Collections.Concurrent;
using System.Globalization;
using Newtonsoft.Json;

namespace JFSI_MissingMusic_Downloader;

/// <summary>Timings you gave for a track. Skip=true means "never ask me about this one again".</summary>
internal record TrimEntry(string VideoId, double Start, double End, bool Skip);

internal record PendingTrim(TrackObj Track, Candidate Candidate);

/// <summary>
///     Tracks that only exist on YouTube as a longer video (music video with intro/outro, etc.).
///     Nothing is ever trimmed on a guess: they are collected during the run, then you are asked for the
///     start/end times once the downloads finish. Your answers are saved to trims.json and reused.
/// </summary>
internal static class Trims
{
    private const string StorePath = "/run/media/system/SSD3/JFSI_MissingMusic_Downloader/MissingJsons/trims.json";
    private const double LengthWarnSeconds = 3; // warn if your range differs from Spotify's length by more than this

    private static readonly object Gate = new();
    private static Dictionary<string, TrimEntry> _entries = Load();

    public static readonly ConcurrentDictionary<string, PendingTrim> Pending = new();

    public static TrimEntry? Get(string key)
    {
        lock (Gate) return _entries.GetValueOrDefault(key);
    }

    private static void Set(string key, TrimEntry entry)
    {
        lock (Gate)
        {
            _entries[key] = entry;
            File.WriteAllText(StorePath, JsonConvert.SerializeObject(_entries, Formatting.Indented));
        }
    }

    private static Dictionary<string, TrimEntry> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonConvert.DeserializeObject<Dictionary<string, TrimEntry>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    // ---- interactive part (runs after the progress bar has stopped) ----

    public static void ResolvePending()
    {
        if (Pending.IsEmpty) return;

        Console.WriteLine();
        Console.WriteLine("=== {0} track(s) only exist as a longer video and need trimming ===", Pending.Count);
        Console.WriteLine("Open each link, find where the song starts and ends, and enter the times.");
        Console.WriteLine("Times can be seconds (41), m:ss (0:41) or h:mm:ss. Nothing is trimmed unless you confirm.");

        var n = 0;
        foreach (var p in Pending.Values.OrderBy(p => p.Track.name))
        {
            n++;
            if (!Ask(p, n, Pending.Count)) break; // stdin closed
        }
    }

    /// <returns>false if input ended and we should stop asking.</returns>
    private static bool Ask(PendingTrim p, int n, int total)
    {
        var key = Program.TrackKey(p.Track);
        var title = p.Track.name.RemoveChars();
        var artist = p.Track.artist.RemoveChars();
        var spotify = (p.Track.duration_ms ?? 0) / 1000.0;
        var video = p.Candidate.Duration ?? 0;

        Console.WriteLine();
        Console.WriteLine("[{0}/{1}] {2} - {3}", n, total, p.Track.name, p.Track.artist);
        Console.WriteLine("  Spotify length : {0} ({1:F0}s)", Fmt(spotify), spotify);
        Console.WriteLine("  Found video    : {0} | {1} ({2})", p.Candidate.Channel, p.Candidate.Title, Fmt(video));
        Console.WriteLine("  Link           : https://www.youtube.com/watch?v={0}", p.Candidate.Id);

        while (true)
        {
            Console.Write("  Start time (Enter = skip for now, x = never ask again): ");
            var s = Console.ReadLine();
            if (s == null) return false;
            s = s.Trim();
            if (s.Length == 0) return true;
            if (s.Equals("x", StringComparison.OrdinalIgnoreCase))
            {
                Set(key, new TrimEntry(p.Candidate.Id, 0, 0, true));
                Console.WriteLine("  OK, won't ask about this track again (edit trims.json to undo).");
                return true;
            }

            var start = ParseTime(s);
            if (start == null || start < 0 || start >= video)
            {
                Console.WriteLine("  Couldn't read that start time (video is {0} long). Try again.", Fmt(video));
                continue;
            }

            var suggested = Math.Min(start.Value + spotify, video);
            Console.Write("  End time [Enter = {0}, start + Spotify's length]: ", Fmt(suggested));
            var e = Console.ReadLine();
            if (e == null) return false;
            e = e.Trim();
            var end = e.Length == 0 ? suggested : ParseTime(e);
            if (end == null || end <= start || end > video + 1)
            {
                Console.WriteLine("  Couldn't read that end time (must be after the start, within {0}). Start again.", Fmt(video));
                continue;
            }

            var length = end.Value - start.Value;
            Console.WriteLine("  Selected: {0} -> {1} = {2} long (Spotify: {3})", Fmt(start.Value), Fmt(end.Value), Fmt(length), Fmt(spotify));
            if (spotify > 0 && Math.Abs(length - spotify) > LengthWarnSeconds)
                Console.WriteLine("  WARNING: that differs from Spotify's length by {0:F0}s, so it may include intro/outro or cut the song short.",
                    Math.Abs(length - spotify));

            Console.Write("  Download this range? [y/N]: ");
            var ok = Console.ReadLine();
            if (ok == null) return false;
            if (!ok.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Not downloaded. Enter new times, or press Enter at the start prompt to skip.");
                continue;
            }

            Console.WriteLine("  Downloading and trimming...");
            if (Program.DownloadAudio(p.Track, p.Candidate.Id, title, artist, start, end))
            {
                Set(key, new TrimEntry(p.Candidate.Id, start.Value, end.Value, false));
                Console.WriteLine("  Saved. These timings are remembered in trims.json.");
                return true;
            }

            Console.WriteLine("  Download failed. Try again, or press Enter at the start prompt to skip.");
        }
    }

    public static double? ParseTime(string s)
    {
        var parts = s.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
            total = total * 60 + v;
        }

        return total;
    }

    public static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }
}
