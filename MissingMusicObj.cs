namespace JFSI_MissingMusic_Downloader;

public class PlaylistDef
{
    public required string playlist_id { get; set; }
    public required string playlist_name { get; set; }
    public string? owner_id { get; set; }
    public string? owner { get; set; }
    public string? current_user_id { get; set; }   // the Spotify account that was logged in when exported
    public bool collaborative { get; set; }
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
    public string? album_release_date { get; init; }
    public string? album_image_url { get; init; }   // Spotify cover art (640x640)
    public string? album_artist { get; init; }
    public int? track_number { get; init; }
    public int? disc_number { get; init; }
}