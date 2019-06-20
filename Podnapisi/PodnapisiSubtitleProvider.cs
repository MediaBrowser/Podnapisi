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
using MediaBrowser.Model.Xml;
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

        public PodnapisiSubtitleProvider(ILogger logger, IHttpClient httpClient, IFileSystem fileSystem, IApplicationHost appHost)
        {
            _logger = logger;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
            _appHost = appHost;
        }

        private HttpRequestOptions BaseRequestOptions => new HttpRequestOptions
        {
            UserAgent = $"Emby/{_appHost.ApplicationVersion}"
        };

        private string NormalizeLanguage(string language)
        {
            if (language != null)
            {
                var culture = CultureInfo.GetCultureInfo(language);
                if (culture != null)
                {
                    return culture.TwoLetterISOLanguageName;
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
            opts.Url = $"https://www.podnapisi.net/{lang}/subtitles/{title}/{pid}/download";

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
                    Language = lang,
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

            var url = new StringBuilder("https://www.podnapisi.net/subtitles/search/old?sXML=1");
            url.Append($"&sL={NormalizeLanguage(request.TwoLetterISOLanguageName)}");
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
                        var settings = new XmlReaderSettings
                        {
                            CheckCharacters = false,
                            IgnoreComments = true,
                            DtdProcessing = DtdProcessing.Parse,
                            MaxCharactersFromEntities = 1024
                        };

                        using (var result = XmlReader.Create(reader, settings))
                        {
                            return ParseSearch(result).OrderByDescending(i => i.DownloadCount);
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

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new List<VideoContentType> { VideoContentType.Episode, VideoContentType.Movie };

        public int Order => 2;

        private List<RemoteSubtitleInfo> ParseSearch(XmlReader reader)
        {
            var list = new List<RemoteSubtitleInfo>();
            reader.MoveToContent();
            reader.Read();

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
                                    reader.Read();
                                    continue;
                                }
                                using (var subReader = reader.ReadSubtree())
                                {
                                    list.Add(ParseSubtitleList(subReader));
                                }
                                break;
                            }
                        default:
                            {
                                reader.Skip();
                                break;
                            }
                    }
                }
                else
                {
                    reader.Read();
                }
            }
            return list;
        }

        private RemoteSubtitleInfo ParseSubtitleList(XmlReader reader)
        {
            var SubtitleInfo = new RemoteSubtitleInfo
            {
                ProviderName = Name,
                Format = "srt"
            };
            reader.MoveToContent();
            reader.Read();
            var id = new StringBuilder();

            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "pid":
                            {
                                id.Append($"{reader.ReadElementContentAsString()},");
                                break;
                            }
                        case "release":
                            {
                                SubtitleInfo.Name = reader.ReadElementContentAsString();
                                break;
                            }
                        case "url":
                            {
                                id.Append($"{reader.ReadElementContentAsString().Split('/')[5]},");
                                break;
                            }
                        case "language":
                            {
                                var lang = reader.ReadElementContentAsString();
                                SubtitleInfo.ThreeLetterISOLanguageName = lang;
                                id.Append($"{lang},");
                                break;
                            }
                        case "rating":
                            {
                                SubtitleInfo.CommunityRating = reader.ReadElementContentAsFloat();
                                break;
                            }
                        case "downloads":
                            {
                                SubtitleInfo.DownloadCount = reader.ReadElementContentAsInt();
                                break;
                            }
                        default:
                            {
                                reader.Skip();
                                break;
                            }
                    }
                }
                else
                {
                    reader.Read();
                }
            }
            SubtitleInfo.Id = id.ToString();
            return SubtitleInfo;
        }

    }

}
