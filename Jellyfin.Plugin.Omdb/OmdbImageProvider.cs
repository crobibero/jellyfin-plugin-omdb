using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Omdb
{
    /// <summary>
    /// Omdb image provider.
    /// </summary>
    public class OmdbImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IFileSystem _fileSystem;
        private readonly IServerConfigurationManager _configurationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="OmdbImageProvider"/> class.
        /// </summary>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        public OmdbImageProvider(
            IHttpClientFactory httpClientFactory,
            IFileSystem fileSystem,
            IServerConfigurationManager configurationManager)
        {
            _httpClientFactory = httpClientFactory;
            _fileSystem = fileSystem;
            _configurationManager = configurationManager;
        }

        /// <inheritdoc />
        public string Name => "The Open Movie Database";

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            yield return ImageType.Primary;
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var imdbId = item.GetProviderId(MetadataProvider.Imdb);

            var list = new List<RemoteImageInfo>();

            var provider = new OmdbProvider(_httpClientFactory, _fileSystem, _configurationManager);

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var rootObject = await provider.GetRootObject(imdbId, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(rootObject.Poster))
                {
                    if (item is Episode)
                    {
                        // img.omdbapi.com is returning 404's
                        list.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Url = rootObject.Poster
                        });
                    }
                    else
                    {
                        list.Add(new RemoteImageInfo
                        {
                            ProviderName = Name,
                            Url = string.Format(CultureInfo.InvariantCulture, "https://img.omdbapi.com/?i={0}&apikey=2c9d9507", imdbId)
                        });
                    }
                }
            }

            return list;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }

        /// <inheritdoc />
        public bool Supports(BaseItem item)
        {
            return item is Movie || item is Trailer || item is Episode;
        }
    }
}
