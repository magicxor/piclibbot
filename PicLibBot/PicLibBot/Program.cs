using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using PicLibBot.Enums;
using PicLibBot.Exceptions;
using PicLibBot.Extensions;
using PicLibBot.Models;
using PicLibBot.Services;
using Telegram.Bot;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PicLibBot;

public static class Program
{
    private static readonly LoggingConfiguration LoggingConfiguration = new XmlLoggingConfiguration("nlog.config");

    public static void Main(string[] args)
    {
        // NLog: setup the logger first to catch all errors
        LogManager.Configuration = LoggingConfiguration;
        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) =>
                {
                    config
                        .AddEnvironmentVariables("PICLIBBOT_")
                        .AddJsonFile("appsettings.json", optional: true);
                })
                .ConfigureLogging(loggingBuilder =>
                {
                    loggingBuilder.ClearProviders();
                    loggingBuilder.SetMinimumLevel(LogLevel.Trace);
                    loggingBuilder.AddNLog(LoggingConfiguration);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services
                        .AddOptions<PicLibBotOptions>()
                        .Bind(hostContext.Configuration.GetSection(nameof(OptionSections.PicLibBot)))
                        .ValidateDataAnnotations()
                        .ValidateOnStart();

                    services.AddHttpClient(nameof(HttpClientTypes.Telegram))
                        .AddPolicyHandler(HttpPolicyProvider.TelegramCombinedPolicy)
                        .AddDefaultLogger();

                    services.AddHttpClient(nameof(HttpClientTypes.LibreYCatalog))
                        .AddPolicyHandler(HttpPolicyProvider.LibreYCombinedPolicy)
                        .AddDefaultLogger();

                    services.AddHttpClient(nameof(HttpClientTypes.ExternalContent))
                        .AddPolicyHandler(HttpPolicyProvider.ExternalContentCombinedPolicy)
                        .AddDefaultLogger();

                    var telegramBotApiKey = hostContext.Configuration.GetTelegramBotApiKey()
                                            ?? throw new ServiceException("Telegram bot API key is missing");
                    services.AddScoped<ITelegramBotClient, TelegramBotClient>(s => new TelegramBotClient(telegramBotApiKey,
                        s.GetRequiredService<IHttpClientFactory>()
                            .CreateClient(nameof(HttpClientTypes.Telegram))));

                    services.AddScoped<ImageProvider>();
                    services.AddScoped<TelegramBotService>();
                    services.AddHostedService<Worker>();
                })
                .Build();

            host.Run();
        }
        catch (Exception ex)
        {
            // NLog: catch setup errors
            LogManager.GetCurrentClassLogger().Error(ex, "Stopped program because of exception");
            throw;
        }
        finally
        {
            // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
            LogManager.Shutdown();
        }
    }
}
