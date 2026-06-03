using System.Diagnostics;
using Newtonsoft.Json;

namespace JFSI_MissingMusic_Downloader;

internal static class Program
{
    private static void Main(string[] args)
    {
        UpdateDownloader();

        foreach (var path in args)
            if (File.Exists(path))
                ProcessFile(path);
            else if (Directory.Exists(path))
                ProcessFilesInFolder(path);
            else
                Console.WriteLine("{0} is not a valid file or directory", path);

        Console.WriteLine("Finished Downloading");
        Console.WriteLine("Init Cleanup");
        PostCleanup();
    }

    private static void UpdateDownloader()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            UseShellExecute = true,
            Arguments = "-U"
        };
        var process = Process.Start(psi);
        process?.WaitForExit();
        process?.Close();
    }

    private static void ProcessFilesInFolder(string folderPath)
    {
        var files = Directory.GetFiles(folderPath, "*.json");
        foreach (var file in files)
        {
            Console.WriteLine("Processing file {0}", file);
            ProcessFile(file);
            Console.WriteLine("Finished processing file {0}", file);
        }
    }

    private static void ProcessFile(string filePath)
    {
        try
        {
            var fileContents = File.ReadAllText(filePath);
            var deserializeObject = JsonConvert.DeserializeObject<List<PlaylistDef>>(fileContents);
            if (deserializeObject == null) return;

            var missingTracks = deserializeObject[0];
            var songNo = 0;
            foreach (var track in missingTracks.tracks)
            {
                const string saveLocation = "/run/media/ssd3/JFSI_MissingMusic_Downloader/MissingJsons/tracks/";
                var title = track.title.RemoveChars();
                var artist = track.artists[0].RemoveChars();

                if (!File.Exists(saveLocation + title + ".mp3"))
                    RunCommand(title, artist, saveLocation);
                else
                    Console.WriteLine("Song already downloaded skipping. Song: {0}", title + " " + artist);

                songNo += 1;
                var percent = Math.Round(decimal.Divide(songNo, missingTracks.tracks.Count) * 100, 2);
                Console.WriteLine("Progress {0}/{1} {2}%", songNo, missingTracks.tracks.Count, percent
                );
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("!!!! Exception occurred processing file {0}: {1}", filePath, e.Message);
        }
    }

    private static void RunCommand(string title, string artist, string saveLocation)
    {
        var search = title + " " + artist + " Topic";
        Console.WriteLine("Calling yt-dlp with search {0}", search);
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            UseShellExecute = true,
            Arguments = "\"ytsearch:" + search +
                        "\" -t mp3 -t sleep --windows-filenames -o \"" + title + "-" + artist +
                        ".%(ext)s\" -P " + saveLocation +
                        " --embed-metadata --cookies-from-browser firefox"
        };
        var process = Process.Start(psi);
        process?.WaitForExit();
        process?.Close();
    }

    private static void PostCleanup()
    {
        var webmFiles = Directory.GetFiles("/home/matthew/Videos/YT/", "*.webm");
        foreach (var file in webmFiles)
            if (File.Exists(file))
                File.Delete(file);
    }

    private static string RemoveChars(this string val)
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
            { ":", "" }
        };

        return badChars.Aggregate(val, (current, c) => current.Replace(c.Key, c.Value));
    }
}