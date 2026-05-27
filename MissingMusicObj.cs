namespace JFSI_MissingMusic_Downloader;

public class PlaylistDef
{
    public required string id { get; set; }
    public required string name { get; set; }
    public required List<TrackObj> tracks { get; set; }
}

public class TrackObj
{
    public required string id { get; init; }
    public required string title { get; init; }
    public required string album { get; init; }
    public required string[] artists { get; init; }
}