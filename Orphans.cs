namespace JFSI_MissingMusic_Downloader;

/// <summary>
///     Dedicated step: mp3s in tracks/ that no processed playlist refers to ("orphans": songs you unliked, playlists you
///     stopped following or blocked, leftovers of older file-naming rules) are moved to MissingJsons/orphaned/, outside
///     the library, so Jellyfin only sees songs that are in your playlists. Nothing is deleted. If a song comes back into
///     a playlist later it is restored from there instead of being searched for and downloaded again.
///     Safety: if more than 15% of the library (and more than 20 files) would be moved, the step refuses, because that
///     usually means a playlist failed to load or the skip rules changed. --force-orphans overrides; --keep-orphans skips.
/// </summary>
internal static class Orphans
{
    private const double MaxShare = 0.15;
    private const int AlwaysAllowed = 20;

    public static int MovedThisRun { get; private set; }
    public static string? Refused { get; private set; }

    private static string Folder(string saveLocation) => Path.GetFullPath(Path.Combine(saveLocation, "..", "orphaned"));

    /// <summary>mp3s in the library that no track of the given (processed) playlists resolves to.</summary>
    public static List<string> Find(string saveLocation, IEnumerable<PlaylistDef> processed)
    {
        var expected = processed.SelectMany(p => p.tracks).Select(Library.FileNameFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.GetFiles(saveLocation, "*.mp3").Select(f => Path.GetFileName(f)!)
            .Where(f => !expected.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void MoveAll(string saveLocation, IReadOnlyList<PlaylistDef> processed, bool force)
    {
        if (processed.Count == 0) return; // nothing was processed: we can't know what belongs

        var orphans = Find(saveLocation, processed);
        if (orphans.Count == 0) return;

        var total = Directory.GetFiles(saveLocation, "*.mp3").Length;
        var limit = Math.Max(AlwaysAllowed, (int)(total * MaxShare));
        Console.WriteLine();
        if (orphans.Count > limit && !force)
        {
            Refused = $"{orphans.Count} of {total} files look orphaned, which is more than {limit}: not moved (a playlist may have failed to load, " +
                      "or you changed what is skipped). Check run_report.txt, then run with --force-orphans if that is intended.";
            Console.WriteLine("Orphan step skipped: " + Refused);
            return;
        }

        var folder = Folder(saveLocation);
        Directory.CreateDirectory(folder);
        var manifest = new List<string>();
        foreach (var file in orphans)
        {
            var dest = Path.Combine(folder, file);
            for (var i = 2; File.Exists(dest); i++) dest = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(file)} ({i}).mp3");
            File.Move(saveLocation + file, dest);
            Library.Forget(file);
            manifest.Add($"{DateTime.Now:yyyy-MM-dd HH:mm}  {file}");
        }

        File.AppendAllLines(Path.Combine(folder, "ORPHANED.txt"), manifest);
        MovedThisRun = orphans.Count;
        Console.WriteLine("Moved {0} orphaned song(s) (in none of your processed playlists) to {1}", orphans.Count, folder);
        foreach (var f in orphans.Take(10)) Console.WriteLine("    {0}", f.Replace(' ', ' '));
        if (orphans.Count > 10) Console.WriteLine("    ... and {0} more (all listed in ORPHANED.txt)", orphans.Count - 10);
    }

    /// <summary>A song that is back in a playlist: bring its file back from orphaned/ instead of downloading it again.</summary>
    public static bool TryRestore(string saveLocation, TrackObj t)
    {
        try
        {
            var folder = Folder(saveLocation);
            if (!Directory.Exists(folder)) return false;
            var want = Library.DefaultFileName(t);
            var hit = Directory.GetFiles(folder, "*.mp3").Select(f => Path.GetFileName(f)!)
                .FirstOrDefault(f => f.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (hit == null || File.Exists(saveLocation + hit)) return false;

            File.Move(Path.Combine(folder, hit), saveLocation + hit);
            Library.Adopt(hit);
            UI.WriteLine("Restored '{0}' from orphaned/ (the song is back in a playlist)", hit);
            return true;
        }
        catch
        {
            return false; // fall back to a normal download
        }
    }
}
