using System.Diagnostics;
using Newtonsoft.Json;

namespace JFSI_MissingMusic_Downloader;

internal static class Program
{
    private const int NoOfThreadsToOpenAtOnce = 50;

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
        var tasks = new List<Task>();
        foreach (var task in files.Select(file => new Task(() => { ProcessFile(file); })))
        {
            task.Start();
            tasks.Add(task);
        }

        if (tasks.Count <= NoOfThreadsToOpenAtOnce)
            Task.WhenAll(tasks).Wait();
    }

    private static void ProcessFile(string filePath)
    {
        try
        {
            var fileContents = File.ReadAllText(filePath);
            var missingTracks = JsonConvert.DeserializeObject<List<MissingMusicObj>>(fileContents);
            if (missingTracks == null) return;

            var dupTracks = missingTracks.Where(t =>
                File.Exists("/home/matthew/Videos/YT/" + t.Name + " " + t.ArtistNames[0] + ".webm") ||
                File.Exists("/home/matthew/Videos/YT/" + t.Name + " " + t.ArtistNames[0] + ".mp3"));
            var tracksToDownload = missingTracks.Where(mt => !dupTracks.Contains(mt)).ToList();

            var tasks = new List<Task>();
            foreach (var task in tracksToDownload.Select(track => new Task(async () => { RunCommand(track); })))
            {
                task.Start();
                tasks.Add(task);
            }

            if (tasks.Count <= NoOfThreadsToOpenAtOnce)
                Task.WhenAll(tasks).Wait();
        }
        catch (Exception e)
        {
            Console.WriteLine("!!!! Exception occurred processing file {0}: {1}", filePath, e.Message);
        }
    }

    private static void RunCommand(MissingMusicObj missingTrack)
    {
        Console.WriteLine("Calling yt-dlp....");
        var artist = string.IsNullOrEmpty(missingTrack.AlbumArtistNames[0])
            ? missingTrack.ArtistNames[0]
            : missingTrack.AlbumArtistNames[0];
        var search = missingTrack.Name + " " + artist;
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            UseShellExecute = true,
            Arguments = "\"ytsearch1:" + search +
                        "\" --preset-alias mp3 --windows-filenames -o \"" + missingTrack.Name +
                        ".%(ext)s\" -P /home/matthew/Videos/YT/ --parse-metadata \"" + missingTrack.Name +
                        " :%(title)s\" --parse-metadata \"" + artist +
                        " :%(artist)s\" --embed-metadata"
        };
        var process = Process.Start(psi);
        process?.WaitForExit();
        process?.Close();
        Console.WriteLine("Finished Call");
    }

    private static void PostCleanup()
    {
        var webmFiles = Directory.GetFiles("/home/matthew/Videos/YT/", "*.webm");
        foreach (var file in webmFiles) File.Delete(file);
    }
}