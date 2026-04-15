using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider.Status;
using Prowlarr.Http.Extensions;
using Prowlarr.Http.REST;
using BadRequestException = NzbDrone.Core.Exceptions.BadRequestException;

namespace NzbDrone.Api.V1.Indexers
{
    [Route("")]
    [EnableCors("ApiCorsPolicy")]
    [ApiController]
    public class NewznabController : Controller
    {
        private IIndexerFactory _indexerFactory { get; set; }
        private IReleaseSearchService _releaseSearchService { get; set; }
        private IIndexerLimitService _indexerLimitService { get; set; }
        private IIndexerStatusService _indexerStatusService;
        private IDownloadMappingService _downloadMappingService { get; set; }
        private IDownloadService _downloadService { get; set; }
        private INewznabResultsCacheService _cacheService { get; set; }
        private INewznabCacheIdentityResolver _cacheIdentityResolver { get; set; }
        private readonly Logger _logger;

        public NewznabController(IndexerFactory indexerFactory,
            IReleaseSearchService releaseSearchService,
            IIndexerLimitService indexerLimitService,
            IIndexerStatusService indexerStatusService,
            IDownloadMappingService downloadMappingService,
            IDownloadService downloadService,
            INewznabResultsCacheService cacheService,
            INewznabCacheIdentityResolver cacheIdentityResolver,
            Logger logger)
        {
            _indexerFactory = indexerFactory;
            _releaseSearchService = releaseSearchService;
            _indexerLimitService = indexerLimitService;
            _indexerStatusService = indexerStatusService;
            _downloadMappingService = downloadMappingService;
            _downloadService = downloadService;
            _cacheService = cacheService;
            _cacheIdentityResolver = cacheIdentityResolver;
            _logger = logger;
        }

        [HttpGet("/api/v1/indexer/{id:int}/newznab")]
        [HttpGet("{id:int}/api")]
        public async Task<IActionResult> GetNewznabResponse(int id, [FromQuery] NewznabRequest request)
        {
            var requestType = request.t;
            request.source = Request.GetSource();
            request.server = Request.GetServerUrl();
            request.host = Request.GetHostName();

            if (requestType.IsNullOrWhiteSpace())
            {
                return CreateResponse(CreateErrorXML(200, "Missing parameter (t)"), statusCode: StatusCodes.Status400BadRequest);
            }

            request.imdbid = request.imdbid?.TrimStart('t') ?? null;

            if (request.imdbid.IsNotNullOrWhiteSpace())
            {
                if (!int.TryParse(request.imdbid, out var imdb) || imdb == 0)
                {
                    return CreateResponse(CreateErrorXML(201, "Incorrect parameter (imdbid)"), statusCode: StatusCodes.Status400BadRequest);
                }
            }

            if (id == 0)
            {
                switch (requestType)
                {
                    case "caps":
                        var caps = new IndexerCapabilities
                        {
                            TvSearchParams = new List<TvSearchParam>
                            {
                                TvSearchParam.Q, TvSearchParam.Season, TvSearchParam.Ep
                            },
                            MovieSearchParams = new List<MovieSearchParam>
                            {
                                MovieSearchParam.Q
                            },
                            MusicSearchParams = new List<MusicSearchParam>
                            {
                                MusicSearchParam.Q
                            },
                            BookSearchParams = new List<BookSearchParam>
                            {
                                BookSearchParam.Q
                            }
                        };

                        foreach (var cat in NewznabStandardCategory.AllCats)
                        {
                            caps.Categories.AddCategoryMapping(1, cat);
                        }

                        return CreateResponse(caps.ToXml());
                    case "search":
                    case "tvsearch":
                    case "music":
                    case "book":
                    case "movie":
                        var results = new NewznabResults
                        {
                            Releases = new List<ReleaseInfo>
                            {
                                new()
                                {
                                    Title = "Test Release",
                                    Guid = "https://prowlarr.com",
                                    DownloadUrl = "https://prowlarr.com",
                                    PublishDate = DateTime.Now
                                }
                            }
                        };

                        return CreateResponse(results.ToXml(DownloadProtocol.Usenet));
                }
            }

            var indexerDef = _indexerFactory.Get(id);

            if (indexerDef == null)
            {
                throw new NotFoundException("Indexer Not Found");
            }

            if (!indexerDef.Enable)
            {
                return CreateResponse(CreateErrorXML(410, "Indexer is disabled"), statusCode: StatusCodes.Status410Gone);
            }

            var indexer = _indexerFactory.GetInstance(indexerDef);

            var blockedIndexerStatusPre = GetBlockedIndexerStatus(indexer);

            if (blockedIndexerStatusPre?.DisabledTill != null)
            {
                AddRetryAfterHeader(CalculateRetryAfterDisabledTill(blockedIndexerStatusPre.DisabledTill.Value));

                return CreateResponse(CreateErrorXML(429, $"Indexer is disabled till {blockedIndexerStatusPre.DisabledTill.Value.ToLocalTime()} due to recent failures."), statusCode: StatusCodes.Status429TooManyRequests);
            }

            switch (requestType)
            {
                case "caps":
                    // Note: caps is not subject to query limits — it returns locally-configured
                    // capabilities without making upstream HTTP calls to the indexer.
                    var caps = indexer.GetCapabilities();
                    return CreateResponse(caps.ToXml());
                case "search":
                case "tvsearch":
                case "music":
                case "book":
                case "movie":
                    // Caching flow: fast-path cache check → dedup lock (if caching enabled) →
                    // double-check cache → query limit → upstream search → cache result → rewrite URLs
                    // cachetime=0 bypasses cache and dedup entirely (SearchAsync called directly)

                    // Resolve cache TTL from three-tier config (request → indexer → global)
                    var indexerSettings = (IIndexerSettings)indexerDef.Settings;
                    var cacheIdentity = _cacheIdentityResolver.Resolve(id, request, indexer.GetCapabilities());
                    var cacheTtl = _cacheService.ResolveTtl(id, request.cachetime, indexerSettings.BaseSettings.CacheTtlMinutes);

                    var usesAdaptiveRssCaching = NewznabCacheQueryPolicy.UsesAdaptiveRssCaching(request);

                    // Extend TTL for RSS-like queries based on result stability
                    if (cacheTtl.HasValue && usesAdaptiveRssCaching)
                    {
                        cacheTtl = _cacheService.GetAdaptiveTtl(cacheIdentity, cacheTtl.Value);
                    }

                    // Fast path: serve from cache (no lock needed)
                    if (cacheTtl.HasValue)
                    {
                        var cachedEntry = _cacheService.FindWithMetadata(cacheIdentity, request);
                        if (cachedEntry != null)
                        {
                            var ageRatio = CalculateAgeRatio(cachedEntry.Metadata);
                            var bypassSuppressedUntilUtc = _cacheService.GetBypassSuppressedUntilUtc(cacheIdentity);

                            // Phase 1 keeps the existing unlocked bypass path, but scales the
                            // bypass probability by cache age and suppresses retries for 30s
                            // after a bypass failure.
                            if (usesAdaptiveRssCaching
                                && NewznabCacheQueryPolicy.ShouldBypassCacheHit(
                                        request,
                                        _indexerLimitService.AtQueryLimit(indexerDef),
                                        ageRatio,
                                        bypassSuppressedUntilUtc))
                            {
                                try
                                {
                                    return await SearchAsync(forceUpstream: true);
                                }
                                catch (Exception ex)
                                {
                                    var suppressionRecorded = _cacheService.RecordBypassFailure(cacheIdentity, DateTime.UtcNow);
                                    LogBypassFailure(indexerDef.Name, cacheIdentity, cachedEntry.Metadata, ageRatio, suppressionRecorded, ex);
                                }
                            }

                            return CreateSearchResponse(cachedEntry.Results, request, indexer, indexerDef, cloneReleases: true);
                        }
                    }

                    // Search path — shared by dedup, non-dedup, and bypass paths
                    async Task<IActionResult> SearchAsync(bool forceUpstream = false)
                    {
                        // Re-check cache after acquiring dedup lock (another request may have
                        // populated it). Skipped for probabilistic bypass requests which
                        // explicitly want a fresh upstream call.
                        if (!forceUpstream && cacheTtl.HasValue)
                        {
                            var deduped = _cacheService.FindForRecheck(cacheIdentity);
                            if (deduped != null)
                            {
                                return CreateSearchResponse(deduped, request, indexer, indexerDef, cloneReleases: true);
                            }
                        }

                        // TODO Optimize this so it's not called here and in ReleaseSearchService (for manual search)
                        // Query limit check is intentionally inside the dedup lock: cache hits (above)
                        // bypass this check, so limits only count actual upstream requests. Moving it
                        // outside would either count cache hits against limits or require duplication.
                        if (_indexerLimitService.AtQueryLimit(indexerDef))
                        {
                            var retryAfterQueryLimit = _indexerLimitService.CalculateRetryAfterQueryLimit(indexerDef);
                            AddRetryAfterHeader(retryAfterQueryLimit);

                            var queryLimit = indexerSettings.BaseSettings.QueryLimit;
                            var intervalLimitHours = _indexerLimitService.CalculateIntervalLimitHours(indexerDef);

                            return CreateResponse(CreateErrorXML(429, $"User configurable Indexer Query Limit of {queryLimit} in last {intervalLimitHours} hour(s) reached."), statusCode: StatusCodes.Status429TooManyRequests);
                        }

                        var results = await _releaseSearchService.Search(request, new List<int> { indexerDef.Id }, false);

                        var blockedIndexerStatusPost = GetBlockedIndexerStatus(indexer);

                        if (blockedIndexerStatusPost?.DisabledTill != null)
                        {
                            AddRetryAfterHeader(CalculateRetryAfterDisabledTill(blockedIndexerStatusPost.DisabledTill.Value));

                            return CreateResponse(CreateErrorXML(429, $"Indexer is disabled till {blockedIndexerStatusPost.DisabledTill.Value.ToLocalTime()} due to recent failures."), statusCode: StatusCodes.Status429TooManyRequests);
                        }

                        // Cache results before URL rewriting (store original URLs).
                        // WARNING: MemberwiseClone is sufficient because URL rewriting only reassigns
                        // string properties (DownloadUrl/MagnetUrl). If future code mutates collection
                        // properties (Genres, Categories, etc.) on cached/served copies, use deep clone.
                        if (cacheTtl.HasValue)
                        {
                            if (results.Releases.Count > 0)
                            {
                                var resultsToCache = new NewznabResults
                                {
                                    Releases = results.Releases.Select(r => (ReleaseInfo)r.Clone()).ToList()
                                };
                                _cacheService.Set(cacheIdentity, request, resultsToCache, cacheTtl.Value, indexerDef.Name);
                            }
                            else
                            {
                                // Negative cache: store empty results with a short TTL to prevent
                                // repeated upstream queries for searches that genuinely return nothing,
                                // while still allowing newly available content to appear within 60s.
                                var negativeTtl = TimeSpan.FromSeconds(Math.Min(60, cacheTtl.Value.TotalSeconds));
                                _cacheService.Set(cacheIdentity, request, new NewznabResults { Releases = new List<ReleaseInfo>() }, negativeTtl, indexerDef.Name);
                            }
                        }

                        return CreateSearchResponse(results, request, indexer, indexerDef, cloneReleases: false);
                    }

                    // Serialize concurrent identical requests when caching is enabled
                    if (cacheTtl.HasValue)
                    {
                        // Lambda required — SearchAsync has a bool param; Func<Task<T>> takes zero params
                        return await _cacheService.DeduplicateAsync<IActionResult>(cacheIdentity, () => SearchAsync());
                    }

                    return await SearchAsync();
                default:
                    return CreateResponse(CreateErrorXML(202, $"No such function ({requestType})"), statusCode: StatusCodes.Status400BadRequest);
            }
        }

        [HttpGet("/api/v1/indexer/{id:int}/download")]
        [HttpGet("{id:int}/download")]
        public async Task<object> GetDownload(int id, string link, string file)
        {
            var indexerDef = _indexerFactory.Get(id);

            if (indexerDef == null)
            {
                throw new NotFoundException("Indexer Not Found");
            }

            if (!indexerDef.Enable)
            {
                return CreateResponse(CreateErrorXML(410, "Indexer is disabled"), statusCode: StatusCodes.Status410Gone);
            }

            var indexer = _indexerFactory.GetInstance(indexerDef);

            var blockedIndexerStatus = GetBlockedIndexerStatus(indexer);

            if (blockedIndexerStatus?.DisabledTill != null)
            {
                var retryAfterDisabledTill = Convert.ToInt32(blockedIndexerStatus.DisabledTill.Value.ToLocalTime().Subtract(DateTime.Now).TotalSeconds);
                AddRetryAfterHeader(retryAfterDisabledTill);

                return CreateResponse(CreateErrorXML(429, $"Indexer is disabled till {blockedIndexerStatus.DisabledTill.Value.ToLocalTime()} due to recent failures."), statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (_indexerLimitService.AtDownloadLimit(indexerDef))
            {
                var retryAfterDownloadLimit = _indexerLimitService.CalculateRetryAfterDownloadLimit(indexerDef);
                AddRetryAfterHeader(retryAfterDownloadLimit);

                var grabLimit = ((IIndexerSettings)indexer.Definition.Settings).BaseSettings.GrabLimit;
                var intervalLimitHours = _indexerLimitService.CalculateIntervalLimitHours(indexerDef);

                return CreateResponse(CreateErrorXML(429, $"User configurable Indexer Grab Limit of {grabLimit} in last {intervalLimitHours} hour(s) reached."), statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (link.IsNullOrWhiteSpace() || file.IsNullOrWhiteSpace())
            {
                throw new BadRequestException("Invalid Prowlarr link");
            }

            file = WebUtility.UrlDecode(file);

            var source = Request.GetSource();
            var host = Request.GetHostName();

            var unprotectedLink = _downloadMappingService.ConvertToNormalLink(link);

            if (unprotectedLink.IsNullOrWhiteSpace())
            {
                throw new BadRequestException("Failed to normalize provided link");
            }

            // If Indexer is set to download via Redirect then just redirect to the link unless it's a Usenet indexer at which point it forces Redirect.
            if (indexer.Protocol == DownloadProtocol.Usenet || (indexer.SupportsRedirect && indexerDef.Redirect))
            {
                _downloadService.RecordRedirect(unprotectedLink, id, source, host, file);
                return RedirectPermanent(unprotectedLink);
            }

            byte[] downloadBytes;

            try
            {
                downloadBytes = await _downloadService.DownloadReport(unprotectedLink, id, source, host, file);
            }
            catch (ReleaseUnavailableException ex)
            {
                return CreateResponse(CreateErrorXML(410, ex.Message), statusCode: StatusCodes.Status410Gone);
            }
            catch (ReleaseDownloadException ex) when (ex.InnerException is TooManyRequestsException http429)
            {
                var http429RetryAfter = Convert.ToInt32(http429.RetryAfter.TotalSeconds);
                AddRetryAfterHeader(http429RetryAfter);

                return CreateResponse(CreateErrorXML(429, ex.Message), statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (Exception ex)
            {
                _logger.Error(ex);

                return CreateResponse(CreateErrorXML(500, ex.Message), statusCode: StatusCodes.Status500InternalServerError);
            }

            // handle magnet URLs
            if (downloadBytes.Length >= 7
                && downloadBytes[0] == 0x6d
                && downloadBytes[1] == 0x61
                && downloadBytes[2] == 0x67
                && downloadBytes[3] == 0x6e
                && downloadBytes[4] == 0x65
                && downloadBytes[5] == 0x74
                && downloadBytes[6] == 0x3a)
            {
                var magnetUrl = Encoding.UTF8.GetString(downloadBytes);
                return RedirectPermanent(magnetUrl);
            }

            var contentType = indexer.Protocol == DownloadProtocol.Torrent ? "application/x-bittorrent" : "application/x-nzb";
            var extension = indexer.Protocol == DownloadProtocol.Torrent ? "torrent" : "nzb";
            var filename = $"{file}.{extension}";

            return File(downloadBytes, contentType, filename);
        }

        public static string CreateErrorXML(int code, string description)
        {
            var xdoc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement("error",
                    new XAttribute("code", code.ToString()),
                    new XAttribute("description", description)));

            return xdoc.Declaration + Environment.NewLine + xdoc;
        }

        private ContentResult CreateResponse(string content, string contentType = "application/rss+xml", int statusCode = StatusCodes.Status200OK)
        {
            var mediaTypeHeaderValue = MediaTypeHeaderValue.Parse(contentType);

            return new ContentResult
            {
                StatusCode = statusCode,
                Content = content,
                ContentType = mediaTypeHeaderValue.ToString()
            };
        }

        private ProviderStatusBase GetBlockedIndexerStatus(IIndexer indexer)
        {
            var blockedIndexers = _indexerStatusService.GetBlockedProviders().ToDictionary(v => v.ProviderId, v => v);

            return blockedIndexers.GetValueOrDefault(indexer.Definition.Id);
        }

        private void AddRetryAfterHeader(int retryAfterSeconds)
        {
            if (!HttpContext.Response.Headers.ContainsKey(HeaderNames.RetryAfter) && retryAfterSeconds > 0)
            {
                HttpContext.Response.Headers.RetryAfter = $"{retryAfterSeconds}";
            }
        }

        private static int CalculateRetryAfterDisabledTill(DateTime disabledTill)
        {
            return Convert.ToInt32(disabledTill.ToLocalTime().Subtract(DateTime.Now).TotalSeconds);
        }

        private static double CalculateAgeRatio(CachedEntryMetadata metadata)
        {
            var ageSeconds = Math.Max(0, (DateTime.UtcNow - metadata.CachedAtUtc).TotalSeconds);
            return Math.Clamp(ageSeconds / metadata.CachedTtlSecs, 0.0, 1.0);
        }

        private void LogBypassFailure(string indexerName, CanonicalCacheIdentity identity, CachedEntryMetadata metadata, double ageRatio, bool suppressionRecorded, Exception ex)
        {
            _logger.Debug(
                "Probabilistic bypass failed for indexer {0}, key {1}, cachedAt {2:o}, ageRatio {3:F2}, suppressionRecorded={4}, exceptionType={5}; serving cached result",
                indexerName,
                identity.Key,
                metadata.CachedAtUtc,
                ageRatio,
                suppressionRecorded,
                ex.GetType().Name);
        }

        private IActionResult CreateSearchResponse(NewznabResults results, NewznabRequest request, IIndexer indexer, IndexerDefinition indexerDef, bool cloneReleases)
        {
            // Clone releases when serving from cache to avoid mutating cached objects
            var releases = cloneReleases
                ? results.Releases.Select(r => (ReleaseInfo)r.Clone()).ToList()
                : results.Releases;

            foreach (var result in releases)
            {
                result.DownloadUrl = result.DownloadUrl.IsNotNullOrWhiteSpace() ? _downloadMappingService.ConvertToProxyLink(new Uri(result.DownloadUrl), request.server, indexerDef.Id, result.Title).AbsoluteUri : null;

                if (result.DownloadProtocol == DownloadProtocol.Torrent)
                {
                    ((TorrentInfo)result).MagnetUrl = ((TorrentInfo)result).MagnetUrl.IsNotNullOrWhiteSpace() ? _downloadMappingService.ConvertToProxyLink(new Uri(((TorrentInfo)result).MagnetUrl), request.server, indexerDef.Id, result.Title).AbsoluteUri : null;
                }
            }

            var rewrittenResults = cloneReleases ? new NewznabResults { Releases = releases } : results;

            var preferMagnetUrl = indexer.Protocol == DownloadProtocol.Torrent && indexerDef.Settings is ITorrentIndexerSettings ts && (ts.TorrentBaseSettings?.PreferMagnetUrl ?? false);

            return CreateResponse(rewrittenResults.ToXml(indexer.Protocol, preferMagnetUrl));
        }
    }
}
