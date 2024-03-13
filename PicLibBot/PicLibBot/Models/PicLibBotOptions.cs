using System.ComponentModel.DataAnnotations;

namespace PicLibBot.Models;

public sealed class PicLibBotOptions
{
    [Required]
    [RegularExpression(@".*:.*")]
    public required string TelegramBotApiKey { get; init; }

    [Required]
    [Range(2, 10)]
    public required int ImagesFetchTimeoutInSeconds { get; set; }

    [Required]
    [Range(3, 50)]
    public required int MaxInlineResults { get; set; }

    [Required]
    [MinLength(1)]
    public required IReadOnlyCollection<string> LibreYApiMirrors { get; init; }
}
