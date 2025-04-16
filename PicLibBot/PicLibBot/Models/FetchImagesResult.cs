namespace PicLibBot.Models;

internal sealed class FetchImagesResult
{
    public required int SearchResultsCount { get; set; }
    public required string? MirrorBaseUrl { get; set; }
    public required IReadOnlyCollection<ImageMetaInfo> ImageMetaInfoCollection { get; set; }
}
