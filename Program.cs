using System.Diagnostics;
using System.Globalization;
using Newtonsoft.Json;

namespace JFSI_MissingMusic_Downloader;

internal static class Program
{
    private const string SaveLocation = "/run/media/system/SSD3/JFSI_MissingMusic_Downloader/MissingJsons/tracks/";
    private const string JsonLocation = "/run/media/system/SSD3/JFSI_MissingMusic_Downloader/MissingJsons/Jsons/";
    private const int MaxParallelDownloads = 4; // lower this if YouTube starts rate-limiting
    private static readonly object LogLock = new();
    private static readonly List<PlaylistDef> AllPlaylists = [];
    private const string UnmatchedLogName = "unmatched.log";
    // Tracks we already failed to match: skipped automatically (run with --retry-unmatched to try them again).
    private static readonly HashSet<string> Unmatched = LoadUnmatched();
    private const string PythonScriptArgs = "spotify_lib/my_spotify_playlists_downloader.py --split";

    private static void Main(string[] args)
    {
        if (args.Contains("--retry-unmatched"))
        {
            // Forget the old failures; anything that still can't be matched is written back as it is found.
            lock (LogLock)
            {
                Unmatched.Clear();
                if (File.Exists(SaveLocation + UnmatchedLogName)) File.Delete(SaveLocation + UnmatchedLogName);
            }

            UI.WriteLine("--retry-unmatched: cleared {0}, those tracks will be searched again", UnmatchedLogName);
        }
        else if (Unmatched.Count > 0)
        {
            UI.WriteLine("Skipping {0} track(s) listed in {1} (run with --retry-unmatched to try them again)",
                Unmatched.Count, UnmatchedLogName);
        }

        UpdateDownloader();
        GetPlaylistsJSON();

        var jsons = Directory.GetFiles(JsonLocation);
        UI.Start(jsons.Sum(CountTracks));
        foreach (var path in jsons)
            if (File.Exists(path))
            {
                var fileContents = File.ReadAllText(path);
                var deserializeObject = JsonConvert.DeserializeObject<List<PlaylistDef>>(fileContents);
                if (deserializeObject == null || deserializeObject.Count == 0) continue;
                var playlist = deserializeObject[0];
                ProcessFile(playlist);
                AllPlaylists.Add(playlist);
                Generate3MUPlaylist(playlist); // partial copy in case the run is interrupted
            }
            else if (Directory.Exists(path))
            {
                ProcessFilesInFolder(path);
            }
            else
            {
                UI.WriteLine("{0} is not a valid file or directory", path);
            }

        UI.Stop();
        UI.WriteLine("Finished Downloading");
        Trims.ResolvePending(); // asks you for start/end times for tracks that only exist as a longer video

        // Final pass: now that trims are resolved, rewrite every playlist from what is really on disk.
        foreach (var playlist in AllPlaylists) Generate3MUPlaylist(playlist);
        UI.WriteLine("Init Cleanup");
        PostCleanup();
    }

    internal static string TrackKey(TrackObj t) => t.name.RemoveChars() + "-" + t.artist.RemoveChars();

    private static int CountTracks(string path)
    {
        try
        {
            var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.json") : [path];
            return files.Sum(f =>
            {
                var list = JsonConvert.DeserializeObject<List<PlaylistDef>>(File.ReadAllText(f));
                return list is { Count: > 0 } ? list[0].tracks.DistinctBy(TrackKey).Count() : 0;
            });
        }
        catch { return 0; }
    }

    private static void GetPlaylistsJSON()
    {
        RunGetPlaylistsJSON("python", PythonScriptArgs);
    }

    private static void RunGetPlaylistsJSON(string pycommand, string args)
    {
        RunCommand(pycommand, true, args);
    }

    private static void UpdateDownloader()
    {
        RunCommand("yt-dlp", false, "-U");
    }

    private static void ProcessFilesInFolder(string folderPath)
    {
        var files = Directory.GetFiles(folderPath, "*.json");
        foreach (var file in files)
        {
            UI.WriteLine("Processing file {0}", file);
            var fileContents = File.ReadAllText(file);
            var deserializeObject = JsonConvert.DeserializeObject<List<PlaylistDef>>(fileContents);
            if (deserializeObject == null || deserializeObject.Count == 0) continue;
            var playlist = deserializeObject[0];
            ProcessFile(playlist);
            AllPlaylists.Add(playlist);
            Generate3MUPlaylist(playlist); // partial copy in case the run is interrupted
            UI.WriteLine("Finished processing file {0}", file);
        }
    }

    /// <summary>
    ///     Writes <playlist>.m3u next to the mp3s. Only tracks whose file actually exists are listed (skipped, rejected
    ///     or not-yet-trimmed tracks would otherwise show up as broken entries), with real durations in #EXTINF.
    /// </summary>
    private static void Generate3MUPlaylist(PlaylistDef playlist)
    {
        try
        {
            var lines = new List<string> { "#EXTM3U", "#PLAYLIST:" + playlist.playlist_name };
            var listed = 0;
            var missing = 0;

            foreach (var track in playlist.tracks)
            {
                var file = track.name.RemoveChars() + "-" + track.artist.RemoveChars() + ".mp3";
                if (!File.Exists(SaveLocation + file))
                {
                    missing++;
                    continue;
                }

                // #EXTINF:<length in whole seconds>,<display title>   (-1 = unknown)
                var seconds = track.duration_ms is > 0 ? (int)Math.Round(track.duration_ms.Value / 1000.0) : -1;
                lines.Add("#EXTINF:" + seconds + "," + track.artist + " - " + track.name);
                lines.Add("./" + file); // relative to the .m3u, which lives in the same folder as the mp3s
                listed++;
            }

            var path = SaveLocation + SafeFileName(playlist.playlist_name) + ".m3u";
            File.WriteAllLines(path, lines); // UTF-8, no BOM
            UI.WriteLine("Playlist '{0}': {1} track(s) written{2}", playlist.playlist_name, listed,
                missing > 0 ? $", {missing} not downloaded (left out)" : "");
        }
        catch (Exception e)
        {
            UI.WriteLine("!!!! Exception occurred generating playlist {0}: {1}", playlist.playlist_name, e.Message);
        }
    }

    // Playlist names come from Spotify and can contain characters that are invalid or dangerous in a file name ("AC/DC").
    private static string SafeFileName(string name)
    {
        var cleaned = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "playlist" : cleaned;
    }

    private static void ProcessFile(PlaylistDef playlist)
    {
        try
        {
            UI.SetPlaylist(playlist.playlist_name);
            // Same song can appear twice in a playlist; only fetch it once.
            var unique = playlist.tracks.DistinctBy(TrackKey);

            Parallel.ForEach(unique, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelDownloads }, track =>
            {
                UI.TrackStart();
                var outcome = Outcome.Failed;
                try
                {
                    var title = track.name.RemoveChars();
                    var artist = track.artist.RemoveChars();

                    if (File.Exists(SaveLocation + title + "-" + artist + ".mp3"))
                        outcome = Outcome.AlreadyHad;
                    else
                        outcome = RunDownloadCommand(track, title, artist, SaveLocation);
                }
                catch (Exception e)
                {
                    UI.WriteLine("!!!! Failed {0} - {1}: {2}", track.name, track.artist, e.Message);
                }

                UI.TrackEnd(outcome);
            });
        }
        catch (Exception e)
        {
            UI.WriteLine("!!!! Exception occurred processing playlist {0}: {1}", playlist.playlist_name,
                e.Message);
        }
    }

    private static Outcome RunDownloadCommand(TrackObj track, string title, string artist, string saveLocation)
    {
        var key = TrackKey(track);

        // Timings you gave on a previous run: reuse them, no searching or asking.
        var saved = Trims.Get(key);
        if (saved != null)
        {
            if (saved.Skip) return Outcome.NoMatch;
            UI.WriteLine("Using your saved trim for {0} - {1}", track.name, track.artist);
            return DownloadAudio(track, saved.VideoId, title, artist, saved.Start, saved.End) ? Outcome.Downloaded : Outcome.Failed;
        }

        if (Trims.Pending.ContainsKey(key)) return Outcome.NeedsTrim; // same song in another playlist

        if (IsUnmatched(track)) return Outcome.NoMatch; // failed before, don't waste searches on it

        UI.WriteLine("Searching for {0} - {1}", track.name, track.artist);
        var match = TrackMatcher.FindMatch(track);

        if (match.Best == null && match.TrimCandidate != null)
        {
            Trims.Pending[key] = new PendingTrim(track, match.TrimCandidate);
            UI.WriteLine("Needs trimming (only a longer video exists), will ask at the end: {0} - {1}", track.name, track.artist);
            return Outcome.NeedsTrim;
        }

        if (match.Best == null)
        {
            UI.WriteLine("!!! No confident match, skipping: {0} - {1}", track.name, track.artist);
            RecordUnmatched(track);
            return Outcome.NoMatch;
        }

        var best = match.Best;
        UI.WriteLine("Chosen: {0} | {1} (score {2:F1})", best.Channel, best.Title, best.Score);
        return DownloadAudio(track, best.Id, title, artist) ? Outcome.Downloaded : Outcome.Failed;
    }

    private static string UnmatchedKey(TrackObj t) => t.name + " - " + t.artist;

    private static HashSet<string> LoadUnmatched()
    {
        try
        {
            var path = SaveLocation + UnmatchedLogName;
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsUnmatched(TrackObj t)
    {
        lock (LogLock) return Unmatched.Contains(UnmatchedKey(t));
    }

    private static void RecordUnmatched(TrackObj t)
    {
        lock (LogLock)
            if (Unmatched.Add(UnmatchedKey(t)))
                File.AppendAllText(SaveLocation + UnmatchedLogName, UnmatchedKey(t) + "\n"); // no duplicate lines
    }

    /// <summary>
    ///     Downloads a video's audio as mp3 (optionally only start..end seconds) and verifies the result against the
    ///     Spotify track. A file that fails verification is deleted and logged to rejected.log.
    /// </summary>
    internal static bool DownloadAudio(TrackObj track, string videoId, string title, string artist,
        double? start = null, double? end = null)
    {
        var trimmed = start.HasValue && end.HasValue;
        var section = trimmed
            ? string.Format(CultureInfo.InvariantCulture,
                " --download-sections \"*{0:0.###}-{1:0.###}\" --force-keyframes-at-cuts", start, end)
            : "";
        RunCommand("yt-dlp", false, true, "\"https://www.youtube.com/watch?v=" + videoId +
                                         "\" -t mp3 -q --sleep-interval 1 --max-sleep-interval 3 --windows-filenames -o \"" +
                                         title + "-" + artist + ".%(ext)s\" -P " + SaveLocation +
                                         " --embed-metadata --cookies-from-browser firefox" +
                                         " --js-runtimes node --remote-components ejs:github" + section);

        var path = SaveLocation + title + "-" + artist + ".mp3";
        if (!File.Exists(path)) return false;

        // Trims were chosen by you, so only check the length came out as selected. Everything else gets the full check.
        var result = trimmed
            ? Verifier.Check(path, track, end!.Value - start!.Value, 3, false)
            : Verifier.Check(path, track, (track.duration_ms ?? 0) / 1000.0, 8, true);
        if (result.Ok) return true;

        File.Delete(path);
        UI.WriteLine("!!! Rejected after download, file deleted: {0} - {1}: {2}", track.name, track.artist, result.Reason);
        lock (LogLock)
            File.AppendAllText(SaveLocation + "rejected.log",
                $"{track.name} - {track.artist} | {result.Reason} | {result.YouTubeTitle} | {result.Url}\n");
        if (!trimmed) RecordUnmatched(track); // the same upload would just be picked again next run
        return false;
    }

    private static void RunCommand(string fileName, bool useShellExecute, string arguments)
    {
        RunCommand(fileName, useShellExecute, false, arguments);
    }

    // capture=true routes the child's output through the UI so it can't corrupt the progress bar.
    private static void RunCommand(string fileName, bool useShellExecute, bool capture, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = useShellExecute,
            Arguments = arguments,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture
        };
        var process = Process.Start(psi);
        if (process == null) return;
        if (capture)
        {
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) UI.WriteLine("{0}", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) UI.WriteLine("{0}", e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        process.WaitForExit();
        process.Close();
    }

    private static void PostCleanup()
    {
        const string webmDir = "/home/matthew/Videos/YT/";
        if (!Directory.Exists(webmDir)) return;
        var webmFiles = Directory.GetFiles(webmDir, "*.webm");
        foreach (var file in webmFiles)
            if (File.Exists(file))
                File.Delete(file);
    }

    internal static string RemoveChars(this string val)
    {
        IDictionary<string, string> badChars = new Dictionary<string, string>
        {
            { ".", "" },
            { ",", "" },
            { "(", "" },
            { ")", "" },
            { "{", "" },
            { "}", "" },
            { "\"", "" },
            { "'", "" },
            { "/", "" },
            { "\\", "" },
            { "?", "" },
            { ";", "" },
            { "*", "" },
            { "!", "" },
            { ":", "" },
            { "&", "" },
            // Not allowed in Windows file names (the library is read by Jellyfin on Windows), and % is special
            // in yt-dlp output templates.
            { "<", "" },
            { ">", "" },
            { "|", "" },
            { "%", "" }
        };

        // Windows also refuses names that end in a space or a dot.
        return badChars.Aggregate(val, (current, c) => current.Replace(c.Key, c.Value)).Trim().TrimEnd('.');
    }
}