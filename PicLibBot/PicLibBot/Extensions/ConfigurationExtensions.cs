using Microsoft.Extensions.Configuration;
using PicLibBot.Enums;
using PicLibBot.Models;

namespace PicLibBot.Extensions;

internal static class ConfigurationExtensions
{
    public static string? GetTelegramBotApiKey(this IConfiguration configuration)
    {
        return configuration.GetSection(nameof(OptionSections.PicLibBot)).GetValue<string>(nameof(PicLibBotOptions.TelegramBotApiKey));
    }
}
