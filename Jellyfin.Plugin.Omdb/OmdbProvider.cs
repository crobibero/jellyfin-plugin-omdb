using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Omdb.Dtos;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.Omdb
{
    /// <summary>
    /// Omdb provider.
    /// </summary>
    public class OmdbProvider
    {
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");

        /// <summary>
        /// Initializes a new instance of the <see cref="OmdbProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        public OmdbProvider(
            IHttpClientFactory httpClientFactory,
            IFileSystem fileSystem,
            IServerConfigurationManager configurationManager)
        {
            _httpClientFactory = httpClientFactory;
            _fileSystem = fileSystem;
            _configurationManager = configurationManager;
        }

        /// <summary>
        /// Attempt to fetch object.
        /// </summary>
        /// <param name="itemResult">The metadata result.</param>
        /// <param name="imdbId">The imdb id.</param>
        /// <param name="language">The metadata language.</param>
        /// <param name="country">The metadata country origin.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="T">The type of BaseItem to return.</typeparam>
        /// <returns>The resulting BaseItem.</returns>
        /// <exception cref="ArgumentNullException">ImdbId is null.</exception>
        public async Task Fetch<T>(MetadataResult<T> itemResult, string imdbId, string language, string country, CancellationToken cancellationToken)
            where T : BaseItem
        {
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var item = itemResult.Item;

            var result = await GetRootObject(imdbId, cancellationToken).ConfigureAwait(false);

            // Only take the name and rating if the user's language is set to English, since Omdb has no localization
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) || _configurationManager.Configuration.EnableNewOmdbSupport)
            {
                item.Name = result.Title;

                if (string.Equals(country, "us", StringComparison.OrdinalIgnoreCase))
                {
                    item.OfficialRating = result.Rated;
                }
            }

            if (!string.IsNullOrEmpty(result.Year) && result.Year.Length >= 4
                && int.TryParse(result.Year.AsSpan().Slice(0, 4), NumberStyles.Number, _usCulture, out var year)
                && year >= 0)
            {
                item.ProductionYear = year;
            }

            var tomatoScore = result.GetRottenTomatoScore();

            if (tomatoScore.HasValue)
            {
                item.CriticRating = tomatoScore;
            }

            if (!string.IsNullOrEmpty(result.ImdbVotes)
                && int.TryParse(result.ImdbVotes, NumberStyles.Number, _usCulture, out var voteCount)
                && voteCount >= 0)
            {
                // item.VoteCount = voteCount;
            }

            if (!string.IsNullOrEmpty(result.ImdbRating)
                && float.TryParse(result.ImdbRating, NumberStyles.Any, _usCulture, out var imdbRating)
                && imdbRating >= 0)
            {
                item.CommunityRating = imdbRating;
            }

            if (!string.IsNullOrEmpty(result.Website))
            {
                item.HomePageUrl = result.Website;
            }

            if (!string.IsNullOrWhiteSpace(result.ImdbId))
            {
                item.SetProviderId(MetadataProvider.Imdb, result.ImdbId);
            }

            ParseAdditionalMetadata(itemResult, result);
        }

        /// <summary>
        /// Fetch episode data.
        /// </summary>
        /// <param name="itemResult">The metadata result.</param>
        /// <param name="episodeNumber">The episode number.</param>
        /// <param name="seasonNumber">The season number.</param>
        /// <param name="episodeImdbId">The episode's imdb id.</param>
        /// <param name="seriesImdbId">The series' imdb id.</param>
        /// <param name="language">The metadata language.</param>
        /// <param name="country">The metadata country origin.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="T">The type of BaseItem to return.</typeparam>
        /// <returns>The resulting BaseItem.</returns>
        /// <exception cref="ArgumentNullException">series imdb id is null.</exception>
        public async Task<bool> FetchEpisodeData<T>(MetadataResult<T> itemResult, int episodeNumber, int seasonNumber, string episodeImdbId, string seriesImdbId, string language, string country, CancellationToken cancellationToken)
            where T : BaseItem
        {
            if (string.IsNullOrWhiteSpace(seriesImdbId))
            {
                throw new ArgumentNullException(nameof(seriesImdbId));
            }

            var item = itemResult.Item;

            var seasonResult = await GetSeasonRootObject(seriesImdbId, seasonNumber, cancellationToken).ConfigureAwait(false);

            if (seasonResult?.Episodes == null)
            {
                return false;
            }

            RootObject? result = null;

            if (!string.IsNullOrWhiteSpace(episodeImdbId))
            {
                foreach (var episode in seasonResult.Episodes)
                {
                    if (string.Equals(episodeImdbId, episode.ImdbId, StringComparison.OrdinalIgnoreCase))
                    {
                        result = episode;
                        break;
                    }
                }
            }

            // finally, search by numbers
            if (result == null)
            {
                foreach (var episode in seasonResult.Episodes)
                {
                    if (episode.Episode == episodeNumber)
                    {
                        result = episode;
                        break;
                    }
                }
            }

            if (result == null)
            {
                return false;
            }

            // Only take the name and rating if the user's language is set to English, since Omdb has no localization
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) || _configurationManager.Configuration.EnableNewOmdbSupport)
            {
                item.Name = result.Title;

                if (string.Equals(country, "us", StringComparison.OrdinalIgnoreCase))
                {
                    item.OfficialRating = result.Rated;
                }
            }

            if (!string.IsNullOrEmpty(result.Year) && result.Year.Length >= 4
                && int.TryParse(result.Year.AsSpan().Slice(0, 4), NumberStyles.Number, _usCulture, out var year)
                && year >= 0)
            {
                item.ProductionYear = year;
            }

            var tomatoScore = result.GetRottenTomatoScore();

            if (tomatoScore.HasValue)
            {
                item.CriticRating = tomatoScore;
            }

            if (!string.IsNullOrEmpty(result.ImdbVotes)
                && int.TryParse(result.ImdbVotes, NumberStyles.Number, _usCulture, out var voteCount)
                && voteCount >= 0)
            {
                // item.VoteCount = voteCount;
            }

            if (!string.IsNullOrEmpty(result.ImdbRating)
                && float.TryParse(result.ImdbRating, NumberStyles.Any, _usCulture, out var imdbRating)
                && imdbRating >= 0)
            {
                item.CommunityRating = imdbRating;
            }

            if (!string.IsNullOrEmpty(result.Website))
            {
                item.HomePageUrl = result.Website;
            }

            if (!string.IsNullOrWhiteSpace(result.ImdbId))
            {
                item.SetProviderId(MetadataProvider.Imdb, result.ImdbId);
            }

            ParseAdditionalMetadata(itemResult, result);

            return true;
        }

        internal async Task<RootObject> GetRootObject(string imdbId, CancellationToken cancellationToken)
        {
            var path = await EnsureItemInfo(imdbId, cancellationToken).ConfigureAwait(false);

            string resultString;

            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            {
                resultString = await reader.ReadToEndAsync().ConfigureAwait(false);
                resultString = resultString.Replace("\"N/A\"", "\"\"", StringComparison.OrdinalIgnoreCase);
            }

            var result = JsonSerializer.Deserialize<RootObject>(resultString, JsonDefaults.GetOptions());
            return result ?? new RootObject();
        }

        internal async Task<SeasonRootObject> GetSeasonRootObject(string imdbId, int seasonId, CancellationToken cancellationToken)
        {
            var path = await EnsureSeasonInfo(imdbId, seasonId, cancellationToken).ConfigureAwait(false);

            string resultString;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    resultString = reader.ReadToEnd();
                    resultString = resultString.Replace("\"N/A\"", "\"\"", StringComparison.OrdinalIgnoreCase);
                }
            }

            var result = JsonSerializer.Deserialize<SeasonRootObject>(resultString, JsonDefaults.GetOptions());
            return result ?? new SeasonRootObject();
        }

        internal static bool IsValidSeries(Dictionary<string, string> seriesProviderIds)
        {
            if (seriesProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var id) && !string.IsNullOrEmpty(id))
            {
                // This check should ideally never be necessary but we're seeing some cases of this and haven't tracked them down yet.
                if (!string.IsNullOrWhiteSpace(id))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get the full omdb url from query.
        /// </summary>
        /// <param name="query">The query to append.</param>
        /// <returns>The full omdb url.</returns>
        public static Uri GetOmdbUrl(string query)
        {
            const string Url = "https://www.omdbapi.com?apikey=2c9d9507";
            return string.IsNullOrWhiteSpace(query) ? new Uri(Url) : new Uri(Url + "&" + query);
        }

        private async Task<string> EnsureItemInfo(string imdbId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var imdbParam = imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdbId : "tt" + imdbId;

            var path = GetDataFilePath(imdbParam);

            var fileInfo = _fileSystem.GetFileSystemInfo(path);

            if (fileInfo.Exists)
            {
                // If it's recent or automatic updates are enabled, don't re-download
                if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 1)
                {
                    return path;
                }
            }

            var url = GetOmdbUrl(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "i={0}&plot=short&tomatoes=true&r=json",
                    imdbParam));

            var jsonOptions = JsonDefaults.GetOptions();
            var rootObject = await GetDeserializedOmdbResponse<RootObject>(_httpClientFactory.CreateClient(NamedClient.Default), url, jsonOptions, cancellationToken).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(path) ?? throw new ResourceNotFoundException($"Provided path ({path}) is not valid.");
            Directory.CreateDirectory(directory);
            await using FileStream jsonFileStream = File.Create(path);
            await JsonSerializer.SerializeAsync(jsonFileStream, rootObject, jsonOptions, cancellationToken).ConfigureAwait(false);

            return path;
        }

        private async Task<string> EnsureSeasonInfo(string seriesImdbId, int seasonId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(seriesImdbId))
            {
                throw new ArgumentException("The series IMDb ID was null or whitespace.", nameof(seriesImdbId));
            }

            var imdbParam = seriesImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? seriesImdbId : "tt" + seriesImdbId;

            var path = GetSeasonFilePath(imdbParam, seasonId);

            var fileInfo = _fileSystem.GetFileSystemInfo(path);

            if (fileInfo.Exists)
            {
                // If it's recent or automatic updates are enabled, don't re-download
                if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 1)
                {
                    return path;
                }
            }

            var url = GetOmdbUrl(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "i={0}&season={1}&detail=full",
                    imdbParam,
                    seasonId));

            var jsonOptions = JsonDefaults.GetOptions();
            var rootObject = await GetDeserializedOmdbResponse<SeasonRootObject>(_httpClientFactory.CreateClient(NamedClient.Default), url, jsonOptions, cancellationToken).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(path) ?? throw new ResourceNotFoundException($"Provided path ({path}) is not valid.");
            Directory.CreateDirectory(directory);
            await using FileStream jsonFileStream = File.Create(path);
            await JsonSerializer.SerializeAsync(jsonFileStream, rootObject, jsonOptions, cancellationToken).ConfigureAwait(false);

            return path;
        }

        /// <summary>
        /// Get an omdb response already deserialized into an object.
        /// </summary>
        /// <param name="httpClient">The http client.</param>
        /// <param name="url">The url to request.</param>
        /// <param name="jsonOptions">The json serialization options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <typeparam name="T">The object in which the json response should be deserialized.</typeparam>
        /// <returns>The omdb response deserialized into an object.</returns>
        public static async Task<T?> GetDeserializedOmdbResponse<T>(HttpClient httpClient, Uri url, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            using var response = await GetOmdbResponse(httpClient, url, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // OMDb is sending "N/A" for no empty number fields
            content = content.Replace("\"N/A\"", "\"0\"", StringComparison.InvariantCulture);
            return JsonSerializer.Deserialize<T>(content, jsonOptions);
        }

        /// <summary>
        /// Get an omdb response.
        /// </summary>
        /// <param name="httpClient">The http client.</param>
        /// <param name="url">The url to request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The http response message.</returns>
        public static Task<HttpResponseMessage> GetOmdbResponse(HttpClient httpClient, Uri url, CancellationToken cancellationToken)
        {
            return httpClient.GetAsync(url, cancellationToken);
        }

        internal string GetDataFilePath(string imdbId)
        {
            if (string.IsNullOrEmpty(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var dataPath = Path.Combine(_configurationManager.ApplicationPaths.CachePath, "omdb");

            var filename = string.Format(CultureInfo.InvariantCulture, "{0}.json", imdbId);

            return Path.Combine(dataPath, filename);
        }

        internal string GetSeasonFilePath(string imdbId, int seasonId)
        {
            if (string.IsNullOrEmpty(imdbId))
            {
                throw new ArgumentNullException(nameof(imdbId));
            }

            var dataPath = Path.Combine(_configurationManager.ApplicationPaths.CachePath, "omdb");

            var filename = string.Format(CultureInfo.InvariantCulture, "{0}_season_{1}.json", imdbId, seasonId);

            return Path.Combine(dataPath, filename);
        }

        private void ParseAdditionalMetadata<T>(MetadataResult<T> itemResult, RootObject result)
            where T : BaseItem
        {
            var item = itemResult.Item;

            var isConfiguredForEnglish = IsConfiguredForEnglish(item) || _configurationManager.Configuration.EnableNewOmdbSupport;

            // Grab series genres because IMDb data is better than TVDB. Leave movies alone
            // But only do it if English is the preferred language because this data will not be localized
            if (isConfiguredForEnglish && !string.IsNullOrWhiteSpace(result.Genre))
            {
                item.Genres = Array.Empty<string>();

                foreach (var genre in result.Genre
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(i => i.Trim())
                    .Where(i => !string.IsNullOrWhiteSpace(i)))
                {
                    item.AddGenre(genre);
                }
            }

            if (isConfiguredForEnglish)
            {
                // Omdb is currently English only, so for other languages skip this and let secondary providers fill it in
                item.Overview = result.Plot;
            }

            if (!OmdbPlugin.Instance?.Configuration.CastAndCrew ?? false)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Director))
            {
                var person = new PersonInfo
                {
                    Name = result.Director.Trim(),
                    Type = PersonType.Director
                };

                itemResult.AddPerson(person);
            }

            if (!string.IsNullOrWhiteSpace(result.Writer))
            {
                var person = new PersonInfo
                {
                    Name = result.Writer.Trim(),
                    Type = PersonType.Writer
                };

                itemResult.AddPerson(person);
            }

            if (!string.IsNullOrWhiteSpace(result.Actors))
            {
                var actorList = result.Actors.Split(',');
                foreach (var actor in actorList)
                {
                    if (!string.IsNullOrWhiteSpace(actor))
                    {
                        var person = new PersonInfo
                        {
                            Name = actor.Trim(),
                            Type = PersonType.Actor
                        };

                        itemResult.AddPerson(person);
                    }
                }
            }
        }

        private bool IsConfiguredForEnglish(BaseItem item)
        {
            var lang = item.GetPreferredMetadataLanguage();

            // The data isn't localized and so can only be used for English users
            return string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        }
    }
}
