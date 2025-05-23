﻿using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PicLibBot.Abstractions;
using PicLibBot.Enums;
using PicLibBot.Exceptions;
using PicLibBot.Models;
using Refit;
using SixLabors.ImageSharp;

namespace PicLibBot.Services;

internal sealed class ImageProvider : IDisposable
{
    private readonly SemaphoreSlim _semaphoreSlim = new(1, 1);

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
                try
                {
                    var httpClient = _httpClientFactory.CreateClient(nameof(HttpClientTypes.LibreYCatalog));
                    httpClient.BaseAddress = new Uri(baseUrl);
                    var api = RestService.For<ILibreYApi>(httpClient);

                    var stopwatch = Stopwatch.StartNew();
                    var apiResponse = JsonSerializer.Deserialize<List<LibreYImageResult>>(
                        await api.ListImagesAsync("test", 0, currentCancellationToken)) ?? [];
                    stopwatch.Stop();

                    if (apiResponse.Count > 0)
                    {
                        _libreYApiMirrors.TryAdd(Guid.NewGuid(), new LibreYApiMirror(baseUrl, stopwatch.Elapsed));
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Error while initializing LibreY API mirror {BaseUrl}", baseUrl);
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

    private async Task<LibreYResult> FetchImageCatalogFromLibreYAsync(string query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(query))
        {
            return new LibreYResult
            {
                MirrorBaseUrl = null,
                ImageResults = ReadOnlyCollection<LibreYImageResult>.Empty,
            };
        }

        var fastestApiMirror = _libreYApiMirrors
            .MinBy(mirror => mirror.Value.ResponseTime);
        if (fastestApiMirror.Value == null)
        {
            throw new ServiceException("No available LibreY API mirrors");
        }

        var httpClient = _httpClientFactory.CreateClient(nameof(HttpClientTypes.LibreYCatalog));
        httpClient.BaseAddress = new Uri(fastestApiMirror.Value.BaseUrl);
        var api = RestService.For<ILibreYApi>(httpClient);

        List<LibreYImageResult> apiResponse = [];
        TimeSpan stopwatchElapsed;
        var stopwatch = Stopwatch.StartNew();
        string stringResponse = string.Empty;
        try
        {
            stringResponse = await api.ListImagesAsync(query, 0, cancellationToken);
            apiResponse = stringResponse.Contains("No results found. Unable to fallback", StringComparison.OrdinalIgnoreCase)
                ? []
                : JsonSerializer.Deserialize<List<LibreYImageResult>>(stringResponse) ?? [];
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

            _logger.LogError(e,
                "Error while calling LibreY API mirror {BaseUrl}; response: {Response}",
                fastestApiMirror.Value.BaseUrl,
                stringResponse);
        }

        _libreYApiMirrors.TryUpdate(fastestApiMirror.Key,
            fastestApiMirror.Value with { ResponseTime = stopwatchElapsed },
            fastestApiMirror.Value);
        _logger.LogInformation("LibreY API mirrors updated: {Mirrors}",
            JsonSerializer.Serialize(_libreYApiMirrors.Values));

        var imageResults = apiResponse
            .DistinctBy(x => x.Thumbnail)
            .ToList()
            .AsReadOnly();

        return new LibreYResult
        {
            MirrorBaseUrl = fastestApiMirror.Value.BaseUrl,
            ImageResults = imageResults,
        };
    }

    private async Task<ImageMetaInfo> FetchImageFromExternalSiteAsync(Uri url, string? alt, CancellationToken cancellationToken = default)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(HttpClientTypes.ExternalContent));
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var sourceImage = await Image.LoadAsync(sourceStream, cancellationToken);

        var width = sourceImage.Width;
        var height = sourceImage.Height;
        var format = sourceImage.Metadata.DecodedImageFormat?.Name;

        return new ImageMetaInfo(url.ToString(), format, alt, width, height);
    }

    public async Task<FetchImagesResult> FetchImagesAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var searchResult = await FetchImageCatalogFromLibreYAsync(query, cancellationToken);
        stopwatch.Stop();

        var timeoutInSeconds = _options.Value.ImagesFetchTimeoutInSeconds - stopwatch.Elapsed.TotalSeconds;
        timeoutInSeconds = timeoutInSeconds > 0 ? timeoutInSeconds : _options.Value.ImagesFetchTimeoutInSeconds;

        using var parallelForEachCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutInSeconds));
        var parallelForEachCancellationToken = parallelForEachCancellationTokenSource.Token;
        var completedIterations = 0;
        var images = new ConcurrentBag<ImageMetaInfo>();

        try
        {
            await Parallel.ForEachAsync(searchResult.ImageResults,
                new ParallelOptions { CancellationToken = parallelForEachCancellationToken },
                async (imageSearchResult, token) =>
                {
                    try
                    {
                        if (Uri.TryCreate(imageSearchResult.Thumbnail, UriKind.Absolute, out var thumbnailUri))
                        {
                            var fileMetaInfo = await FetchImageFromExternalSiteAsync(thumbnailUri, imageSearchResult.Alt, token);
                            if ((decimal)fileMetaInfo.Width / (decimal)fileMetaInfo.Height is >= 1.0m and <= 2.3m
                                || (decimal)fileMetaInfo.Height / (decimal)fileMetaInfo.Width is >= 1.0m and <= 2.3m)
                            {
                                images.Add(fileMetaInfo);
                                Interlocked.Increment(ref completedIterations);
                                if (completedIterations >= limit || cancellationToken.IsCancellationRequested)
                                {
                                    await parallelForEachCancellationTokenSource.CancelAsync();
                                }
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

        return new FetchImagesResult
        {
            SearchResultsCount = searchResult.ImageResults.Count,
            MirrorBaseUrl = searchResult.MirrorBaseUrl,
            ImageMetaInfoCollection = images,
        };
    }

    public void Dispose()
    {
        _semaphoreSlim.Dispose();
    }
}
