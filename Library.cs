using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace JFSI_MissingMusic_Downloader;

/// <summary>
///     Which file holds each track's audio. Rules:
///     - One copy per YouTube video: if two Spotify entries resolve to the same video, both playlist lines point at the
///       same file (the second entry becomes an alias; nothing is downloaded twice).
///     - Windows-safe names: file names are compared ignoring case ("PERCEPTION CHECK" and "Perception Check" are one
///       file), and over-long names are shortened.
///     - Entries that are genuinely different recordings stay separate files with their own playlist lines.
///     The mapping is saved in MissingJsons/file_map.json.
/// </summary>
internal static class Library
{
    private const int MaxNameBytes = 200; // Linux allows 255 bytes per name, Windows 255 characters: stay well inside both

    private static readonly object Gate = new();
    private static string _dir = "";
    private static string _mapPath = "";

    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);             // exact file name -> video key ("" = unknown)
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase); // track key -> file holding its audio
    private static readonly Dictionary<string, string> OnDisk = new(StringComparer.OrdinalIgnoreCase);  // name ignoring case -> exact name on disk
    private static readonly Dictionary<string, string> Tagged = new(StringComparer.Ordinal);            // file name -> what it was tagged with (see Tagger)

    private sealed class MapFile
    {
        public Dictionary<string, string> files { get; set; } = new();
        public Dictionary<string, string> aliases { get; set; } = new();
        public Dictionary<string, string> tagged { get; set; } = new();
    }

    public static int MergedThisRun { get; private set; }

    public static int SharedEntries
    {
        get { lock (Gate) return Aliases.Count; }
    }

    // ---------------------------------------------------------------- naming

    /// <summary>The key identifying one video, or one trimmed range of it.</summary>
    public static string VideoKey(string videoId, double? start, double? end) =>
        start.HasValue && end.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "{0}@{1:0.###}-{2:0.###}", videoId, start, end)
            : videoId;

    /// <summary>The name a NEW download gets: cleaned title + artist, Windows-safe, never longer than MaxNameBytes.</summary>
    public static string DefaultFileName(TrackObj t)
    {
        var stem = t.name.RemoveChars() + "-" + t.artist.RemoveChars();
        if (Encoding.UTF8.GetByteCount(stem) > MaxNameBytes)
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stem)))[..6];
            var cut = stem;
            while (Encoding.UTF8.GetByteCount(cut) > MaxNameBytes - 8) cut = cut[..^1];
            stem = cut.TrimEnd() + "~" + hash;
        }

        return stem + ".mp3";
    }

    /// <summary>
    ///     The file that holds this track's audio: an alias if its audio is shared with another entry, otherwise an
    ///     existing file of the same name ignoring case, otherwise the default name for a new download.
    /// </summary>
    public static string FileNameFor(TrackObj t)
    {
        var key = Program.TrackKey(t);
        lock (Gate)
        {
            if (Aliases.TryGetValue(key, out var target))
            {
                if (OnDisk.TryGetValue(target, out var actual)) return actual;
                Aliases.Remove(key); // the shared file is gone: fall back to the track's own file
            }

            var def = DefaultFileName(t);
            return OnDisk.TryGetValue(def, out var existing) ? existing : def;
        }
    }

    public static bool Exists(TrackObj t) => File.Exists(_dir + FileNameFor(t));

    // ---------------------------------------------------------------- video <-> file

    /// <summary>A file already in the library that holds exactly this video (or trimmed range), or null.</summary>
    public static string? FileForVideo(string videoKey)
    {
        lock (Gate)
            foreach (var kv in Files)
                if (kv.Value == videoKey && File.Exists(_dir + kv.Key))
                    return kv.Key;
        return null;
    }

    public static void Register(string fileName, string videoKey)
    {
        lock (Gate)
        {
            Files[fileName] = videoKey;
            OnDisk.TryAdd(fileName, fileName);
            Tagged.Remove(fileName); // a fresh download has YouTube's tags until Tagger rewrites them
            Save();
        }
    }

    /// <summary>This Spotify entry's audio is the existing file (same YouTube video): point it there.</summary>
    public static void Alias(TrackObj t, string fileName)
    {
        lock (Gate)
        {
            Aliases[Program.TrackKey(t)] = fileName;
            Save();
        }
    }

    // ---------------------------------------------------------------- startup

    public static void Init(string saveLocation)
    {
        _dir = saveLocation;
        _mapPath = Path.GetFullPath(Path.Combine(saveLocation, "..", "file_map.json"));

        List<string> toProbe;
        lock (Gate)
        {
            Files.Clear();
            Aliases.Clear();
            OnDisk.Clear();
            Tagged.Clear();
            try
            {
                if (File.Exists(_mapPath))
                {
                    var map = JsonConvert.DeserializeObject<MapFile>(File.ReadAllText(_mapPath));
                    if (map != null)
                    {
                        foreach (var kv in map.files) Files[kv.Key] = kv.Value;
                        foreach (var kv in map.aliases) Aliases[kv.Key] = kv.Value;
                        foreach (var kv in map.tagged) Tagged[kv.Key] = kv.Value;
                    }
                }
            }
            catch
            {
                // a damaged map is rebuilt from the files themselves
            }

            var disk = Directory.GetFiles(saveLocation, "*.mp3").Select(Path.GetFileName).Select(f => f!).ToList();
            foreach (var f in disk) OnDisk.TryAdd(f, f);

            var onDisk = disk.ToHashSet(StringComparer.Ordinal);
            foreach (var gone in Files.Keys.Where(f => !onDisk.Contains(f)).ToList()) Files.Remove(gone);
            foreach (var gone in Tagged.Keys.Where(f => !onDisk.Contains(f)).ToList()) Tagged.Remove(gone);
            foreach (var stale in Aliases.Where(kv => !OnDisk.ContainsKey(kv.Value)).Select(kv => kv.Key).ToList()) Aliases.Remove(stale);

            toProbe = disk.Where(f => !Files.ContainsKey(f)).ToList();
        }

        if (toProbe.Count > 0)
        {
            UI.WriteLine("Indexing {0} new file(s) in the library (reading which YouTube video each came from)...", toProbe.Count);
            var found = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
            Parallel.ForEach(toProbe, new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
            {
                var info = Verifier.Probe(saveLocation + f);
                var m = Regex.Match(info?.Url ?? "", @"v=([\w-]{11})");
                var key = m.Success ? m.Groups[1].Value : "";
                // A range you trimmed yourself is its own thing: never treat it as "the whole video".
                if (key.Length > 0 && Trims.Get(f[..^4]) is { Skip: false } e && e.End > e.Start) key = VideoKey(key, e.Start, e.End);
                found[f] = key;
            });
            lock (Gate)
            {
                foreach (var kv in found) Files[kv.Key] = kv.Value;
                Save();
            }
        }
    }

    // ---------------------------------------------------------------- merging existing duplicates

    /// <summary>
    ///     Files that came from the same YouTube video AND are byte-identical are one recording stored twice. Keep one,
    ///     point every Spotify entry that used the other at it, and move the redundant copy to merged_duplicates/
    ///     (outside the library, so Jellyfin never sees it and nothing is lost).
    /// </summary>
    public static List<string> MergeDuplicates(IEnumerable<TrackObj> tracks)
    {
        var trackList = tracks.ToList();
        var referenced = new HashSet<string>(trackList.Select(DefaultFileName), StringComparer.OrdinalIgnoreCase);
        var moved = new List<string>();
        var manifest = new List<string>();
        var holding = Path.GetFullPath(Path.Combine(_dir, "..", "merged_duplicates"));

        lock (Gate)
        {
            var sameVideo = Files.Where(kv => kv.Value.Length > 0 && File.Exists(_dir + kv.Key)).GroupBy(kv => kv.Value)
                .Where(g => g.Count() > 1).ToList();

            foreach (var group in sameVideo)
            {
                var identical = group.Select(kv => kv.Key)
                    .GroupBy(f => new FileInfo(_dir + f).Length)
                    .Where(g => g.Count() > 1)
                    .SelectMany(g => g.GroupBy(f => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(_dir + f)))))
                    .Where(g => g.Count() > 1);

                foreach (var set in identical)
                {
                    var keep = set.OrderByDescending(f => referenced.Contains(f)) // a file a Spotify entry actually names
                        .ThenBy(f => f.Length)
                        .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(f => f, StringComparer.Ordinal)
                        .First();

                    foreach (var other in set.Where(f => f != keep))
                    {
                        foreach (var t in trackList.Where(t => DefaultFileName(t).Equals(other, StringComparison.OrdinalIgnoreCase)))
                            Aliases[Program.TrackKey(t)] = keep;

                        Directory.CreateDirectory(holding);
                        var dest = Path.Combine(holding, other);
                        for (var i = 2; File.Exists(dest); i++) dest = Path.Combine(holding, $"{Path.GetFileNameWithoutExtension(other)} ({i}).mp3");
                        File.Move(_dir + other, dest);

                        Files.Remove(other);
                        if (OnDisk.TryGetValue(other, out var mapped) && mapped == other) OnDisk[keep] = keep; // case-insensitive index now points at the kept copy
                        moved.Add(other);
                        manifest.Add($"{other}  ->  same audio as  {keep}  (YouTube video {group.Key}, byte-identical)");
                    }
                }
            }

            if (moved.Count > 0) Save();
        }

        if (manifest.Count > 0)
            File.AppendAllLines(Path.Combine(holding, "MERGED.txt"), manifest.Select(l => $"{DateTime.Now:yyyy-MM-dd HH:mm}  {l}"));
        MergedThisRun = moved.Count;
        return moved;
    }

    /// <summary>A file left the library (moved to orphaned/): drop everything we knew about it.</summary>
    public static void Forget(string fileName)
    {
        lock (Gate)
        {
            Files.Remove(fileName);
            Tagged.Remove(fileName);
            if (OnDisk.TryGetValue(fileName, out var mapped) && mapped == fileName) OnDisk.Remove(fileName);
            foreach (var k in Aliases.Where(kv => kv.Value.Equals(fileName, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                Aliases.Remove(k);
            Save();
        }
    }

    /// <summary>A file appeared in the library from outside a download (restored from orphaned/): index it.</summary>
    public static void Adopt(string fileName)
    {
        var info = Verifier.Probe(_dir + fileName);
        var m = Regex.Match(info?.Url ?? "", @"v=([\w-]{11})");
        var key = m.Success ? m.Groups[1].Value : "";
        if (key.Length > 0 && Trims.Get(fileName[..^4]) is { Skip: false } e && e.End > e.Start) key = VideoKey(key, e.Start, e.End);
        lock (Gate)
        {
            Files[fileName] = key;
            OnDisk.TryAdd(fileName, fileName);
            Tagged.Remove(fileName); // re-tagged at the end of the run against the current Spotify data
            Save();
        }
    }

    // ---------------------------------------------------------------- tagging state

    public static string? TaggedWith(string fileName)
    {
        lock (Gate) return Tagged.GetValueOrDefault(fileName);
    }

    public static void MarkTagged(string fileName, string what)
    {
        lock (Gate)
        {
            Tagged[fileName] = what;
            Save();
        }
    }

    public static int TaggedCount(IEnumerable<string> fileNames)
    {
        lock (Gate) return fileNames.Count(f => Tagged.ContainsKey(f));
    }

    // ---------------------------------------------------------------- persistence

    private static void Save()
    {
        try
        {
            File.WriteAllText(_mapPath, JsonConvert.SerializeObject(new MapFile
            {
                files = new Dictionary<string, string>(Files),
                aliases = new Dictionary<string, string>(Aliases),
                tagged = new Dictionary<string, string>(Tagged)
            }, Formatting.Indented));
        }
        catch
        {
            // the map is a cache: it is rebuilt from the files if it can't be written
        }
    }
}
