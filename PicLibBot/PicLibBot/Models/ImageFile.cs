namespace PicLibBot.Models;

public sealed record ImageFile(Stream Content,
    string? Alt,
    int Width,
    int Height);
