using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Common;
using MediaBrowser.Model.Net;
using System.Xml;
using System.IO.Compression;
using MediaBrowser.Model.Globalization;
using System.Globalization;

namespace Podnapisi
{
    public class PodnapisiSubtitleProvider : ISubtitleProvider, IHasOrder
    {
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly IApplicationHost _appHost;
        private ILocalizationManager _localizationManager;

        public PodnapisiSubtitleProvider(ILogger logger, IHttpClient httpClient, IFileSystem fileSystem,
            IApplicationHost appHost, ILocalizationManager localizationManager)
        {
            _logger = logger;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
            _appHost = appHost;
            _localizationManager = localizationManager;
        }

        private HttpRequestOptions BaseRequestOptions => new HttpRequestOptions
        {
            UserAgent = $"Emby/{_appHost.ApplicationVersion}"
        };

        private string NormalizeLanguage(string language)
        {
            if (language != null)
            {
                var culture = _localizationManager.FindLanguageInfo(language.AsSpan());
                if (culture != null)
                {
                    return culture.ThreeLetterISOLanguageName;
                }
            }

            return language;
        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            var pid = id.Split(',')[0];
            var title = id.Split(',')[1];
            var lang = id.Split(',')[2];
            var opts = BaseRequestOptions;
            opts.Url = $"https://www.podnapisi.net/en/subtitles/{title}/{pid}/download";

            using (var response = await _httpClient.GetResponse(opts).ConfigureAwait(false))
            {
                var ms = new MemoryStream();
                var archive = new ZipArchive(response.Content);

                await archive.Entries.FirstOrDefault().Open().CopyToAsync(ms).ConfigureAwait(false);
                ms.Position = 0;

                var fileExt = archive.Entries.FirstOrDefault().FullName.Split('.').LastOrDefault();

                if (string.IsNullOrWhiteSpace(fileExt))
                {
                    fileExt = "srt";
                }

                return new SubtitleResponse
                {
                    Format = fileExt,
                    Language = NormalizeLanguage(lang),
                    Stream = ms
                };

            }
        }

        public string Name => "Podnapisi";

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request,
            CancellationToken cancellationToken)
        {
            if (request.IsForced.HasValue)
            {
                // TODO: Is this filter supported?
                return new List<RemoteSubtitleInfo>();
            }
            if (request.IsPerfectMatch)
            {
                // TODO: Is this filter supported?
                return new List<RemoteSubtitleInfo>();
            }

            var url = new StringBuilder("https://www.podnapisi.net/subtitles/search/old?sXML=1");
            url.Append($"&sL={request.TwoLetterISOLanguageName}");
            if (request.SeriesName == null)
            {
                url.Append($"&sK={request.Name}");
            }
            else
            {
                url.Append($"&sK={request.SeriesName}");
            }
            if (request.ParentIndexNumber.HasValue)
            {
                url.Append($"&sTS={request.ParentIndexNumber}");
            }
            if (request.IndexNumber.HasValue)
            {
                url.Append($"&sTE={request.IndexNumber}");
            }
            if (request.ProductionYear.HasValue)
            {
                url.Append($"&sY={request.ProductionYear}");
            }

            var opts = BaseRequestOptions;
            opts.Url = url.ToString();
            _logger.Debug("Requesting {0}", opts.Url);

            try
            {
                using (var response = await _httpClient.GetResponse(opts).ConfigureAwait(false))
                {
                    using (var reader = new StreamReader(response.Content))
                    {
                        var settings = Create(false);
                        settings.CheckCharacters = false;
                        settings.IgnoreComments = true;
                        settings.DtdProcessing = DtdProcessing.Parse;
                        settings.MaxCharactersFromEntities = 1024;
                        settings.Async = true;

                        using (var result = XmlReader.Create(reader, settings))
                        {
                            return (await ParseSearch(result).ConfigureAwait(false)).OrderByDescending(i => i.DownloadCount);
                        }
                    }
                }
            }
            catch (HttpException ex)
            {
                if (ex.StatusCode.HasValue && ex.StatusCode.Value == HttpStatusCode.NotFound)
                {
                    return new List<RemoteSubtitleInfo>();
                }

                throw;
            }
        }

        private XmlReaderSettings Create(bool enableValidation)
        {
            var settings = new XmlReaderSettings();

            if (!enableValidation)
            {
                settings.ValidationType = ValidationType.None;
            }

            return settings;
        }

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new List<VideoContentType> { VideoContentType.Episode, VideoContentType.Movie };

        public int Order => 2;

        private async Task<List<RemoteSubtitleInfo>> ParseSearch(XmlReader reader)
        {
            var list = new List<RemoteSubtitleInfo>();
            await reader.MoveToContentAsync().ConfigureAwait(false);
            await reader.ReadAsync().ConfigureAwait(false);

            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "subtitle":
                            {
                                if (reader.IsEmptyElement)
                                {
                                    await reader.ReadAsync().ConfigureAwait(false);
                                    continue;
                                }
                                using (var subReader = reader.ReadSubtree())
                                {
                                    list.Add(await ParseSubtitleList(subReader).ConfigureAwait(false));
                                }
                                break;
                            }
                        default:
                            {
                                await reader.SkipAsync().ConfigureAwait(false);
                                break;
                            }
                    }
                }
                else
                {
                    await reader.ReadAsync().ConfigureAwait(false);
                }
            }
            return list;
        }

        private async Task<RemoteSubtitleInfo> ParseSubtitleList(XmlReader reader)
        {
            var SubtitleInfo = new RemoteSubtitleInfo
            {
                ProviderName = Name,
                Format = "srt"
            };
            await reader.MoveToContentAsync().ConfigureAwait(false);
            await reader.ReadAsync().ConfigureAwait(false);
            var id = new StringBuilder();

            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "pid":
                            {
                                id.Append($"{(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false))},");
                                break;
                            }
                        case "release":
                            {
                                SubtitleInfo.Name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                break;
                            }
                        case "url":
                            {
                                id.Append($"{(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false)).Split('/')[5]},");
                                break;
                            }
                        case "language":
                            {
                                var lang = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                                SubtitleInfo.ThreeLetterISOLanguageName = NormalizeLanguage(lang);
                                id.Append($"{lang},");
                                break;
                            }
                        case "rating":
                            {
                                SubtitleInfo.CommunityRating = await ReadFloat(reader).ConfigureAwait(false);
                                break;
                            }
                        case "downloads":
                            {
                                SubtitleInfo.DownloadCount = await ReadInt(reader).ConfigureAwait(false);
                                break;
                            }
                        default:
                            {
                                await reader.SkipAsync().ConfigureAwait(false);
                                break;
                            }
                    }
                }
                else
                {
                    await reader.ReadAsync().ConfigureAwait(false);
                }
            }
            SubtitleInfo.Id = id.ToString();
            return SubtitleInfo;
        }

        private async Task<float?> ReadFloat(XmlReader reader)
        {
            var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

            if (float.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out float result))
            {
                return result;
            }

            return null;
        }

        private async Task<int?> ReadInt(XmlReader reader)
        {
            var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

            if (int.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out int result))
            {
                return result;
            }

            return null;
        }
    }

}
