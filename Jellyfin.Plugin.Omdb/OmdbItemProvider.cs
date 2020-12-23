using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Omdb.Dtos;
using MediaBrowser.Common.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Omdb
{
    /// <summary>
    /// Omdb item provider.
    /// </summary>
    public class OmdbItemProvider : IRemoteMetadataProvider<Series, SeriesInfo>,
        IRemoteMetadataProvider<Movie, MovieInfo>, IRemoteMetadataProvider<Trailer, TrailerInfo>
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _configurationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="OmdbItemProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        public OmdbItemProvider(
            IHttpClientFactory httpClientFactory,
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IServerConfigurationManager configurationManager)
        {
            _httpClientFactory = httpClientFactory;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _configurationManager = configurationManager;
        }

        /// <inheritdoc />
        public string Name => "The Open Movie Database";

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            return GetSearchResults(searchInfo, "series", cancellationToken);
        }

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            return GetSearchResults(searchInfo, "movie", cancellationToken);
        }

        /// <summary>
        /// Get search results.
        /// </summary>
        /// <param name="searchInfo">The item lookup info.</param>
        /// <param name="type">The search type.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The remote search result.</returns>
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ItemLookupInfo searchInfo, string type, CancellationToken cancellationToken)
        {
            return GetSearchResultsInternal(searchInfo, type, true, cancellationToken);
        }

        private async Task<IEnumerable<RemoteSearchResult>> GetSearchResultsInternal(ItemLookupInfo searchInfo, string type, bool isSearch, CancellationToken cancellationToken)
        {
            var episodeSearchInfo = searchInfo as EpisodeInfo;

            var imdbId = searchInfo.GetProviderId(MetadataProvider.Imdb);

            var urlQuery = "plot=full&r=json";
            if (type == "episode")
            {
                episodeSearchInfo?.SeriesProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out imdbId);
            }

            var name = searchInfo.Name;
            var year = searchInfo.Year;

            if (!string.IsNullOrWhiteSpace(name))
            {
                var parsedName = _libraryManager.ParseName(name);
                var yearInName = parsedName.Year;
                name = parsedName.Name;
                year ??= yearInName;
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                if (year.HasValue)
                {
                    urlQuery += "&y=" + year.Value.ToString(CultureInfo.InvariantCulture);
                }

                // &s means search and returns a list of results as opposed to t
                if (isSearch)
                {
                    urlQuery += "&s=" + WebUtility.UrlEncode(name);
                }
                else
                {
                    urlQuery += "&t=" + WebUtility.UrlEncode(name);
                }

                urlQuery += "&type=" + type;
            }
            else
            {
                urlQuery += "&i=" + imdbId;
                isSearch = false;
            }

            if (type == "episode")
            {
                if (searchInfo.IndexNumber.HasValue)
                {
                    urlQuery += string.Format(CultureInfo.InvariantCulture, "&Episode={0}", searchInfo.IndexNumber);
                }

                if (searchInfo.ParentIndexNumber.HasValue)
                {
                    urlQuery += string.Format(CultureInfo.InvariantCulture, "&Season={0}", searchInfo.ParentIndexNumber);
                }
            }

            var url = OmdbProvider.GetOmdbUrl(urlQuery);

            using var response = await OmdbProvider.GetOmdbResponse(_httpClientFactory.CreateClient(NamedClient.Default), url, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var resultList = new List<SearchResult>();

            if (isSearch)
            {
                var searchResultList = await JsonSerializer.DeserializeAsync<SearchResultList>(stream, JsonDefaults.GetOptions(), cancellationToken).ConfigureAwait(false);
                if (searchResultList != null)
                {
                    resultList.AddRange(searchResultList.Search);
                }
            }
            else
            {
                var result = await JsonSerializer.DeserializeAsync<SearchResult>(stream, JsonDefaults.GetOptions(), cancellationToken).ConfigureAwait(false);
                if (result != null && string.Equals(result.Response, "true", StringComparison.OrdinalIgnoreCase))
                {
                    resultList.Add(result);
                }
            }

            return resultList.Select(result =>
            {
                var item = new RemoteSearchResult
                {
                    IndexNumber = searchInfo.IndexNumber,
                    Name = result.Title,
                    ParentIndexNumber = searchInfo.ParentIndexNumber,
                    SearchProviderName = Name
                };

                if (episodeSearchInfo != null && episodeSearchInfo.IndexNumberEnd.HasValue)
                {
                    item.IndexNumberEnd = episodeSearchInfo.IndexNumberEnd.Value;
                }

                if (!string.IsNullOrEmpty(result.ImdbId))
                {
                    item.SetProviderId(MetadataProvider.Imdb, result.ImdbId);
                }

                if (!string.IsNullOrEmpty(result.Year)
                    && int.TryParse(result.Year.AsSpan().Slice(0, Math.Min(result.Year.Length, 4)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
                {
                    item.ProductionYear = parsedYear;
                }

                if (!string.IsNullOrEmpty(result.Released)
                    && DateTime.TryParse(result.Released, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var released))
                {
                    item.PremiereDate = released;
                }

                if (!string.IsNullOrWhiteSpace(result.Poster) && !string.Equals(result.Poster, "N/A", StringComparison.OrdinalIgnoreCase))
                {
                    item.ImageUrl = result.Poster;
                }

                return item;
            });
        }

        /// <inheritdoc />
        public Task<MetadataResult<Trailer>> GetMetadata(TrailerInfo info, CancellationToken cancellationToken)
        {
            return GetMovieResult<Trailer>(info, cancellationToken);
        }

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TrailerInfo searchInfo, CancellationToken cancellationToken)
        {
            return GetSearchResults(searchInfo, "movie", cancellationToken);
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>
            {
                Item = new Series(),
                QueriedById = true
            };

            var imdbId = info.GetProviderId(MetadataProvider.Imdb);
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                imdbId = await GetSeriesImdbId(info, cancellationToken).ConfigureAwait(false);
                result.QueriedById = false;
            }

            if (!string.IsNullOrEmpty(imdbId))
            {
                result.Item.SetProviderId(MetadataProvider.Imdb, imdbId);
                result.HasMetadata = true;

                await new OmdbProvider(_httpClientFactory, _fileSystem, _configurationManager)
                    .Fetch(result, imdbId, info.MetadataLanguage, info.MetadataCountryCode, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        /// <inheritdoc />
        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            return GetMovieResult<Movie>(info, cancellationToken);
        }

        private async Task<MetadataResult<T>> GetMovieResult<T>(ItemLookupInfo info, CancellationToken cancellationToken)
            where T : BaseItem, new()
        {
            var result = new MetadataResult<T>
            {
                Item = new T(),
                QueriedById = true
            };

            var imdbId = info.GetProviderId(MetadataProvider.Imdb);
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                imdbId = await GetMovieImdbId(info, cancellationToken).ConfigureAwait(false);
                result.QueriedById = false;
            }

            if (!string.IsNullOrEmpty(imdbId))
            {
                result.Item.SetProviderId(MetadataProvider.Imdb, imdbId);
                result.HasMetadata = true;

                await new OmdbProvider(_httpClientFactory, _fileSystem, _configurationManager)
                    .Fetch(result, imdbId, info.MetadataLanguage, info.MetadataCountryCode, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<string?> GetMovieImdbId(ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var results = await GetSearchResultsInternal(info, "movie", false, cancellationToken).ConfigureAwait(false);
            var first = results.FirstOrDefault();
            return first?.GetProviderId(MetadataProvider.Imdb);
        }

        private async Task<string?> GetSeriesImdbId(SeriesInfo info, CancellationToken cancellationToken)
        {
            var results = await GetSearchResultsInternal(info, "series", false, cancellationToken).ConfigureAwait(false);
            var first = results.FirstOrDefault();
            return first?.GetProviderId(MetadataProvider.Imdb);
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }
    }
}
