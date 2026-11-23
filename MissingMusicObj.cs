namespace JFSI_MissingMusic_Downloader;

public class PlaylistDef
{
    public required string playlist_id { get; set; }
    public required string playlist_name { get; set; }
    public required List<TrackObj> tracks { get; set; }
}

public class TrackObj
{
    public required string position { get; init; }
    public required string name { get; init; }
    public required string album { get; init; }
    public required string artist { get; init; }
    public long? duration_ms { get; init; }
    public string? isrc { get; init; }
}