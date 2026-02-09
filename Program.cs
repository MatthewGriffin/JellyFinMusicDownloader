using System.Diagnostics;
using Newtonsoft.Json;

namespace JFSI_MissingMusic_Downloader;

internal static class Program
{
    private const int SimultaneousDownloads = 50;

    private static void Main(string[] args)
    {
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
            var missingTracks = JsonConvert.DeserializeObject<List<MissingMusicObj>>(fileContents);
            if (missingTracks == null) return;

            var downloadedCount = 0;
            while (downloadedCount < missingTracks.Count)
            {
                var indexToWorkTo = Math.Clamp(downloadedCount + SimultaneousDownloads + 1, 0, missingTracks.Count);
                var currentIndex = Math.Clamp(downloadedCount, 0, missingTracks.Count);
                var tasks = missingTracks.GetRange(currentIndex, indexToWorkTo)
                    .Select(track => Task.Run(() => RunCommand(track))).ToList();
                Task.WhenAll(tasks).Wait();
                downloadedCount += SimultaneousDownloads + 1;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("!!!! Exception occurred processing file {0}: {1}", filePath, e.Message);
        }
    }

    private static void RunCommand(MissingMusicObj missingTrack)
    {
        Console.WriteLine("Calling yt-dlp for track {0}", missingTrack.Name + " " + missingTrack.ArtistNames[0]);
        var artist = string.IsNullOrEmpty(missingTrack.ArtistNames[0])
            ? missingTrack.AlbumArtistNames[0]
            : missingTrack.ArtistNames[0];
        artist = artist.RemoveChars();
        var search = missingTrack.Name + " " + artist + " Topic";
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            UseShellExecute = true,
            Arguments = "\"ytsearch1:" + search +
                        "\" --preset-alias mp3 --windows-filenames -o \"" + missingTrack.Name +
                        ".%(ext)s\" -P /home/matthew/Videos/YT/ --embed-metadata --cookies-from-browser firefox"
        };
        var process = Process.Start(psi);
        process?.WaitForExit();
        process?.Close();
        Console.WriteLine("Finished Call");
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
            { ",", "" },
            { "(", "" },
            { ")", "" },
            { "{", "" },
            { "}", "" },
            { "\"", "" },
            { "'", "" }
        };

        return badChars.Aggregate(val, (current, c) => current.Replace(c.Key, c.Value));
    }
}