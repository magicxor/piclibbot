namespace PicLibBot.Models;

public sealed record ImageMetaInfo(string Url,
    string? Format,
    string? Alt,
    int Width,
    int Height);
