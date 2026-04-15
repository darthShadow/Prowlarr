using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Api.V1.Indexers;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerVersions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Security;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    // Drives the real NewznabController caching flow. Real: controller, cache service over a
    // real CacheManager, identity resolver, indexer factory, indexer status service, indexer
    // limit service, download mapping service, and protection service. Mocked boundaries: the
    // release search service (the seam above indexer HTTP calls), the repositories, the history
    // service, the config services, and the indexer instance the container returns.
    [TestFixture]
    public class NewznabControllerCacheFixture : CoreTest
    {
        private const string CachedTitle = "Cached.Release.2026.1080p-GRP";
        private const string OriginalDownloadUrl = "https://indexer.example.test/getnzb/cached-guid";

        private const string FreshTitle = "Fresh.Release.2026.1080p-GRP";
        private const string FreshDownloadUrl = "https://indexer.example.test/getnzb/fresh-guid";
        private const string MalformedDownloadUrl = "not-an-absolute-url";

        private IndexerDefinition _definition;
        private Mock<IIndexerRepository> _repository;
        private Mock<IHistoryService> _history;
        private IndexerStatusService _statusService;
        private List<IndexerStatus> _statusStore;
        private NewznabResultsCacheService _cacheService;
        private NewznabCacheIdentityResolver _identityResolver;
        private IndexerFactory _indexerFactory;
        private IIndexerLimitService _limitService;
        private IDownloadMappingService _mappingService;
        private Mock<IReleaseSearchService> _search;
        private int _searchCalls;
        private Func<int, Task<NewznabResults>> _searchBehavior;

        [SetUp]
        public void SetUp()
        {
            _searchCalls = 0;
            _statusStore = new List<IndexerStatus>();

            var indexer = new Mock<IIndexer>();
            indexer.SetupProperty(i => i.Definition);
            indexer.SetupGet(i => i.Protocol).Returns(DownloadProtocol.Usenet);
            indexer.Setup(i => i.GetCapabilities()).Returns(new IndexerCapabilities());

            _definition = new IndexerDefinition
            {
                Id = 1,
                Name = "Fixture Indexer",
                Enable = true,
                Implementation = indexer.Object.GetType().Name,
                Settings = new NewznabSettings()
            };

            _repository = new Mock<IIndexerRepository>();
            _repository.Setup(r => r.Get(1)).Returns(_definition);

            var container = new Mock<IServiceProvider>();
            container.Setup(c => c.GetService(It.IsAny<Type>())).Returns(indexer.Object);

            var statusRepository = new Mock<IIndexerStatusRepository>();
            statusRepository.Setup(r => r.FindByProviderId(It.IsAny<int>()))
                .Returns((int id) => _statusStore.FirstOrDefault(s => s.ProviderId == id));
            statusRepository.Setup(r => r.All()).Returns(() => _statusStore.ToList());
            statusRepository.Setup(r => r.Upsert(It.IsAny<IndexerStatus>())).Returns((IndexerStatus status) =>
            {
                _statusStore.RemoveAll(s => s.ProviderId == status.ProviderId);
                _statusStore.Add(status);
                return status;
            });

            var runtimeInfo = new Mock<IRuntimeInfo>();
            runtimeInfo.SetupGet(r => r.StartTime).Returns(DateTime.UtcNow.AddHours(-1));

            _statusService = new IndexerStatusService(statusRepository.Object, Mock.Of<IEventAggregator>(), runtimeInfo.Object, TestLogger);

            _indexerFactory = new IndexerFactory(
                Mock.Of<IIndexerDefinitionUpdateService>(),
                Mock.Of<INewznabCapabilitiesProvider>(),
                _statusService,
                _repository.Object,
                new[] { indexer.Object },
                container.Object,
                Mock.Of<IEventAggregator>(),
                TestLogger);

            var config = new Mock<IConfigService>();
            config.SetupGet(c => c.CacheDefaultTtlMinutes).Returns(10);
            config.SetupGet(c => c.CacheMinimumTtlMinutes).Returns(5);
            config.SetupGet(c => c.DownloadProtectionKey).Returns("0123456789abcdef0123456789abcdef");

            var configFile = new Mock<IConfigFileProvider>();
            configFile.SetupGet(c => c.ApiKey).Returns("fixture-api-key-0000");

            _cacheService = new NewznabResultsCacheService(new CacheManager(), config.Object, TestLogger);
            _identityResolver = new NewznabCacheIdentityResolver(TestLogger);
            _history = new Mock<IHistoryService>();
            _limitService = new IndexerLimitService(_history.Object, TestLogger);
            _mappingService = new DownloadMappingService(new ProtectionService(config.Object), configFile.Object);

            _search = new Mock<IReleaseSearchService>();
            _search.Setup(s => s.Search(It.IsAny<NewznabRequest>(), It.IsAny<List<int>>(), It.IsAny<bool>()))
                .Returns(() => _searchBehavior(Interlocked.Increment(ref _searchCalls)));
        }

        private NewznabController CreateController(HttpContext httpContext)
        {
            return new NewznabController(
                _indexerFactory,
                _search.Object,
                _limitService,
                _statusService,
                _mappingService,
                Mock.Of<IDownloadService>(),
                _cacheService,
                _identityResolver,
                TestLogger)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };
        }

        private static HttpContext CreateHttpContext()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("prowlarr.local", 9696);
            httpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

            return httpContext;
        }

        // RSS-like request: t=search with a category and no q. It uses adaptive caching.
        private static NewznabRequest RssRequest() => new NewznabRequest { t = "search", cat = "2000" };

        private CanonicalCacheIdentity RssIdentity() =>
            _identityResolver.Resolve(1, RssRequest(), new IndexerCapabilities());

        private async Task<(ContentResult Result, HttpContext Context)> GetRss()
        {
            var httpContext = CreateHttpContext();
            var result = await CreateController(httpContext).GetNewznabResponse(1, RssRequest());

            return ((ContentResult)result, httpContext);
        }

        private static NewznabResults OneRelease(string title = CachedTitle, string downloadUrl = OriginalDownloadUrl) => new NewznabResults
        {
            Releases = new List<ReleaseInfo>
            {
                new ReleaseInfo
                {
                    Title = title,
                    Guid = "cached-guid",
                    DownloadUrl = downloadUrl,
                    PublishDate = DateTime.UtcNow
                }
            }
        };

        private ICached<CachedEntry> GetCacheStore()
        {
            var field = typeof(NewznabResultsCacheService).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);

            return (ICached<CachedEntry>)field.GetValue(_cacheService);
        }

        // Rewrites the stored entry so its age is the given share of its TTL.
        private void AgeCachedEntry(double ageRatio)
        {
            var store = GetCacheStore();
            var key = RssIdentity().Key;
            var entry = store.Find(key);
            entry.Should().NotBeNull("the entry to age must be in the cache");

            var ttlSecs = entry.Metadata.CachedTtlSecs;
            var aged = new CachedEntry(entry.Results, new CachedEntryMetadata(DateTime.UtcNow.AddSeconds(-ttlSecs * ageRatio), ttlSecs));
            store.Set(key, aged, TimeSpan.FromSeconds(ttlSecs));
        }

        private long GetIndexerMisses(int indexerId)
        {
            var statsField = typeof(NewznabResultsCacheService).GetField("_indexerStats", BindingFlags.Instance | BindingFlags.NonPublic);
            var stats = statsField.GetValue(_cacheService);
            var args = new object[] { indexerId, null };

            if (!(bool)stats.GetType().GetMethod("TryGetValue").Invoke(stats, args))
            {
                return 0;
            }

            var missesField = args[1].GetType().GetField("_misses", BindingFlags.Instance | BindingFlags.NonPublic);

            return (long)missesField.GetValue(args[1]);
        }

        private static bool SpinUntil(Func<bool> condition, TimeSpan timeout) =>
            SpinWait.SpinUntil(condition, timeout);

        // Sends RSS hits until the search mock has been called a second time, which is the
        // probabilistic bypass. Each hit on an entry aged to its full TTL bypasses with
        // probability 0.25, so 400 hits miss every time with probability 0.75^400 (below 1e-49).
        private async Task<(ContentResult Result, HttpContext Context)> SendHitsUntilBypass()
        {
            (ContentResult Result, HttpContext Context) response = default;

            for (var i = 0; i < 400 && _searchCalls == 1; i++)
            {
                response = await GetRss();
            }

            _searchCalls.Should().Be(2, "a probabilistic bypass must fire within 400 hits on an entry aged to its full TTL");

            return response;
        }

        [Test]
        public async Task Concurrent_identical_requests_should_reach_upstream_once()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _searchBehavior = async _ =>
            {
                await gate.Task;
                return OneRelease();
            };

            var requests = Enumerable.Range(0, 5).Select(_ => Task.Run(GetRss)).ToList();

            // Each request records one cache miss on the fast path before it queues on the dedup
            // lock, so five misses mean all five requests are in flight.
            SpinUntil(() => GetIndexerMisses(1) >= 5, TimeSpan.FromSeconds(5))
                .Should().BeTrue($"all five requests should reach the cache check; recorded misses: {GetIndexerMisses(1)}");

            gate.SetResult(true);
            var responses = await Task.WhenAll(requests);

            _searchCalls.Should().Be(1, "concurrent identical requests share one upstream search");
            responses.Select(r => r.Result.StatusCode).Should().OnlyContain(code => code == 200);
            responses.Select(r => r.Result.Content).Should().OnlyContain(body =>
                body.Contains("http://prowlarr.local:9696/1/download?apikey=") && !body.Contains("indexer.example.test"));
        }

        [Test]
        public async Task Cache_should_store_original_urls_and_proxy_them_per_request()
        {
            _searchBehavior = _ => Task.FromResult(OneRelease());

            var first = await GetRss();

            first.Result.StatusCode.Should().Be(200);
            first.Result.Content.Should().Contain("http://prowlarr.local:9696/1/download?apikey=");
            first.Result.Content.Should().NotContain("indexer.example.test");
            GetCacheStore().Find(RssIdentity().Key).Results.Releases.Single().DownloadUrl.Should().Be(OriginalDownloadUrl);

            var second = await GetRss();

            _searchCalls.Should().Be(1, "the second request is a cache hit");
            second.Result.StatusCode.Should().Be(200);
            second.Result.Content.Should().Contain("http://prowlarr.local:9696/1/download?apikey=");
            second.Result.Content.Should().NotContain("indexer.example.test");
            GetCacheStore().Find(RssIdentity().Key).Results.Releases.Single().DownloadUrl
                .Should().Be(OriginalDownloadUrl, "serving a hit must rewrite a copy, never the cached release");
        }

        [TestCase("releases")]
        [TestCase("no-releases")]
        public async Task Fetch_overlapping_settings_update_should_not_populate_cache(string searchResult)
        {
            var searchEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _searchBehavior = async call =>
            {
                if (call == 1)
                {
                    searchEntered.SetResult(true);
                    await release.Task;
                }

                return searchResult == "releases"
                    ? OneRelease()
                    : new NewznabResults { Releases = new List<ReleaseInfo>() };
            };

            var firstRequest = Task.Run(GetRss);
            SpinUntil(() => searchEntered.Task.IsCompleted, TimeSpan.FromSeconds(5))
                .Should().BeTrue("the first request should reach the blocked search");

            _cacheService.Handle(new ProviderUpdatedEvent<IIndexer>(_definition));
            release.SetResult(true);

            var first = await firstRequest;
            first.Result.StatusCode.Should().Be(200);

            var second = await GetRss();

            second.Result.StatusCode.Should().Be(200);
            _searchCalls.Should().Be(2, "results fetched before the settings update must not be served to the next request");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Bypass_should_serve_cached_feed_when_upstream_failure_blocks_indexer(bool searchReturnsReleases)
        {
            _searchBehavior = call =>
            {
                if (call == 1)
                {
                    return Task.FromResult(OneRelease());
                }

                // Mirrors HttpIndexerBase.FetchReleases after an upstream failure: record the
                // failure, which blocks the indexer. A blocked indexer can still return releases
                // (for example a partial page), and those must not replace the cached entry.
                _statusService.RecordFailure(1);

                return Task.FromResult(searchReturnsReleases
                    ? OneRelease(FreshTitle, FreshDownloadUrl)
                    : new NewznabResults { Releases = new List<ReleaseInfo>() });
            };

            (await GetRss()).Result.StatusCode.Should().Be(200);
            AgeCachedEntry(1.0);

            var bypass = await SendHitsUntilBypass();

            bypass.Result.StatusCode.Should().Be(200, "a bypass that blocks the indexer serves the cached feed");
            bypass.Result.Content.Should().Contain(CachedTitle);
            bypass.Result.Content.Should().NotContain(FreshTitle, "releases returned by a blocking search are not served");
            bypass.Context.Response.Headers.ContainsKey(HeaderNames.RetryAfter).Should().BeFalse("a served cached feed carries no Retry-After");
            _cacheService.FindForRecheck(RssIdentity()).Releases.Single().Title.Should().Be(CachedTitle, "the cached entry stays unchanged");
            _cacheService.GetBypassSuppressedUntilUtc(RssIdentity()).Should().NotBeNull("a failed bypass suppresses further bypasses for the key");
        }

        [Test]
        public async Task Bypass_should_keep_cached_entry_when_upstream_returns_no_releases()
        {
            _searchBehavior = call => Task.FromResult(call == 1
                ? OneRelease()
                : new NewznabResults { Releases = new List<ReleaseInfo>() });

            (await GetRss()).Result.StatusCode.Should().Be(200);
            AgeCachedEntry(1.0);

            var bypass = await SendHitsUntilBypass();

            bypass.Result.StatusCode.Should().Be(200);
            bypass.Result.Content.Should().Contain(CachedTitle, "an empty bypass result keeps the cached feed");
            _cacheService.FindForRecheck(RssIdentity()).Releases.Single().Title.Should().Be(CachedTitle, "an empty bypass result must not replace the cached entry");
            GetCacheStore().Find(RssIdentity().Key).Results.Releases.Single().DownloadUrl
                .Should().Be(OriginalDownloadUrl, "serving the cached feed after a failed bypass must rewrite a copy, never the cached release");
        }

        [Test]
        public async Task Bypass_should_publish_and_serve_fresh_results_when_upstream_succeeds()
        {
            _searchBehavior = call => Task.FromResult(call == 1
                ? OneRelease()
                : OneRelease(FreshTitle, FreshDownloadUrl));

            (await GetRss()).Result.StatusCode.Should().Be(200);
            AgeCachedEntry(1.0);

            var bypass = await SendHitsUntilBypass();

            bypass.Result.StatusCode.Should().Be(200);
            bypass.Result.Content.Should().Contain(FreshTitle, "a successful bypass serves the fresh results");
            bypass.Result.Content.Should().NotContain("indexer.example.test", "the fresh results are served through proxy links");
            _cacheService.GetBypassSuppressedUntilUtc(RssIdentity()).Should().BeNull("a successful bypass records no failure");

            var stored = GetCacheStore().Find(RssIdentity().Key).Results.Releases.Single();

            stored.Title.Should().Be(FreshTitle, "a successful bypass replaces the cached entry");
            stored.DownloadUrl.Should().Be(FreshDownloadUrl, "the cache keeps the original URL, never the proxy link");
        }

        [Test]
        public async Task Bypass_should_serve_cached_feed_when_query_limit_is_reached_after_the_cache_check()
        {
            _definition.Settings = new NewznabSettings { BaseSettings = new IndexerBaseSettings { QueryLimit = 5 } };

            _searchBehavior = call => Task.FromResult(call == 1
                ? OneRelease()
                : OneRelease(FreshTitle, FreshDownloadUrl));

            var readsInRequest = 0;
            _history.Setup(h => h.CountSince(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<List<HistoryEventType>>()))
                .Returns(() => Interlocked.Increment(ref readsInRequest) >= 2 ? 5 : 0);

            (await GetRss()).Result.StatusCode.Should().Be(200);
            AgeCachedEntry(1.0);

            // Each request reads the query limit once for the bypass decision. The count stays
            // below the limit on that read and reaches the limit on the second read, which the
            // fired bypass makes.
            (ContentResult Result, HttpContext Context) response = default;

            for (var i = 0; i < 400 && _cacheService.GetBypassSuppressedUntilUtc(RssIdentity()) == null; i++)
            {
                Interlocked.Exchange(ref readsInRequest, 0);
                response = await GetRss();
            }

            _cacheService.GetBypassSuppressedUntilUtc(RssIdentity()).Should().NotBeNull("a bypass that finds the query limit reached counts as failed");
            response.Result.StatusCode.Should().Be(200);
            response.Result.Content.Should().Contain(CachedTitle, "the cached feed is served when the limit is reached");
            response.Result.Content.Should().NotContain(FreshTitle);
            _searchCalls.Should().Be(1, "no upstream search runs once the query limit is reached");
            _cacheService.FindForRecheck(RssIdentity()).Releases.Single().Title.Should().Be(CachedTitle);
        }

        [Test]
        public async Task Search_should_not_cache_results_when_the_response_cannot_be_built()
        {
            _searchBehavior = call => Task.FromResult(call == 1
                ? OneRelease(FreshTitle, MalformedDownloadUrl)
                : OneRelease());

            Func<Task> malformed = () => GetRss();

            await malformed.Should().ThrowAsync<UriFormatException>("the malformed download URL cannot be proxied");
            GetCacheStore().Find(RssIdentity().Key).Should().BeNull("results that cannot be served must not be cached");

            var next = await GetRss();

            next.Result.StatusCode.Should().Be(200, "later requests for the key keep working");
            next.Result.Content.Should().Contain(CachedTitle);
            _searchCalls.Should().Be(2, "the malformed results were never cached, so the next request searches again");
        }

        [Test]
        public async Task Bypass_should_keep_cached_entry_when_the_fresh_response_cannot_be_built()
        {
            _searchBehavior = call => Task.FromResult(call == 1
                ? OneRelease()
                : OneRelease(FreshTitle, MalformedDownloadUrl));

            (await GetRss()).Result.StatusCode.Should().Be(200);
            AgeCachedEntry(1.0);

            var metadataBefore = GetCacheStore().Find(RssIdentity().Key).Metadata;

            var bypass = await SendHitsUntilBypass();

            bypass.Result.StatusCode.Should().Be(200, "a failed bypass serves the cached feed");
            bypass.Result.Content.Should().Contain(CachedTitle);
            _cacheService.GetBypassSuppressedUntilUtc(RssIdentity()).Should().NotBeNull("a failed bypass suppresses further bypasses for the key");

            var entry = GetCacheStore().Find(RssIdentity().Key);

            entry.Results.Releases.Single().Title.Should().Be(CachedTitle, "results that cannot be served must not replace the cached entry");
            entry.Results.Releases.Single().DownloadUrl.Should().Be(OriginalDownloadUrl);
            entry.Metadata.Should().BeEquivalentTo(metadataBefore, "the cached entry keeps its metadata");

            var next = await GetRss();

            next.Result.StatusCode.Should().Be(200, "later requests for the key keep working");
            next.Result.Content.Should().Contain(CachedTitle);
        }

        [Test]
        public async Task Fetch_should_not_populate_cache_when_settings_change_while_the_definition_is_read()
        {
            var firstRead = true;

            // A settings update that lands while the definition is being read must invalidate the
            // fetch that follows, so the generation has to be captured before the read.
            _repository.Setup(r => r.Get(1)).Returns(() =>
            {
                if (firstRead)
                {
                    firstRead = false;
                    _cacheService.Handle(new ProviderUpdatedEvent<IIndexer>(_definition));
                }

                return _definition;
            });

            _searchBehavior = _ => Task.FromResult(OneRelease());

            (await GetRss()).Result.StatusCode.Should().Be(200);
            (await GetRss()).Result.StatusCode.Should().Be(200);

            _searchCalls.Should().Be(2, "results fetched after a settings update that overlapped the definition read must not be served to the next request");
        }

        [Test]
        public async Task Bypass_should_serve_cached_feed_and_redact_log_when_upstream_throws()
        {
            const string sentinel = "APIKEY-SENTINEL-BYPASS-9D2E";

            _searchBehavior = call =>
            {
                if (call == 1)
                {
                    return Task.FromResult(OneRelease());
                }

                var httpRequest = new NzbDrone.Common.Http.HttpRequest($"https://example.test/api?t=search&cat=5000&apikey={sentinel}");
                var httpResponse = new NzbDrone.Common.Http.HttpResponse(httpRequest, new HttpHeader(), new CookieCollection(), "upstream failure", statusCode: HttpStatusCode.BadGateway, version: new Version(1, 1));

                return Task.FromException<NewznabResults>(new HttpException(httpRequest, httpResponse));
            };

            (await GetRss()).Result.StatusCode.Should().Be(200);
            _searchCalls.Should().Be(1);
            AgeCachedEntry(1.0);

            var target = new MemoryTarget("ControllerBypassLogCapture") { Layout = "${message}" };
            var rule = new LoggingRule("*", LogLevel.Debug, target);
            LogManager.Configuration.AddTarget(target);
            LogManager.Configuration.LoggingRules.Add(rule);
            LogManager.ReconfigExistingLoggers();

            try
            {
                var bypass = await SendHitsUntilBypass();

                bypass.Result.StatusCode.Should().Be(200, "a failed bypass serves the cached feed");
                bypass.Result.Content.Should().Contain(CachedTitle);
                bypass.Context.Response.Headers.ContainsKey(HeaderNames.RetryAfter).Should().BeFalse("a served cached feed carries no Retry-After");
                _cacheService.GetBypassSuppressedUntilUtc(RssIdentity()).Should().NotBeNull("a failed bypass suppresses further bypasses for the key");

                var bypassLogs = target.Logs.Where(l => l.Contains("Probabilistic bypass failed")).ToList();

                bypassLogs.Should().ContainSingle();
                bypassLogs.Single().Should().NotContain(sentinel);
                bypassLogs.Single().Should().NotContain("apikey=");
                bypassLogs.Single().Should().NotContain("https://example.test");
            }
            finally
            {
                LogManager.Configuration.LoggingRules.Remove(rule);
                LogManager.Configuration.RemoveTarget("ControllerBypassLogCapture");
                LogManager.ReconfigExistingLoggers();
            }
        }
    }
}
