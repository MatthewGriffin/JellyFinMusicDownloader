using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace JFSI_MissingMusic_Downloader;

internal record PendingPick(TrackObj Track, List<Candidate> Candidates);

/// <summary>
///     Tracks with no confident match. During the run the search results are kept; afterwards you choose one
///     yourself (or paste a YouTube link). If the video is longer than the song you're taken through the same
///     start/end prompts as a normal trim. Choices are remembered in trims.json.
/// </summary>
internal static class Picker
{
    public static readonly ConcurrentDictionary<string, PendingPick> Pending = new(StringComparer.OrdinalIgnoreCase);

    private const int ShowMax = 8;
    private const double SameLengthSeconds = 6; // same tolerance the matcher uses

    public static void ResolveAll()
    {
        if (Pending.IsEmpty) return;

        Console.WriteLine();
        Console.WriteLine("=== {0} track(s) had no confident match - choose one yourself ===", Pending.Count);
        Console.WriteLine("Pick a number, paste your own YouTube link with u, or press Enter to skip a track.");
        Console.WriteLine("Skipped tracks stay in unmatched.log and are not searched again unless you run with --retry-unmatched.");

        var n = 0;
        foreach (var p in Pending.Values.OrderBy(p => p.Track.name))
        {
            n++;
            if (!Ask(p, n, Pending.Count)) break; // stdin closed
        }
    }

    /// <returns>false if input ended.</returns>
    private static bool Ask(PendingPick p, int n, int total)
    {
        var spotify = (p.Track.duration_ms ?? 0) / 1000.0;
        var options = p.Candidates.OrderByDescending(c => c.Score).Take(ShowMax).ToList();

        Console.WriteLine();
        Console.WriteLine("[{0}/{1}] {2} - {3}", n, total, p.Track.name, p.Track.artist);
        Console.WriteLine("  Spotify length: {0} ({1:F0}s)   album: {2}", Trims.Fmt(spotify), spotify, p.Track.album);

        if (options.Count == 0)
            Console.WriteLine("  No candidates were found.");
        for (var i = 0; i < options.Count; i++)
        {
            var c = options[i];
            var dur = c.Duration ?? 0;
            var diff = dur - spotify;
            var note = Math.Abs(diff) <= SameLengthSeconds ? "same length"
                : diff > 0 ? $"{diff:F0}s longer - needs trim"
                : $"{-diff:F0}s shorter";
            Console.WriteLine("  {0}) {1}  [{2}]  {3} | {4}", i + 1, Trims.Fmt(dur), note, c.Channel, c.Title);
            Console.WriteLine("       https://www.youtube.com/watch?v={0}", c.Id);
        }

        while (true)
        {
            Console.Write("  Choose {0}u = paste a link, Enter = skip: ", options.Count > 0 ? $"1-{options.Count}, " : "");
            var input = Console.ReadLine();
            if (input == null) return false;
            input = input.Trim();
            if (input.Length == 0) return true;

            string id, label;
            double videoSec;

            if (input.Equals("u", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("  YouTube link: ");
                var link = Console.ReadLine();
                if (link == null) return false;
                var parsed = ParseVideoId(link);
                if (parsed == null)
                {
                    Console.WriteLine("  That doesn't look like a YouTube link or video id.");
                    continue;
                }

                Console.WriteLine("  Looking up the video...");
                var info = Lookup(parsed);
                if (info == null)
                {
                    Console.WriteLine("  Couldn't read that video (private, removed, or yt-dlp failed).");
                    continue;
                }

                (id, videoSec, label) = (parsed, info.Value.Duration, info.Value.Label);
                Console.WriteLine("  Found: {0} ({1})", label, Trims.Fmt(videoSec));
            }
            else if (int.TryParse(input, out var pick) && pick >= 1 && pick <= options.Count)
            {
                var c = options[pick - 1];
                (id, videoSec, label) = (c.Id, c.Duration ?? 0, c.Channel + " | " + c.Title);
            }
            else
            {
                Console.WriteLine("  Enter a number from the list, u, or press Enter to skip.");
                continue;
            }

            switch (Handle(p.Track, id, videoSec, spotify))
            {
                case Step.Eof: return false;
                case Step.Done: return true;
                default: continue; // changed their mind: back to the list
            }
        }
    }

    private static Step Handle(TrackObj track, string id, double videoSec, double spotify)
    {
        var diff = videoSec - spotify;

        if (diff > SameLengthSeconds)
        {
            Console.WriteLine("  This video is {0:F0}s longer than the song, so it needs trimming.", diff);
            return Trims.PromptTrim(track, id, videoSec, false);
        }

        if (diff < -SameLengthSeconds)
            Console.WriteLine("  WARNING: this video is {0:F0}s SHORTER than the song. It is probably a different version or an edit.", -diff);

        Console.Write("  Download this video as it is? [y/N]: ");
        var ok = Console.ReadLine();
        if (ok == null) return Step.Eof;
        if (!ok.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)) return Step.Skipped;

        Console.WriteLine("  Downloading...");
        if (Program.DownloadAudio(track, id, userChosen: true))
        {
            Trims.Set(Program.TrackKey(track), new TrimEntry(id, 0, 0, false));
            Console.WriteLine("  Saved. This choice is remembered in trims.json.");
            return Step.Done;
        }

        Console.WriteLine("  Download failed.");
        return Step.Skipped;
    }

    // ---- helpers ----

    /// <summary>Accepts watch?v=ID, youtu.be/ID, /shorts/ID, /embed/ID or a bare 11-character id.</summary>
    internal static string? ParseVideoId(string input)
    {
        input = input.Trim();
        var m = Regex.Match(input, @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/)([A-Za-z0-9_-]{11})");
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(input, @"^[A-Za-z0-9_-]{11}$") ? input : null;
    }

    private static (double Duration, string Label)? Lookup(string id)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "yt-dlp", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[]
                     {
                         "https://www.youtube.com/watch?v=" + id, "--no-playlist", "--skip-download", "-q", "--no-warnings",
                         "--print", "%(duration)s\t%(channel)s\t%(title)s",
                         "--cookies-from-browser", "firefox", "--js-runtimes", "node", "--remote-components", "ejs:github"
                     })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            var parts = output.Trim().Split('\t');
            if (parts.Length < 3 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var dur)) return null;
            return (dur, parts[1] + " | " + parts[2]);
        }
        catch
        {
            return null;
        }
    }
}
