using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace JFSI_MissingMusic_Downloader;

public record Candidate(string Id, string Title, string Channel, double? Duration, double Score, bool Preferred,
    bool NeedsTrim = false);

/// <summary>Best = a download that fits as-is. TrimCandidate = only a longer video exists (needs your timings).</summary>
public record MatchResult(Candidate? Best, Candidate? TrimCandidate);

/// <summary>
///     Searches YouTube (metadata only) and picks the candidate that best matches the Spotify track,
///     instead of blindly taking the first search result.
/// </summary>
internal static class TrackMatcher
{
    private const int SearchCount = 10;
    private const double DurationToleranceSeconds = 6;   // hard limit when Spotify duration is known
    private const double MaxTrimExtraSeconds = 300;     // a longer video is only a trim candidate up to this much longer
    private const double MinScoreUnofficial = 55;
    private const double MinScore = 40;                  // below this we skip rather than download a wrong version

    // Words that mark a different version. Penalised only if the Spotify title/album does NOT contain them.
    private static readonly string[] BadWords =
    [
        "live", "concert", "tour", "karaoke", "cover", "remix", "sped up", "speed up", "slowed", "reverb",
        "nightcore", "instrumental", "acapella", "a cappella", "8d", "bass boosted", "reaction", "tutorial",
        "lyrics", "lyric video", "music video", "official video", "mashup", "mix", "full album", "playlist",
        "interview", "behind the scenes", "megamix", "medley", "symphonic", "orchestral", "tribute", "originally performed", "performed by", "in the style", "made famous", "rendition", "capella", "clean", "english sub", "sub", "chart", "gameplay", "sub español", "subtitulado", "legendado", "traducida", "432hz", "432 hz", "reversed", "midi", "piano version", "fan made", "preview", "teaser", "edit", "demo", "acoustic", "session"
    ];

    // Bracketed text on a YouTube title that doesn't indicate a different version.
    private static readonly string[] AllowedExtras = ["official audio", "audio", "remaster", "hd", "hq", "explicit", "full version", "full song"];

    // Video-ish labels that are harmless when it's the artist's own upload with the same length as the track.
    private static readonly string[] SoftExtras = ["lyric video", "lyrics", "visualizer", "in studio", "official video", "music video", "video"];

    // A lyric video of exactly the track's length (within 1.5s) is the album audio with text on top, from any channel.
    private static readonly string[] LyricExtras = ["lyric video", "lyrics", "visualizer"];
    private const double LyricExactSeconds = 1.5;

    private static readonly string[] VersionWords =
    [
        "version", "ver", "mix", "remix", "edit", "live", "acoustic", "capella", "cappella", "instrumental", "demo",
        "cover", "karaoke", "radio", "extended", "session", "unplugged", "reprise", "rework", "bootleg", "vip", "dub",
        "stripped", "piano", "orchestral", "sped", "slowed", "reverb", "tribute", "clean", "alternate", "alt", "take",
        "mono", "stereo", "single", "album", "original", "reimagined", "re-recorded", "rerecorded"
    ];

    public static MatchResult FindMatch(TrackObj track)
    {
        var first = Clean(track.artist.Split(',')[0]);
        var name = Clean(track.name);
        var artists = Clean(track.artist);
        // Ordered by how likely they are to surface YouTube's auto-generated "Topic" uploads
        // (their description starts "Provided to YouTube by ..."). Very short queries can come back empty,
        // so there are several phrasings; we stop as soon as a Topic/official-channel match is found.
        var queries = new[]
        {
            $"{name} {first} Provided to YouTube by",
            $"{first} {name} Provided to YouTube by",
            $"{first} {name} Provided to YouTube",
            $"{name} {artists} Provided to YouTube by",
            $"{first} {name} audio",
            $"{name} {first} Topic",
            $"{first} {name}"
        };

        var all = new Dictionary<string, Candidate>();
        var raw = new Dictionary<string, JObject>();
        foreach (var query in queries)
        {
            foreach (var json in Search(query))
            {
                var c = Score(json, track);
                if (c == null) continue;
                raw[c.Id] = json;
                if (!all.TryGetValue(c.Id, out var old) || c.Score > old.Score) all[c.Id] = c;
            }

            var best = all.Values.Where(c => !c.NeedsTrim).OrderByDescending(c => c.Score).FirstOrDefault();
            if (best is { Preferred: true, Score: >= MinScore }) break;
        }

        if (!all.Values.Any(c => !c.NeedsTrim))
            foreach (var query in queries.Take(2))
            {
                foreach (var json in Search(query, true))
                {
                    var c = Score(json, track);
                    if (c == null) continue;
                    raw[c.Id] = json;
                    all[c.Id] = c;
                }

                if (all.Values.Any(c => !c.NeedsTrim)) break;
            }

        // Flat search results sometimes omit the channel, which hides Topic/official uploads.
        // Look the channel up for the best few unnamed candidates and rescore them.
        foreach (var c in all.Values.Where(c => c.Channel.Length == 0).OrderByDescending(c => c.Score).Take(4).ToList())
        {
            var author = LookupChannel(c.Id);
            if (string.IsNullOrEmpty(author) || !raw.TryGetValue(c.Id, out var json)) continue;
            json["channel"] = author;
            var rescored = Score(json, track);
            if (rescored == null) all.Remove(c.Id);
            else all[c.Id] = rescored;
        }

        var spotifySeconds = (track.duration_ms ?? 0) / 1000.0;
        var ranked = all.Values.OrderByDescending(c => c.Score)
            .ThenBy(c => Math.Abs((c.Duration ?? 0) - spotifySeconds)) // ties: closest to Spotify's length
            .ToList();
        foreach (var c in ranked.Take(5))
            UI.WriteLine("  candidate {0,6:F1}{1}{5} {2}s  {3} | {4}", c.Score, c.Preferred ? " *" : "  ",
                c.Duration?.ToString("F0") ?? "?", c.Channel, c.Title, c.NeedsTrim ? "T" : " ");

        // A re-upload by a random channel needs a stronger score than a Topic/official-channel upload.
        var top = ranked.FirstOrDefault(c => !c.NeedsTrim && c.Score >= (c.Preferred ? MinScore : MinScoreUnofficial));
        if (top != null) return new MatchResult(top, null);

        // Nothing fits as-is. If the artist's own / a Topic upload exists but is LONGER than the track
        // (music video with intro/outro), offer it for trimming - the user supplies the timings.
        var trim = ranked.FirstOrDefault(c => c.NeedsTrim && c.Preferred && c.Score >= MinScore);
        return new MatchResult(null, trim);
    }

    /// <summary>
    ///     Post-download title check. Returns why a YouTube title can't be the Spotify track, or null if it looks fine.
    ///     Video-ish labels (lyric video, official video...) are allowed here: they're accepted fallbacks.
    /// </summary>
    public static string? TitleProblem(TrackObj track, string ytTitleRaw)
    {
        var ytTitle = Normalize(ytTitleRaw);
        var coreTitle = Normalize(Regex.Split(track.name, @"\s[-–(\[]")[0]);
        var latin = !coreTitle.Any(ch => ch > 127 && char.IsLetter(ch));
        if (latin && coreTitle.Length > 0 && !ytTitle.Contains(coreTitle))
            return $"title \"{ytTitleRaw}\" doesn't contain the song name";

        var spotifyText = Normalize($"{track.name} {track.album}");
        foreach (var word in BadWords.Where(w => !SoftExtras.Contains(w)))
            if (Regex.IsMatch(ytTitle, $@"\b{Regex.Escape(word)}\b") &&
                !Regex.IsMatch(spotifyText, $@"\b{Regex.Escape(word)}\b"))
                return $"title \"{ytTitleRaw}\" contains \"{word}\" (a different version)";
        return null;
    }

    private static Candidate? Score(JObject j, TrackObj track)
    {
        var id = (string?)j["id"];
        if (string.IsNullOrEmpty(id)) return null;
        var title = (string?)j["title"] ?? "";
        var channel = (string?)j["channel"] ?? (string?)j["uploader"] ?? "";
        var duration = (double?)j["duration"];

        var score = 0.0;
        var spotifyText = Normalize($"{track.name} {track.album}");
        var ytTitle = Normalize(title);

        double? diffSec = null;
        var needsTrim = false;
        // Duration is the strongest signal: ads, intros and outros change it.
        if (track.duration_ms is > 0 && duration is > 0)
        {
            var diff = Math.Abs(duration.Value - track.duration_ms.Value / 1000.0);
            diffSec = diff;
            if (diff > DurationToleranceSeconds)
            {
                // Wrong version / preamble / postamble. Unless it is merely a longer video of the same song,
                // which we can offer for trimming (with user-supplied timings).
                var longerBy = duration.Value - track.duration_ms.Value / 1000.0;
                if (longerBy <= 0 || longerBy > MaxTrimExtraSeconds) return null;
                needsTrim = true;
                score += 30;
            }
            else score += 60 - diff * 6;
        }

        // Any credited artist counts (collabs/soundtracks list many); the first is just the primary.
        var artistList = track.artist.Split(',').Select(Normalize).Where(a => a.Length > 0).ToList();
        var coreTitle = Normalize(Regex.Split(track.name, @"\s[-–(\[]")[0]); // "Song - 4K Version" -> "song"
        var channelNorm = Normalize(channel);
        var preferred = false;

        var titleMatches = ytTitle == coreTitle || ytTitle == Normalize(track.name) || ytTitle.Contains(coreTitle);
        var exactDuration = diffSec is <= 3;

        if (channel.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase))
        {
            // "<Artist> - Topic" is YouTube's auto-generated exact album audio, but only if it's the RIGHT artist.
            var topicArtist = Normalize(channel[..^"- Topic".Length]);
            if (artistList.Any(a => IsArtistName(topicArtist, a)))
            {
                score += 60;
                preferred = true;
            }
            else if (titleMatches && exactDuration && artistList.Any(a => a.Any(ch => ch > 127 && char.IsLetter(ch))))
            {
                // Artist spelled differently (e.g. Spotify has 桐生一馬, YouTube has "Kazuma Kiryu") but the
                // title matches and the length is within 3s: it is the same recording.
                score += 40;
                preferred = true;
            }
            else score -= 30; // Topic channel for a different artist
        }
        else if (artistList.Any(a => IsArtistName(channel, a)))
        {
            score += 25; // the artist's own channel
            preferred = true;
        }
        else if (artistList.Any(a => ytTitle.Contains(a))) score += 5; // someone else's re-upload
        else if (ytTitle.Contains("official audio") && titleMatches && exactDuration) score += 5; // label upload
        else score -= 40;                                              // unrelated channel, artist not even named

        if (ytTitle == coreTitle || ytTitle == Normalize(track.name)) score += 20;
        else if (ytTitle.Contains(coreTitle)) score += 10;
        else if (!coreTitle.Any(ch => ch > 127 && char.IsLetter(ch)))
            return null; // a different song: same artist and a similar length is not enough, whatever the channel
        else score -= 60; // Spotify title is non-Latin (kanji etc.), YouTube may romanise it: just penalise
        if (ytTitle.Contains("official audio")) score += 10;

        // A re-upload by an unrelated channel must be the song and nothing else. Any extra word in the title that
        // isn't in the Spotify title/album/artist (e.g. "MEGAMIX", "mashup", "ft. someone else") makes it suspect.
        // Topic/official-channel uploads are exempt: their subtitles are the artist's own ("It's All Good").
        var labelAudio = ytTitle.Contains("official audio") && titleMatches && exactDuration;
        if (!preferred && !labelAudio)
        {
            var known = new HashSet<string>(Tokens($"{track.name} {track.album} {track.artist} {channel}"));
            known.UnionWith(HarmlessTokens);
            var extra = Tokens(title).Where(t => !known.Contains(t) && !(t.Length == 4 && t.All(char.IsDigit))).ToList();
            if (extra.Count > 0) score -= 40;
        }

        var ownUpload = preferred && !channel.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase);
        // Artist's own video upload is an acceptable last resort (within the normal length tolerance, so a short
        // intro/outro is the worst case). Topic/audio uploads still outrank it by a wide margin.
        var softOk = ownUpload;
        var lyricOk = diffSec is <= LyricExactSeconds;

        foreach (Match m in Regex.Matches(title, @"[\(\[]([^\)\]]+)[\)\]]"))
        {
            var extra = Normalize(m.Groups[1].Value);
            if (extra.Length == 0 || spotifyText.Contains(extra)) continue;
            // "(feat. X)" / "(with X)": Spotify keeps featured artists in the artist field, not the title.
            if (Regex.IsMatch(extra, @"^(feat|ft|featuring|with)\b") || artistList.Any(a => extra.Contains(a))) continue;
            if (AllowedExtras.Any(a => extra.Contains(a))) continue;
            if ((softOk && SoftExtras.Any(a => extra.Contains(a))) || (lyricOk && LyricExtras.Any(a => extra.Contains(a))))
            {
                score -= 10;
                continue;
            }
            // Bracket says it's a different recording ("Cool as Heck Version", "Radio Edit", "Live")  -> heavy penalty.
            // Otherwise it's probably just a subtitle Spotify omits ("Smiley Face (It's All Good)") -> small one.
            score -= VersionWords.Any(w => Regex.IsMatch(extra, $@"\b{w}\b")) ? 70 : 15;
        }

        foreach (var word in BadWords)
            if (Regex.IsMatch(ytTitle, $@"\b{Regex.Escape(word)}\b") &&
                !Regex.IsMatch(spotifyText, $@"\b{Regex.Escape(word)}\b"))
            {
                var soft = (softOk && SoftExtras.Contains(word)) || (lyricOk && LyricExtras.Contains(word));
                score -= soft ? 10 : 40;
                if (!soft) preferred = false; // e.g. a "Tribute" or "Live" upload on a Topic channel is not the real thing
            }

        return new Candidate(id, title, channel, duration, score, preferred, needsTrim);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static string? LookupChannel(string videoId)
    {
        try
        {
            var url = "https://www.youtube.com/oembed?format=json&url=https://www.youtube.com/watch?v=" + videoId;
            return (string?)JObject.Parse(Http.GetStringAsync(url).GetAwaiter().GetResult())["author_name"];
        }
        catch
        {
            return null;
        }
    }

    // Words that don't change which song a title refers to.
    private static readonly HashSet<string> HarmlessTokens =
    [
        "official", "audio", "video", "lyric", "lyrics", "hd", "hq", "4k", "topic", "remastered", "remaster", "explicit",
        "feat", "ft", "featuring", "with", "by", "the", "a", "an", "of", "and", "in", "on", "to", "music", "song", "full",
        "new", "provided", "youtube", "vevo", "records", "recordings"
    ];

    private static string[] Tokens(string s) => Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Compact(string s) => Normalize(s).Replace(" ", "");

    // Channel name refers to the artist ("Crywank", "alyankovic" for "Weird Al Yankovic", ...).
    private static bool IsArtistName(string channel, string artist)
    {
        var c = Normalize(channel);
        var a = Normalize(artist);
        if (c.Length >= 2 && (c == a || c.Contains(a) || a.Contains(c) && c.Length >= 4)) return true;
        var cc = Compact(channel);
        var ac = Compact(artist);
        return cc.Length >= 6 && (ac.Contains(cc) || cc.Contains(ac));
    }

    private static string Clean(string s) => s.Replace("\"", " ").Replace("  ", " ").Trim();

    // Lower-case, drop apostrophes ("Ain't" == "Aint", "O'Clock" == "OClock"), other punctuation -> space.
    private static string Normalize(string s) =>
        Regex.Replace(Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"['’‘`´]", ""), @"[^\p{L}\p{N}\s]", " "), @"\s+", " ").Trim();

    private static IEnumerable<JObject> Search(string query, bool useCookies = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var a in new[] { $"ytsearch{SearchCount}:{query}", "--flat-playlist", "--dump-json", "-q" })
            psi.ArgumentList.Add(a);
        if (useCookies)
        {
            psi.ArgumentList.Add("--cookies-from-browser");
            psi.ArgumentList.Add("firefox");
        }

        using var p = Process.Start(psi);
        if (p == null) yield break;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JObject? obj = null;
            try { obj = JObject.Parse(line); } catch { /* ignore non-JSON lines */ }
            if (obj != null) yield return obj;
        }
    }
}
