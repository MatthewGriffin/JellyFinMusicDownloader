using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace JFSI_MissingMusic_Downloader;

/// <summary>
///     Writes Spotify's tags and cover art into each mp3 so Jellyfin/Finamp show real titles, artists, albums and artwork
///     instead of YouTube's. The audio is copied untouched (ffmpeg -c copy); the rewritten file replaces the original only
///     if its length is unchanged. The YouTube link and original YouTube title are preserved (purl / yt_title), because
///     the library index and the end-of-run checks read them.
/// </summary>
internal static class Tagger
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>What a file was tagged with. A different value (new cover, corrected album...) triggers a re-tag.</summary>
    public static string TagKey(TrackObj t) =>
        string.Join("|", t.album_image_url ?? "", t.album, t.album_artist ?? "", t.name, t.artist, t.album_release_date ?? "",
            t.track_number?.ToString() ?? "", t.disc_number?.ToString() ?? "");

    /// <summary>Tag one file. Returns false (file left exactly as it was) on any problem.</summary>
    public static bool Apply(string saveLocation, string fileName, TrackObj t)
    {
        var path = saveLocation + fileName;
        var tmp = path + ".tagging.tmp";
        try
        {
            var before = Verifier.Probe(path);
            if (before == null) return false;

            var art = DownloadArt(saveLocation, t.album_image_url);
            var wantedArt = !string.IsNullOrEmpty(t.album_image_url);

            var psi = new ProcessStartInfo { FileName = "ffmpeg", RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            void Arg(params string[] a) { foreach (var x in a) psi.ArgumentList.Add(x); }

            Arg("-y", "-v", "error", "-i", path);
            if (art != null) Arg("-i", art);
            Arg("-map", "0:a");                      // the audio only: drops any older cover so ours replaces it
            if (art != null) Arg("-map", "1:v");
            Arg("-c", "copy", "-map_metadata", "0", "-id3v2_version", "3", "-write_id3v1", "0");

            var artists = string.Join("; ", t.artist.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0));
            var albumArtist = string.IsNullOrWhiteSpace(t.album_artist)
                ? t.artist.Split(',')[0].Trim()
                : string.Join("; ", t.album_artist.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0));
            Arg("-metadata", "title=" + t.name, "-metadata", "artist=" + artists, "-metadata", "album=" + t.album,
                "-metadata", "album_artist=" + albumArtist, "-metadata", "yt_title=" + before.Value.Title);
            if (!string.IsNullOrWhiteSpace(t.album_release_date)) Arg("-metadata", "date=" + t.album_release_date);
            if (t.track_number is > 0) Arg("-metadata", "track=" + t.track_number);
            if (t.disc_number is > 0) Arg("-metadata", "disc=" + t.disc_number);
            if (art != null) Arg("-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)");
            Arg("-f", "mp3", tmp);

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(tmp))
            {
                UI.WriteLine("  (could not tag {0}: {1})", fileName, err.Trim().Split('\n').LastOrDefault());
                return false;
            }

            // Never replace a file unless the copy is clearly the same audio.
            var after = Verifier.Probe(tmp);
            if (after == null || Math.Abs(after.Value.Duration - before.Value.Duration) > 0.5)
            {
                UI.WriteLine("  (tagging {0} changed its length, left untouched)", fileName);
                return false;
            }

            File.Move(tmp, path, true);
            if (art != null || !wantedArt) Library.MarkTagged(fileName, TagKey(t)); // missing cover => try again next run
            return true;
        }
        catch (Exception e)
        {
            UI.WriteLine("  (could not tag {0}: {1})", fileName, e.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Tag every file in the library that isn't tagged yet (or whose Spotify data changed).</summary>
    public static void TagAll(string saveLocation, IEnumerable<PlaylistDef> playlists)
    {
        // One file may serve several Spotify entries (same YouTube video): it takes the FIRST entry's album and cover.
        var todo = playlists.SelectMany(p => p.tracks)
            .DistinctBy(Program.TrackKey, StringComparer.OrdinalIgnoreCase)
            .Select(t => (Track: t, File: Library.FileNameFor(t)))
            .Where(x => File.Exists(saveLocation + x.File))
            .DistinctBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .Where(x => Library.TaggedWith(x.File) != TagKey(x.Track))
            .ToList();
        if (todo.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine("Adding Spotify tags and album art to {0} file(s) (audio is copied untouched)...", todo.Count);
        int done = 0, failed = 0;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4 }, x =>
        {
            if (!Apply(saveLocation, x.File, x.Track)) Interlocked.Increment(ref failed);
            var n = Interlocked.Increment(ref done);
            if (n % 50 == 0 || n == todo.Count) Console.WriteLine("  tagged {0}/{1}", n, todo.Count);
        });
        if (failed > 0) Console.WriteLine("  {0} file(s) could not be tagged (left as they were; they are retried next run)", failed);
    }

    private static string? DownloadArt(string saveLocation, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var dir = Path.GetFullPath(Path.Combine(saveLocation, "..", "art_cache"));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".jpg");
            if (File.Exists(file) && new FileInfo(file).Length > 1000) return file;

            var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (bytes.Length < 1000) return null;
            var part = file + "." + Environment.CurrentManagedThreadId + ".part";
            File.WriteAllBytes(part, bytes);
            File.Move(part, file, true);
            return file;
        }
        catch
        {
            return null; // no cover this time: tags are still written, and the file is retried next run
        }
    }
}
