using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PicLibBot.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;

namespace PicLibBot.Services;

public sealed class TelegramBotService
{
    private static readonly ReceiverOptions ReceiverOptions = new()
    {
        AllowedUpdates = [UpdateType.InlineQuery],
    };

    private readonly ILogger<TelegramBotService> _logger;
    private readonly IOptions<PicLibBotOptions> _options;
    private readonly ITelegramBotClient _telegramBotClient;
    private readonly ImageProvider _imageProvider;

    public TelegramBotService(ILogger<TelegramBotService> logger,
        IOptions<PicLibBotOptions> options,
        ITelegramBotClient telegramBotClient,
        ImageProvider imageProvider)
    {
        _logger = logger;
        _options = options;
        _telegramBotClient = telegramBotClient;
        _imageProvider = imageProvider;
    }

    private Task HandleUpdateAsync(ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Received update with type={Update}", update.Type.ToString());

        // ReSharper disable once AsyncVoidLambda
        ThreadPool.QueueUserWorkItem(async _ => await HandleUpdateFunctionAsync(botClient, update, cancellationToken));

        return Task.CompletedTask;
    }

    private async Task HandleUpdateFunctionAsync(ITelegramBotClient botClient,
        Update update,
        CancellationToken cancellationToken)
    {
        try
        {
            if (update.InlineQuery is { } inlineQuery)
            {
                _logger.LogInformation("Inline query received. Query (length: {QueryLength}): {Query}",
                    inlineQuery.Query.Length,
                    inlineQuery.Query);

                var maxInlineQueryResults = _options.Value.MaxInlineResults;
                var imagesMetadata = await _imageProvider.FetchImagesAsync(inlineQuery.Query.Trim(), maxInlineQueryResults, cancellationToken);

                var inlineResults = imagesMetadata
                    .Select((img, i) => new InlineQueryResultPhoto(
                        $"{i}_{DateTime.UtcNow.ToString("yyyy-MM-dd_HH", CultureInfo.InvariantCulture)}",
                        img.Url,
                        img.Url)
                    {
                        PhotoWidth = img.Width,
                        PhotoHeight = img.Height,
                    })
                    .ToList();

                var cacheTime = inlineResults.Count > 0 ? (int)TimeSpan.FromDays(7).TotalSeconds : 0;
                await botClient.AnswerInlineQuery(inlineQuery.Id, inlineResults, cacheTime, false, cancellationToken: cancellationToken);
                _logger.LogInformation("Inline query answered. Sent {Count} results", inlineResults.Count);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while handling update");
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient botClient,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is ApiRequestException apiRequestException)
        {
            _logger.LogError(exception,
                "Telegram API Error. ErrorCode={ErrorCode}, RetryAfter={RetryAfter}, MigrateToChatId={MigrateToChatId}",
                apiRequestException.ErrorCode,
                apiRequestException.Parameters?.RetryAfter,
                apiRequestException.Parameters?.MigrateToChatId);
        }
        else
        {
            _logger.LogError(exception, @"Telegram API Error");
        }

        return Task.CompletedTask;
    }

    public void Start(CancellationToken cancellationToken)
    {
        _telegramBotClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: ReceiverOptions,
            cancellationToken: cancellationToken
        );
    }
}
