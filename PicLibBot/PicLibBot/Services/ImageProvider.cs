﻿using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PicLibBot.Abstractions;
using PicLibBot.Enums;
using PicLibBot.Models;
using Refit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace PicLibBot.Services;

public sealed class ImageProvider : IDisposable
{
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);
    private readonly JpegEncoder _jpegEncoder = new() { Quality = 95, SkipMetadata = true };

    private readonly IOptions<PicLibBotOptions> _options;
    private readonly ILogger<ImageProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ConcurrentDictionary<Guid, LibreYApiMirror> _libreYApiMirrors = [];
    private bool _initialized;

    public ImageProvider(IOptions<PicLibBotOptions> options,
        ILogger<ImageProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _semaphoreSlim.WaitAsync(cancellationToken);

        try
        {
            if (_initialized)
                return;

            await Parallel.ForEachAsync(_options.Value.LibreYApiMirrors,
                new ParallelOptions { CancellationToken = cancellationToken },
                async (baseUrl, currentCancellationToken) =>
            {
                var httpClient = _httpClientFactory.CreateClient(nameof(HttpClientTypes.LibreYCatalog));
                httpClient.BaseAddress = new Uri(baseUrl);
                var api = RestService.For<ILibreYApi>(httpClient);

                var stopwatch = Stopwatch.StartNew();
                var apiResponse = await api.ListImagesAsync("test", 0, currentCancellationToken);
                stopwatch.Stop();

                if (apiResponse.Count > 0)
                {
                    _libreYApiMirrors.TryAdd(Guid.NewGuid(), new LibreYApiMirror(baseUrl, stopwatch.Elapsed));
                }
            });

            _logger.LogInformation("LibreY API mirrors initialized: {Count}, mirrors: {Mirrors}",
                _libreYApiMirrors.Count,
                JsonSerializer.Serialize(_libreYApiMirrors.Values));

            _initialized = true;
        }
        finally
        {
            _semaphoreSlim.Release();
        }
    }

    private async Task<IReadOnlyList<LibreYImageResult>> FetchImageCatalogFromLibreYAsync(string query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(query))
        {
            return ReadOnlyCollection<LibreYImageResult>.Empty;
        }

        var fastestApiMirror = _libreYApiMirrors
            .MinBy(mirror => mirror.Value.ResponseTime);
        if (fastestApiMirror.Value == null)
        {
            throw new SerializationException("No available LibreY API mirrors");
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(HttpClientTypes.LibreYCatalog));
        httpClient.BaseAddress = new Uri(fastestApiMirror.Value.BaseUrl);
        var api = RestService.For<ILibreYApi>(httpClient);

        List<LibreYImageResult> apiResponse = [];
        TimeSpan stopwatchElapsed;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            apiResponse = await api.ListImagesAsync(query, 0, cancellationToken);
            stopwatch.Stop();
            stopwatchElapsed = stopwatch.Elapsed;

            _logger.LogInformation("LibreY API mirror {BaseUrl} responded in {Elapsed}, result count: {Count}",
                fastestApiMirror.Value.BaseUrl,
                stopwatchElapsed,
                apiResponse.Count);
        }
        catch (Exception e)
        {
            stopwatch.Stop();
            stopwatchElapsed = stopwatch.Elapsed;
            stopwatchElapsed += TimeSpan.FromSeconds(2);

            _logger.LogError(e, "Error while calling LibreY API mirror {BaseUrl}", fastestApiMirror.Value.BaseUrl);
        }

        _libreYApiMirrors.TryUpdate(fastestApiMirror.Key,
            fastestApiMirror.Value with { ResponseTime = stopwatchElapsed },
            fastestApiMirror.Value);

        return apiResponse
            .DistinctBy(x => x.Thumbnail)
            .ToList()
            .AsReadOnly();
    }

    private async Task<ImageFile> FetchImageFromExternalSiteAsync(string url, string? alt, CancellationToken cancellationToken = default)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(HttpClientTypes.ExternalContent));
        var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var resultStream = new MemoryStream();
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var sourceImage = await Image.LoadAsync(sourceStream, cancellationToken);

        var width = sourceImage.Width;
        var height = sourceImage.Height;

        await sourceImage.SaveAsJpegAsync(resultStream, _jpegEncoder, cancellationToken);
        resultStream.Position = 0;

        return new ImageFile(resultStream, alt, width, height);
    }

    [SuppressMessage("Blocker Bug", "S2930:\"IDisposables\" should be disposed", Justification = "Captured variable shouldn't be disposed in the outer scope")]
    public async Task<IReadOnlyCollection<string>> FetchAndUploadImagesAsync(ITelegramBotClient botClient, string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        var imageSearchResults = await FetchImageCatalogFromLibreYAsync(query, cancellationToken);

        var parallelForEachCancellationTokenSource = new CancellationTokenSource();
        var parallelForEachCancellationToken = parallelForEachCancellationTokenSource.Token;
        var completedIterations = 0;
        var uploadedFileIds = new ConcurrentBag<string>();

        try
        {
            await Parallel.ForEachAsync(imageSearchResults,
                new ParallelOptions { MaxDegreeOfParallelism = limit, CancellationToken = parallelForEachCancellationToken },
                async (imageSearchResult, token) =>
                {
                    try
                    {
                        var fetchedJpegFile = await FetchImageFromExternalSiteAsync(imageSearchResult.Thumbnail, imageSearchResult.Alt, token);

                        var uploadedPhotoMessage = await botClient.SendPhotoAsync(_options.Value.TelegramCacheChatId,
                            new InputFileStream(fetchedJpegFile.Content),
                            caption: fetchedJpegFile.Alt,
                            cancellationToken: token);

                        if (uploadedPhotoMessage.Photo?.FirstOrDefault() != null)
                        {
                            uploadedFileIds.Add(uploadedPhotoMessage.Photo[0].FileId);

                            Interlocked.Increment(ref completedIterations);

                            if (completedIterations >= limit || cancellationToken.IsCancellationRequested)
                            {
                                await parallelForEachCancellationTokenSource.CancelAsync();
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Ignore
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error while fetching and uploading image {Thumbnail} ({Alt})", imageSearchResult.Thumbnail, imageSearchResult.Alt);
                    }
                });
        }
        catch (TaskCanceledException)
        {
            // Ignore
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while fetching and uploading images");
        }

        return uploadedFileIds;
    }

    public void Dispose()
    {
        _semaphoreSlim.Dispose();
    }
}
