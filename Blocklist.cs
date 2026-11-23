using System.Text.RegularExpressions;

namespace JFSI_MissingMusic_Downloader;

/// <summary>
///     Playlists you never want downloaded. Spotify-generated playlists you've saved (Playlist in a Bottle, Daily Mix...)
///     are listed as owned by you, so they can only be recognised by name.
///     The list lives in MissingJsons/playlist_blocklist.txt: one name per line, * matches anything, case-insensitive,
///     lines starting with # are comments. The file is created with defaults the first time it is missing.
/// </summary>
internal static class Blocklist
{
    private const string FilePath = "/run/media/system/SSD3/JFSI_MissingMusic_Downloader/MissingJsons/playlist_blocklist.txt";

    private const string Defaults = """
        # Playlists to skip. One name per line (case-insensitive). * matches anything. Lines starting with # are ignored.
        # Delete a line to download that playlist again.

        # Spotify-generated playlists (Wrapped / yearly recaps)
        My 20* Playlist in a Bottle
        Your Top Songs *
        Your Summer Rewind
        Time Capsule
        Repeat Rewind

        # Spotify-generated playlists (personal mixes that refresh automatically)
        Discover Weekly
        Release Radar
        On Repeat
        Daily Mix *
        Your Daily Drive
        Your Episodes
        """;

    private static readonly List<Regex> Patterns = Load();

    public static bool IsBlocked(string playlistName) => Patterns.Any(p => p.IsMatch(playlistName.Trim()));

    private static List<Regex> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, Defaults + "\n");
            return File.ReadAllLines(FilePath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => new Regex("^" + Regex.Escape(l).Replace("\\*", ".*") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .ToList();
        }
        catch
        {
            return []; // an unreadable blocklist must never stop the run
        }
    }
}
