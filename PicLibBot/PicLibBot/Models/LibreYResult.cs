namespace PicLibBot.Models;

internal sealed class LibreYResult
{
    public required string? MirrorBaseUrl { get; set; }
    public required IReadOnlyList<LibreYImageResult> ImageResults { get; set; }
}
