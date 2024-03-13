using System.ComponentModel.DataAnnotations;

namespace PicLibBot.Models;

public sealed class PicLibBotOptions
{
    [Required]
    [RegularExpression(@".*:.*")]
    public required string TelegramBotApiKey { get; init; }

    [Required]
    public required long TelegramCacheChatId { get; init; }

    [Required]
    [MinLength(1)]
    public required IReadOnlyCollection<string> LibreYApiMirrors { get; init; }
}
