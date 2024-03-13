using System.Text.Json.Serialization;

namespace PicLibBot.Models;

public sealed class LibreYImageResult
{
    [JsonPropertyName("thumbnail")]
    public required string Thumbnail { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("alt")]
    public string? Alt { get; set; }
}
