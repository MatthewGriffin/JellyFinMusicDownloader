namespace JFSI_MissingMusic_Downloader;

public class MissingMusicObj
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string AlbumName { get; init; }
    public required string[] AlbumArtistNames { get; init; }
    public required string[] ArtistNames { get; init; }
}