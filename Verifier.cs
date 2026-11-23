using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace JFSI_MissingMusic_Downloader;

/// <summary>
///     Checks a downloaded mp3 against what Spotify says the track is, using the YouTube title yt-dlp embedded in
///     the file. Search-time matching can be fooled (e.g. a fan megamix of the right length), so this is the
///     safety net: a file that doesn't fit is deleted rather than left in the library.
/// </summary>
internal static class Verifier
{
    public record Result(bool Ok, string Reason, string YouTubeTitle, string Url);

    /// <param name="expectedSeconds">What the file's length should be.</param>
    /// <param name="toleranceSeconds">Allowed difference (mp3 conversion adds ~1s of slack).</param>
    /// <param name="checkTitle">False for trims you confirmed yourself: only the length is checked.</param>
    public static Result Check(string path, TrackObj track, double expectedSeconds, double toleranceSeconds, bool checkTitle)
    {
        var info = Probe(path);
        if (info == null) return new Result(true, "ffprobe unavailable, not verified", "", ""); // never delete on doubt

        var (duration, title, url) = info.Value;

        if (expectedSeconds > 0 && Math.Abs(duration - expectedSeconds) > toleranceSeconds)
            return new Result(false, $"length {duration:F0}s but expected {expectedSeconds:F0}s", title, url);

        if (checkTitle && title.Length > 0)
        {
            var problem = TrackMatcher.TitleProblem(track, title);
            if (problem != null) return new Result(false, problem, title, url);
        }

        return new Result(true, "", title, url);
    }

    private static (double Duration, string Title, string Url)? Probe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var a in new[]
                     {
                         "-v", "error", "-show_entries", "format=duration:format_tags=title,purl", "-of", "json", path
                     })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();

            var format = JObject.Parse(output)["format"];
            if (format == null) return null;
            var duration = (double?)format["duration"] ?? 0;
            var tags = format["tags"];
            return (duration, (string?)tags?["title"] ?? "", (string?)tags?["purl"] ?? "");
        }
        catch
        {
            return null;
        }
    }
}
