using System.Collections.Concurrent;
using System.Text;

namespace JFSI_MissingMusic_Downloader;

/// <summary>
///     End-of-run report. Besides listing what happened this run, it re-checks the tracks folder against the playlists
///     from scratch (files present, readable, plausible length and title, .m3u files consistent) and ends with an
///     explicit verdict: COMPLETE, COMPLETE WITH WARNINGS or INCOMPLETE. Saved to MissingJsons/run_report.txt.
/// </summary>
internal static class Report
{
    private static readonly ConcurrentDictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Outcome> Outcomes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(string Name, int Tracks, string Reason)> Skipped = [];
    private static readonly object Gate = new();

    private const double SuspiciousLengthSeconds = 10; // present file this far from Spotify's length gets flagged
    private const int ConsoleListLimit = 15;           // long lists are cut on screen, complete in run_report.txt

    /// <summary>Why a track isn't available. The first reason recorded wins (the most specific one comes first).</summary>
    public static void Note(string key, string reason) => Reasons.TryAdd(key, reason);

    /// <summary>What happened to a track this run. A later "already had" never hides an earlier "downloaded".</summary>
    public static void SetOutcome(string key, Outcome outcome)
    {
        if (outcome == Outcome.AlreadyHad) Outcomes.TryAdd(key, outcome);
        else Outcomes[key] = outcome;
    }

    public static void SkippedPlaylist(string name, int tracks, string reason)
    {
        lock (Gate) Skipped.Add((name, tracks, reason));
    }

    /// <returns>true only if the verdict is COMPLETE or COMPLETE WITH WARNINGS (nothing missing, nothing broken).</returns>
    public static bool Print(IReadOnlyList<PlaylistDef> playlists, string saveLocation)
    {
        string FileOf(TrackObj t) => Library.FileNameFor(t);
        bool Exists(TrackObj t) => File.Exists(saveLocation + FileOf(t));
        var unique = playlists.SelectMany(p => p.tracks).DistinctBy(Program.TrackKey, StringComparer.OrdinalIgnoreCase).ToList();
        var present = unique.Where(Exists).ToList();
        var stillMissing = unique.Where(t => !Exists(t)).ToList();

        // ---------------- disk check ----------------
        Console.WriteLine();
        Console.WriteLine("Verifying {0} files on disk (readable? right length? right song?) ...", present.Count);
        var probes = new ConcurrentDictionary<string, (double Duration, string Title)?>();
        Parallel.ForEach(present, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            var info = Verifier.Probe(saveLocation + FileOf(t));
            probes[Program.TrackKey(t)] = info == null ? null : (info.Value.Duration, info.Value.Title);
        });
        var integrityChecked = probes.Values.Any(v => v != null) || present.Count == 0; // ffprobe missing => don't cry wolf

        var unreadable = new List<string>();
        var lengthOff = new List<string>();
        var titleOff = new List<string>();
        if (integrityChecked)
            foreach (var t in present)
            {
                var key = Program.TrackKey(t);
                var info = probes.GetValueOrDefault(key);
                var size = new FileInfo(saveLocation + FileOf(t)).Length;
                if (info == null || info.Value.Duration < 1 || size == 0)
                {
                    unreadable.Add($"{t.name} - {t.artist}");
                    continue;
                }

                var chosenByYou = Trims.Get(key) is { Skip: false }; // a video you picked/trimmed yourself is never second-guessed
                if (chosenByYou) continue;

                var spotify = (t.duration_ms ?? 0) / 1000.0;
                if (spotify > 0 && Math.Abs(info.Value.Duration - spotify) > SuspiciousLengthSeconds)
                    lengthOff.Add($"{t.name} - {t.artist}: {info.Value.Duration:F0}s but Spotify says {spotify:F0}s");

                if (info.Value.Title.Length > 0 && TrackMatcher.TitleProblem(t, info.Value.Title) is { } problem)
                    titleOff.Add($"{t.name} - {t.artist}: {problem}");
            }

        // .m3u files must exist and list exactly the tracks that are present
        var playlistFileProblems = new List<string>();
        foreach (var p in playlists)
        {
            var path = saveLocation + Program.SafeFileName(p.playlist_name) + ".m3u";
            if (!File.Exists(path))
            {
                playlistFileProblems.Add($"'{p.playlist_name}': playlist file is missing");
                continue;
            }

            var entries = File.ReadAllLines(path).Where(l => l.StartsWith("./")).Select(l => l[2..]).ToList();
            var dead = entries.Count(e => !File.Exists(saveLocation + e));
            var expected = p.tracks.Count(Exists);
            if (dead > 0) playlistFileProblems.Add($"'{p.playlist_name}': {dead} entries point at files that don't exist");
            else if (entries.Count != expected)
                playlistFileProblems.Add($"'{p.playlist_name}': lists {entries.Count} tracks but {expected} are on disk (out of date)");
        }

        // mp3s that belong to no processed playlist (informational only, never touched)
        var expectedFiles = unique.Select(FileOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = Directory.Exists(saveLocation)
            ? Directory.GetFiles(saveLocation, "*.mp3").Select(Path.GetFileName).Where(f => !expectedFiles.Contains(f!)).OrderBy(f => f).ToList()
            : [];

        // ---------------- verdict ----------------
        var incomplete = stillMissing.Count > 0 || unreadable.Count > 0 || playlistFileProblems.Count > 0 || playlists.Count == 0;
        var warnings = lengthOff.Count + titleOff.Count + (integrityChecked ? 0 : 1);
        var verdict = incomplete
            ? $"INCOMPLETE - {stillMissing.Count} track(s) missing, {unreadable.Count} unreadable, {playlistFileProblems.Count} playlist file problem(s)"
            : warnings > 0
                ? $"COMPLETE WITH WARNINGS - all {unique.Count} tracks are on disk, but {warnings} {(warnings == 1 ? "needs" : "need")} a look (see below)"
                : $"COMPLETE - all {unique.Count} tracks of {playlists.Count} playlists are on disk and verified";

        // ---------------- build the report (file = everything, console = long lists cut) ----------------
        var file = new List<string>();
        var screen = new List<string>();
        void Both(string s) { file.Add(s); screen.Add(s); }
        void List(IReadOnlyList<string> items, string indent)
        {
            for (var i = 0; i < items.Count; i++)
            {
                file.Add(indent + items[i]);
                if (i < ConsoleListLimit) screen.Add(indent + items[i]);
            }

            if (items.Count > ConsoleListLimit) screen.Add($"{indent}... and {items.Count - ConsoleListLimit} more (full list in run_report.txt)");
        }

        var rule = new string('=', 66);
        Both("");
        Both(rule);
        Both($" RUN REPORT  {DateTime.Now:yyyy-MM-dd HH:mm}");
        Both(rule);
        Both(" RESULT: " + verdict);
        Both(rule);

        Both($"Playlists: {playlists.Count} processed, {Skipped.Count} skipped");
        foreach (var s in Skipped) Both($"  skipped '{s.Name}' ({s.Tracks} tracks): {s.Reason}");

        int Count(Outcome oc) => Outcomes.Count(kv => kv.Value == oc);
        var fixedLater = unique.Count(t => Outcomes.TryGetValue(Program.TrackKey(t), out var oc)
                                           && oc is Outcome.NoMatch or Outcome.NeedsTrim or Outcome.Failed && Exists(t));
        string Category(TrackObj t)
        {
            var r = Reasons.GetValueOrDefault(Program.TrackKey(t), "");
            if (r.StartsWith("rejected")) return "rejected after download";
            if (r.StartsWith("no confident match")) return "no confident match";
            if (r.StartsWith("only a longer video")) return "only a longer video (needs your trim times)";
            if (r.StartsWith("you chose")) return "marked \"never ask again\"";
            return "download failed / not attempted";
        }

        Both("");
        Both($"Tracks ({unique.Count} unique across the processed playlists):");
        Both($"  downloaded this run : {Count(Outcome.Downloaded)}");
        Both($"  already had         : {Count(Outcome.AlreadyHad)}");
        if (fixedLater > 0) Both($"  fixed at the end-of-run prompts (included in the above): {fixedLater}");
        Both($"  on disk now         : {present.Count} of {unique.Count}");
        Both($"  still missing       : {stillMissing.Count}");
        foreach (var g in stillMissing.GroupBy(Category).OrderByDescending(g => g.Count()))
            Both($"      {g.Count(),4}  {g.Key}");

        Both("");
        Both("Per playlist:");
        foreach (var p in playlists)
        {
            var tracks = p.tracks.DistinctBy(Program.TrackKey, StringComparer.OrdinalIgnoreCase).ToList();
            var missing = tracks.Where(t => !Exists(t)).ToList();
            if (missing.Count == 0)
            {
                Both($"  OK       '{p.playlist_name}': all {tracks.Count} tracks present");
                continue;
            }

            Both($"  MISSING  '{p.playlist_name}': {tracks.Count - missing.Count} of {tracks.Count} tracks present, {missing.Count} missing:");
            foreach (var t in missing)
                Both($"      - {t.name} - {t.artist}\n          {Reasons.GetValueOrDefault(Program.TrackKey(t), "not attempted this run (run interrupted?)")}");
        }

        Both("");
        Both("Disk check:");
        if (!integrityChecked)
            Both("  WARNING: ffprobe could not be run, so file integrity, length and titles were NOT checked.");
        else
        {
            Both($"  unreadable or empty files : {unreadable.Count}");
            List(unreadable, "      - ");
            Both($"  length far from Spotify   : {lengthOff.Count}  (files you picked or trimmed yourself are exempt)");
            List(lengthOff, "      - ");
            Both($"  title looks like a different version: {titleOff.Count}");
            List(titleOff, "      - ");
        }

        var taggedCount = Library.TaggedCount(present.Select(FileOf));
        Both($"  Spotify tags + cover art  : {taggedCount} of {present.Count} files" + (taggedCount < present.Count ? "  (the rest are tagged on the next run, or run without --no-tag)" : ""));
        Both($"  playlist (.m3u) files     : {(playlistFileProblems.Count == 0 ? $"all {playlists.Count} present and consistent" : playlistFileProblems.Count + " problem(s)")}");
        List(playlistFileProblems, "      - ");
        Both($"  one file shared by several Spotify entries: {Library.SharedEntries}  (same YouTube video, so one copy; each entry keeps its own playlist line)");
        if (Library.MergedThisRun > 0)
            Both($"  duplicate copies merged this run: {Library.MergedThisRun}  (extra copies are in MissingJsons/merged_duplicates/)");
        if (Orphans.MovedThisRun > 0)
            Both($"  orphaned songs moved this run: {Orphans.MovedThisRun}  (to MissingJsons/orphaned/, listed in ORPHANED.txt; they come back automatically if a playlist wants them again)");
        if (Orphans.Refused != null) Both("  ORPHAN STEP SKIPPED: " + Orphans.Refused);
        Both($"  mp3s still in no processed playlist: {orphans.Count}");
        List(orphans.Select(o => o!).ToList(), "      - ");

        Both("");
        if (stillMissing.Count > 0)
            Both("Next: tracks marked 'no confident match' are in tracks/unmatched.log (retry with --retry-unmatched); rejected downloads are in tracks/rejected.log.");
        Both(" RESULT: " + verdict);
        Both(rule);

        foreach (var l in screen) Console.WriteLine(l);
        try
        {
            var path = Path.GetFullPath(Path.Combine(saveLocation, "..", "run_report.txt"));
            File.WriteAllLines(path, file, new UTF8Encoding(false));
            Console.WriteLine("Full report saved to " + path);
        }
        catch (Exception e)
        {
            Console.WriteLine("(could not save the report: " + e.Message + ")");
        }

        return !incomplete;
    }
}
