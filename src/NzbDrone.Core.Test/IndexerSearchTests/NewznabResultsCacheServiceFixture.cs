using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Api.V1.Indexers;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class NewznabResultsCacheServiceFixture : CoreTest<NewznabResultsCacheService>
    {
        [SetUp]
        public void SetUp()
        {
            // Default TTL config: 10-minute default, 5-minute minimum
            Mocker.GetMock<IConfigService>()
                .Setup(s => s.CacheDefaultTtlMinutes)
                .Returns(10);
            Mocker.GetMock<IConfigService>()
                .Setup(s => s.CacheMinimumTtlMinutes)
                .Returns(5);
        }

        private static NewznabRequest MakeRequest(string t = "search", string q = null, string cat = null, string extended = null)
        {
            return new NewznabRequest { t = t, q = q, cat = cat, extended = extended };
        }

        private static NewznabResults MakeResults(int count)
        {
            return new NewznabResults
            {
                Releases = Enumerable.Range(1, count)
                    .Select(i => (ReleaseInfo)new ReleaseInfo { Title = $"Release {i}", Guid = $"guid-{i}" })
                    .ToList()
            };
        }

        // Builds stability for a cache key via repeated identical-result Set calls.
        // refreshCount=10 triggers exactly one decay (DecayThreshold=10), leaving
        // unchanged=4, total=5, score=0.80, multiplier=3.4x.
        private void BuildStability(int indexerId, NewznabRequest request, int refreshCount = 10)
        {
            for (var i = 0; i < refreshCount; i++)
            {
                Subject.Set(indexerId, request, MakeResults(3), TimeSpan.FromMinutes(10));
            }
        }

        // Returns a single-release NewznabResults with a unique GUID per index.
        // Calling with sequential indices guarantees every Set is detected as changed.
        private static NewznabResults MakeVolatileResults(int index, string guidPrefix = "volatile")
        {
            return new NewznabResults
            {
                Releases = new List<ReleaseInfo>
                {
                    new ReleaseInfo { Title = $"Release {index}", Guid = $"{guidPrefix}-guid-{index}" }
                }
            };
        }

        private static NewznabResults MakeGuidResults(IEnumerable<string> guids)
        {
            return new NewznabResults
            {
                Releases = guids.Select(guid => (ReleaseInfo)new ReleaseInfo { Guid = guid, Title = guid }).ToList()
            };
        }

        private static NewznabResults MakeConsecutiveGuidResults(int start, int count)
        {
            return MakeGuidResults(Enumerable.Range(start, count).Select(i => $"guid-{i}"));
        }

        private static NewznabResults MakeTitleSizeResults(params (string Title, long? Size)[] entries)
        {
            return new NewznabResults
            {
                Releases = entries.Select(entry => (ReleaseInfo)new ReleaseInfo { Title = entry.Title, Size = entry.Size }).ToList()
            };
        }

        private object GetIndexerStats(int indexerId)
        {
            var statsField = typeof(NewznabResultsCacheService)
                .GetField("_indexerStats", BindingFlags.Instance | BindingFlags.NonPublic);
            var statsDictionary = statsField.GetValue(Subject);
            var tryGetValue = statsDictionary.GetType().GetMethod("TryGetValue");

            var args = new object[] { indexerId, null };
            var exists = (bool)tryGetValue.Invoke(statsDictionary, args);
            return exists ? args[1] : null;
        }

        private long GetIndexerMisses(int indexerId)
        {
            var stats = GetIndexerStats(indexerId);
            if (stats == null)
            {
                return 0;
            }

            var missesField = stats.GetType().GetField("_misses", BindingFlags.Instance | BindingFlags.NonPublic);
            return (long)missesField.GetValue(stats);
        }

        private long GetIndexerHits(int indexerId)
        {
            var stats = GetIndexerStats(indexerId);
            if (stats == null)
            {
                return 0;
            }

            var hitsField = stats.GetType().GetField("_hits", BindingFlags.Instance | BindingFlags.NonPublic);
            return (long)hitsField.GetValue(stats);
        }

        private long GetGlobalHits()
        {
            var field = typeof(NewznabResultsCacheService)
                .GetField("_globalHits", BindingFlags.Instance | BindingFlags.NonPublic);
            return (long)field.GetValue(Subject);
        }

        private CanonicalCacheIdentity ResolveIdentity(int indexerId, NewznabRequest request, IndexerCapabilities capabilities = null)
        {
            return Mocker.Resolve<NewznabCacheIdentityResolver>()
                .Resolve(indexerId, request, capabilities ?? new IndexerCapabilities());
        }

        private ConcurrentDictionary<string, T> GetStringDictionary<T>(string fieldName)
        {
            var field = typeof(NewznabResultsCacheService)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);

            return (ConcurrentDictionary<string, T>)field.GetValue(Subject);
        }

        private ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> GetIndexerKeys()
        {
            var field = typeof(NewznabResultsCacheService)
                .GetField("_indexerKeys", BindingFlags.Instance | BindingFlags.NonPublic);

            return (ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>)field.GetValue(Subject);
        }

        private (long Unchanged, long Total) GetStabilityTrackerCounts(string key)
        {
            var trackersField = typeof(NewznabResultsCacheService)
                .GetField("_stabilityTrackers", BindingFlags.Instance | BindingFlags.NonPublic);
            var trackers = trackersField.GetValue(Subject);
            var tryGetValue = trackers.GetType().GetMethod("TryGetValue");
            var args = new object[] { key, null };

            ((bool)tryGetValue.Invoke(trackers, args)).Should().BeTrue("stability tracker should exist for the canonical key");

            var tracker = args[1];
            var unchangedField = tracker.GetType().GetField("_unchangedRefreshes", BindingFlags.Instance | BindingFlags.NonPublic);
            var totalField = tracker.GetType().GetField("_totalRefreshes", BindingFlags.Instance | BindingFlags.NonPublic);

            return ((long)unchangedField.GetValue(tracker), (long)totalField.GetValue(tracker));
        }

        private ICached<NewznabResults> GetCacheStore()
        {
            var field = typeof(NewznabResultsCacheService)
                .GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic);

            return (ICached<NewznabResults>)field.GetValue(Subject);
        }

        private void InvokeCleanupKeyTracking(string key, bool preserveTracking = false)
        {
            var method = typeof(NewznabResultsCacheService)
                .GetMethod("CleanupKeyTracking", BindingFlags.Instance | BindingFlags.NonPublic);

            method.Invoke(Subject, new object[] { key, preserveTracking });
        }

        private void InvokePruneExpiredKeys(int indexerId, bool preserveTracking)
        {
            var method = typeof(NewznabResultsCacheService)
                .GetMethod("PruneExpiredKeys", BindingFlags.Instance | BindingFlags.NonPublic);

            method.Invoke(Subject, new object[] { indexerId, GetIndexerKeys()[indexerId], preserveTracking });
        }

        private void PopulateAdjunctState(int indexerId, NewznabRequest request, TimeSpan ttl)
        {
            var identity = ResolveIdentity(indexerId, request);
            Subject.Set(identity, request, MakeResults(3), ttl, "Indexer");
            Subject.RecordBypassFailure(identity, DateTime.UtcNow);
        }

        private bool PrivateDictionaryContainsKey(string fieldName, string key)
        {
            var field = typeof(NewznabResultsCacheService)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            var dictionary = field.GetValue(Subject);
            var containsKey = dictionary.GetType().GetMethod("ContainsKey");

            return (bool)containsKey.Invoke(dictionary, new object[] { key });
        }

        private void AssertAdjunctStateRemoved(string key, bool previousGuidSetsRemoved)
        {
            GetStringDictionary<CachedEntryMetadata>("_entryMetadata").ContainsKey(key).Should().BeFalse();
            GetStringDictionary<DateTime>("_bypassSuppressedUntilUtc").ContainsKey(key).Should().BeFalse();
            GetStringDictionary<HashSet<string>>("_previousGuidSets").ContainsKey(key).Should().Be(!previousGuidSetsRemoved);
        }

        private long GetGlobalMisses()
        {
            var field = typeof(NewznabResultsCacheService)
                .GetField("_globalMisses", BindingFlags.Instance | BindingFlags.NonPublic);
            return (long)field.GetValue(Subject);
        }

        private void MakeTrackerStale(NewznabRequest request, int indexerId = 1)
        {
            var key = NewznabResultsCacheService.GenerateCacheKey(indexerId, request);

            var trackersField = typeof(NewznabResultsCacheService)
                .GetField("_stabilityTrackers", BindingFlags.Instance | BindingFlags.NonPublic);
            var trackers = trackersField.GetValue(Subject);

            var tryGetValue = trackers.GetType().GetMethod("TryGetValue");
            var args = new object[] { key, null };
            if (!(bool)tryGetValue.Invoke(trackers, args))
            {
                Assert.Fail("MakeTrackerStale: no stability tracker found — ensure the request qualifies for adaptive RSS caching before this helper.");
                return;
            }

            var tracker = args[1];
            var ticksField = tracker.GetType()
                .GetField("_lastUpdatedTicks", BindingFlags.Instance | BindingFlags.NonPublic);
            ticksField.SetValue(tracker, DateTime.UtcNow.AddHours(-2).Ticks);
        }

        // ── Cache CRUD ─────────────────────────────────────────────────────────
        [Test]
        public void Find_should_return_null_on_cache_miss()
        {
            var result = Subject.Find(1, MakeRequest(q: "test"));
            result.Should().BeNull();
        }

        [Test]
        public void Find_should_not_reset_stability_tracker_on_expiry_miss()
        {
            var req = MakeRequest(cat: "5000");

            // Build enough observations for adaptive TTL to engage.
            for (var i = 0; i < 3; i++)
            {
                Subject.Set(1, req, MakeResults(3), TimeSpan.FromMilliseconds(25));
            }

            Thread.Sleep(75);
            Subject.Find(1, req).Should().BeNull("entry should expire to simulate refresh miss");

            // Next refresh should continue existing stability history, not restart from zero.
            Subject.Set(1, req, MakeResults(3), TimeSpan.FromMinutes(10));

            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, req, baseTtl).TotalSeconds
                .Should().BeGreaterThan(600, "expiry miss must not wipe adaptive history");
        }

        [Test]
        public void FindForRecheck_should_not_record_additional_miss()
        {
            var req = MakeRequest(cat: "5000");

            Subject.Find(1, req).Should().BeNull();
            Subject.FindForRecheck(1, req).Should().BeNull();

            GetIndexerMisses(1).Should().Be(1, "dedup re-check miss must not double-count misses");
        }

        [Test]
        public void FindForRecheck_hit_should_not_count_stats()
        {
            var req = MakeRequest(cat: "5000");
            Subject.Set(1, req, MakeResults(3), TimeSpan.FromMinutes(10));

            var hitsBefore = GetGlobalHits();
            Subject.FindForRecheck(1, req).Should().NotBeNull("cache entry exists");

            GetGlobalHits().Should().Be(hitsBefore, "recheck hit must not inflate global hit counter");
        }

        [Test]
        public void Find_should_only_count_hits_for_adaptive_rss_queries()
        {
            var rssRequest = MakeRequest(cat: "5000");
            var searchRequest = MakeRequest(q: "specific.title", cat: "5000");
            var cachetimeRequest = new NewznabRequest { t = "search", cat = "5000", cachetime = 300 };

            Subject.Set(1, rssRequest, MakeResults(3), TimeSpan.FromMinutes(10));
            Subject.Set(1, searchRequest, MakeResults(3), TimeSpan.FromMinutes(10));
            Subject.Set(1, cachetimeRequest, MakeResults(3), TimeSpan.FromMinutes(10));

            var indexerHitsBefore = GetIndexerHits(1);
            var globalHitsBefore = GetGlobalHits();

            Subject.Find(1, rssRequest).Should().NotBeNull("RSS cache entry should exist");
            Subject.Find(1, searchRequest).Should().NotBeNull("search cache entry should exist");
            Subject.Find(1, cachetimeRequest).Should().NotBeNull("cachetime cache entry should exist");

            var indexerHitsAfter = GetIndexerHits(1);
            var globalHitsAfter = GetGlobalHits();

            indexerHitsAfter.Should().Be(indexerHitsBefore + 1,
                $"expected only the adaptive-RSS hit to be counted, but indexer hits changed from {indexerHitsBefore} to {indexerHitsAfter}");
            globalHitsAfter.Should().Be(globalHitsBefore + 1,
                $"expected only the adaptive-RSS hit to be counted globally, but global hits changed from {globalHitsBefore} to {globalHitsAfter}");
        }

        [Test]
        public void Find_should_only_count_misses_for_adaptive_rss_queries()
        {
            var rssRequest = MakeRequest(cat: "5000");
            var searchRequest = MakeRequest(q: "specific.title", cat: "5000");
            var cachetimeRequest = new NewznabRequest { t = "search", cat = "5000", cachetime = 300 };

            var indexerMissesBefore = GetIndexerMisses(1);
            var globalMissesBefore = GetGlobalMisses();

            Subject.Find(1, rssRequest).Should().BeNull("RSS request should miss on empty cache");
            Subject.Find(1, searchRequest).Should().BeNull("search request should miss on empty cache");
            Subject.Find(1, cachetimeRequest).Should().BeNull("cachetime request should miss on empty cache");

            var indexerMissesAfter = GetIndexerMisses(1);
            var globalMissesAfter = GetGlobalMisses();

            indexerMissesAfter.Should().Be(indexerMissesBefore + 1,
                $"expected only the adaptive-RSS miss to be counted, but indexer misses changed from {indexerMissesBefore} to {indexerMissesAfter}");
            globalMissesAfter.Should().Be(globalMissesBefore + 1,
                $"expected only the adaptive-RSS miss to be counted globally, but global misses changed from {globalMissesBefore} to {globalMissesAfter}");
        }

        [Test]
        public void Set_then_Find_should_return_cached_results()
        {
            var request = MakeRequest(q: "test");
            var results = MakeResults(3);

            Subject.Set(1, request, results, TimeSpan.FromMinutes(10));

            var found = Subject.Find(1, request);
            found.Should().NotBeNull();
            found.Releases.Count.Should().Be(3);
        }

        [Test]
        public void FindWithMetadata_should_return_cached_entry_when_metadata_matches()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            Subject.Set(identity, request, MakeResults(3), TimeSpan.FromMinutes(10), "Indexer");

            var cachedEntry = Subject.FindWithMetadata(identity, request);

            cachedEntry.Should().NotBeNull();
            cachedEntry.Results.Releases.Count.Should().Be(3);
            cachedEntry.Metadata.CachedTtlSecs.Should().Be(600);
            cachedEntry.Metadata.CachedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        }

        [Test]
        public void FindWithMetadata_should_return_null_when_cache_entry_exists_but_metadata_is_missing()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            Subject.Set(identity, request, MakeResults(3), TimeSpan.FromMinutes(10), "Indexer");

            GetStringDictionary<CachedEntryMetadata>("_entryMetadata").TryRemove(identity.Key, out _);

            Subject.FindWithMetadata(identity, request).Should().BeNull();
        }

        [Test]
        public void FindWithMetadata_should_return_null_when_metadata_exists_but_cache_entry_is_missing()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            Subject.Set(identity, request, MakeResults(3), TimeSpan.FromMinutes(10), "Indexer");

            GetCacheStore().Remove(identity.Key);

            Subject.FindWithMetadata(identity, request).Should().BeNull();
        }

        [Test]
        public void CachedEntryMetadata_should_require_utc_timestamp()
        {
            Assert.Throws<ArgumentException>(() => new CachedEntryMetadata(DateTime.Now, 600));
        }

        [Test]
        public void CachedEntryMetadata_should_require_positive_ttl()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CachedEntryMetadata(DateTime.UtcNow, 0));
        }

        [Test]
        public void Set_should_reject_non_positive_ttl()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Subject.Set(identity, request, MakeResults(1), TimeSpan.Zero, "Indexer"));
            Subject.FindWithMetadata(identity, request).Should().BeNull();
        }

        [Test]
        public void RecordBypassFailure_should_create_30_second_cooldown()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            var now = DateTime.UtcNow;

            Subject.RecordBypassFailure(identity, now).Should().BeTrue();

            var suppressedUntilUtc = Subject.GetBypassSuppressedUntilUtc(identity);

            suppressedUntilUtc.Should().NotBeNull();
            suppressedUntilUtc.Value.Should().BeOnOrAfter(now.AddSeconds(29));
            suppressedUntilUtc.Value.Should().BeOnOrBefore(now.AddSeconds(31));
        }

        [Test]
        public void GetBypassSuppressedUntilUtc_should_ignore_expired_cooldown_without_removing_it()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            Subject.RecordBypassFailure(identity, DateTime.UtcNow.AddSeconds(-31)).Should().BeTrue();

            Subject.GetBypassSuppressedUntilUtc(identity).Should().BeNull();
            GetStringDictionary<DateTime>("_bypassSuppressedUntilUtc").ContainsKey(identity.Key).Should().BeTrue();
        }

        [Test]
        public void InvalidateIndexer_should_remove_entries_for_that_indexer_only()
        {
            var reqA = MakeRequest(q: "alpha");
            var reqB = MakeRequest(q: "beta");

            Subject.Set(1, reqA, MakeResults(1), TimeSpan.FromMinutes(10));
            Subject.Set(1, reqB, MakeResults(1), TimeSpan.FromMinutes(10));
            Subject.Set(2, reqA, MakeResults(1), TimeSpan.FromMinutes(10));

            Subject.InvalidateIndexer(1);

            Subject.Find(1, reqA).Should().BeNull("indexer 1 reqA should be evicted");
            Subject.Find(1, reqB).Should().BeNull("indexer 1 reqB should be evicted");
            Subject.Find(2, reqA).Should().NotBeNull("indexer 2 should be untouched");
        }

        [Test]
        public void Clear_should_remove_all_entries()
        {
            Subject.Set(1, MakeRequest(q: "a"), MakeResults(1), TimeSpan.FromMinutes(10));
            Subject.Set(2, MakeRequest(q: "b"), MakeResults(1), TimeSpan.FromMinutes(10));

            Subject.Clear();

            Subject.Find(1, MakeRequest(q: "a")).Should().BeNull();
            Subject.Find(2, MakeRequest(q: "b")).Should().BeNull();
        }

        // ── Cache key ──────────────────────────────────────────────────────────
        [Test]
        public void ResolveCacheIdentity_should_be_deterministic()
        {
            var req = MakeRequest(q: "test");
            var key1 = ResolveIdentity(1, req).Key;
            var key2 = ResolveIdentity(1, req).Key;
            key1.Should().Be(key2);
        }

        [Test]
        public void ResolveCacheIdentity_should_differ_on_different_params()
        {
            var key1 = ResolveIdentity(1, MakeRequest(q: "foo")).Key;
            var key2 = ResolveIdentity(1, MakeRequest(q: "bar")).Key;
            key1.Should().NotBe(key2);
        }

        [Test]
        public void ResolveCacheIdentity_should_collapse_different_extended()
        {
            var key1 = ResolveIdentity(1, MakeRequest(extended: "1")).Key;
            var key2 = ResolveIdentity(1, MakeRequest(extended: null)).Key;
            key1.Should().Be(key2);
        }

        [Test]
        public void ResolveCacheIdentity_should_normalize_cat_order()
        {
            var key1 = ResolveIdentity(1, new NewznabRequest { t = "search", cat = "5000,2000" }).Key;
            var key2 = ResolveIdentity(1, new NewznabRequest { t = "search", cat = "2000,5000" }).Key;
            key1.Should().Be(key2, "category order should not affect cache key");
        }

        [Test]
        public void ResolveCacheIdentity_should_preserve_q_whitespace()
        {
            var key1 = ResolveIdentity(1, new NewznabRequest { t = "search", q = "test" }).Key;
            var key2 = ResolveIdentity(1, new NewznabRequest { t = "search", q = " test " }).Key;
            key1.Should().NotBe(key2, "canonical q must be preserved as-is, including leading and trailing whitespace");
            key2.Should().Contain("q= test ", "the canonical key should keep q exactly as supplied");
        }

        [Test]
        public void ResolveCacheIdentity_should_exclude_volatile_fields()
        {
            var req1 = new NewznabRequest { t = "search", q = "test", source = "a", host = "x", server = "s1", configured = "1", cachetime = 300 };
            var req2 = new NewznabRequest { t = "search", q = "test", source = "b", host = "y", server = "s2", configured = "0", cachetime = 600 };

            var key1 = ResolveIdentity(1, req1).Key;
            var key2 = ResolveIdentity(1, req2).Key;
            key1.Should().Be(key2, "source, host, server, configured, cachetime are volatile/meta and excluded from the cache key");
        }

        // ── TTL resolution ─────────────────────────────────────────────────────
        [Test]
        public void ResolveTtl_should_return_null_for_cachetime_zero()
        {
            var ttl = Subject.ResolveTtl(1, requestCacheTimeSecs: 0, indexerCacheTtlMins: null);
            ttl.Should().BeNull("cachetime=0 is the bypass sentinel");
        }

        [Test]
        public void ResolveTtl_should_use_request_cachetime_when_provided()
        {
            // 900s > 300s floor → no clamping
            var ttl = Subject.ResolveTtl(1, requestCacheTimeSecs: 900, indexerCacheTtlMins: 20);
            ttl.Should().Be(TimeSpan.FromSeconds(900), "per-request takes priority over per-indexer");
        }

        [Test]
        public void ResolveTtl_should_use_indexer_ttl_when_no_request_override()
        {
            // 15 min = 900s > 300s floor
            var ttl = Subject.ResolveTtl(1, requestCacheTimeSecs: null, indexerCacheTtlMins: 15);
            ttl.Should().Be(TimeSpan.FromSeconds(900));
        }

        [Test]
        public void ResolveTtl_should_use_global_default_when_no_overrides()
        {
            // global default = 10 min = 600s > 300s floor
            var ttl = Subject.ResolveTtl(1, requestCacheTimeSecs: null, indexerCacheTtlMins: null);
            ttl.Should().Be(TimeSpan.FromSeconds(600));
        }

        [Test]
        public void ResolveTtl_should_enforce_global_minimum_floor()
        {
            // 60s < 300s floor → clamped to 300s
            var ttl = Subject.ResolveTtl(1, requestCacheTimeSecs: 60, indexerCacheTtlMins: null);
            ttl.Should().Be(TimeSpan.FromSeconds(300), "global minimum floor of 5 minutes applies");
        }

        [Test]
        public void ResolveTtl_should_clamp_negative_cachetime_to_minimum_floor()
        {
            // -1s < 300s floor → clamped to 300s (same as any below-floor value)
            var ttl = Subject.ResolveTtl(1, requestCacheTimeSecs: -1, indexerCacheTtlMins: null);
            ttl.Should().Be(TimeSpan.FromSeconds(300), "negative cachetime is clamped to global minimum floor");
        }

        // ── Dedup ──────────────────────────────────────────────────────────────
        [Test]
        public async Task DeduplicateAsync_should_serialize_concurrent_requests()
        {
            var request = MakeRequest(q: "concurrent");
            var gate = new TaskCompletionSource<bool>();
            var factoryEntered = new ManualResetEventSlim(false);
            var maxConcurrency = 0;
            var currentConcurrency = 0;
            var factoryCallCount = 0;

            async Task<int> Factory()
            {
                Interlocked.Increment(ref factoryCallCount);
                var current = Interlocked.Increment(ref currentConcurrency);

                // Track peak concurrency (atomic compare-and-swap loop)
                int observed;
                do
                {
                    observed = Volatile.Read(ref maxConcurrency);
                }
                while (current > observed && Interlocked.CompareExchange(ref maxConcurrency, current, observed) != observed);

                // Signal that the factory has been entered (used by first call only)
                factoryEntered.Set();

                if (factoryCallCount == 1)
                {
                    // First caller blocks until we release the gate
                    await gate.Task;
                }

                Interlocked.Decrement(ref currentConcurrency);
                return 42;
            }

            // Start first request (will block at gate inside factory)
            var task1 = Subject.DeduplicateAsync(1, request, Factory);

            // Wait deterministically for factory entry instead of Task.Delay
            factoryEntered.Wait(TimeSpan.FromSeconds(5));

            // Start second request (will block on semaphore, not enter factory yet)
            factoryEntered.Reset();
            var task2 = Subject.DeduplicateAsync(1, request, Factory);

            // Release gate — task1 completes, task2 can now enter factory
            gate.SetResult(true);

            await Task.WhenAll(task1, task2);

            maxConcurrency.Should().Be(1, "semaphore serializes access — never 2 concurrent factory calls");
            factoryCallCount.Should().Be(2, "both requests pass through the factory (dedup serializes, not skips)");
        }

        [Test]
        public async Task DeduplicateAsync_should_release_semaphore_on_factory_exception()
        {
            var request = MakeRequest(q: "error");

            // First call: factory throws
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await Subject.DeduplicateAsync<int>(1, request, () =>
                    Task.FromException<int>(new InvalidOperationException("upstream failure")));
            });

            // Second call: factory succeeds — must not hang (semaphore was released)
            var result = await Subject.DeduplicateAsync(1, request, () => Task.FromResult(42));
            result.Should().Be(42, "semaphore must be released even when factory throws");
        }

        // ── Event handlers ─────────────────────────────────────────────────────
        [Test]
        public void Handle_bulk_updated_should_invalidate_all_affected_indexers()
        {
            var req = MakeRequest(q: "x");
            Subject.Set(1, req, MakeResults(1), TimeSpan.FromMinutes(10));
            Subject.Set(2, req, MakeResults(1), TimeSpan.FromMinutes(10));
            Subject.Set(3, req, MakeResults(1), TimeSpan.FromMinutes(10));

            Subject.Handle(new ProviderBulkUpdatedEvent<IIndexer>(new[]
            {
                new IndexerDefinition { Id = 1 },
                new IndexerDefinition { Id = 2 }
            }));

            Subject.Find(1, req).Should().BeNull("indexer 1 was bulk-updated");
            Subject.Find(2, req).Should().BeNull("indexer 2 was bulk-updated");
            Subject.Find(3, req).Should().NotBeNull("indexer 3 was not updated");
        }

        [Test]
        public void Handle_bulk_deleted_should_invalidate_all_affected_indexers()
        {
            var req = MakeRequest(q: "x");
            Subject.Set(1, req, MakeResults(1), TimeSpan.FromMinutes(10));
            Subject.Set(2, req, MakeResults(1), TimeSpan.FromMinutes(10));
            Subject.Set(3, req, MakeResults(1), TimeSpan.FromMinutes(10));

            Subject.Handle(new ProviderBulkDeletedEvent<IIndexer>(new[] { 1, 3 }));

            Subject.Find(1, req).Should().BeNull("indexer 1 was bulk-deleted");
            Subject.Find(2, req).Should().NotBeNull("indexer 2 was not deleted");
            Subject.Find(3, req).Should().BeNull("indexer 3 was bulk-deleted");
        }

        // ── Adaptive TTL ──────────────────────────────────────────────────────
        [Test]
        public void GetAdaptiveTtl_should_extend_ttl_for_stable_rss_query()
        {
            // RSS-like query: no content-narrowing params, only categories
            var req = MakeRequest(cat: "5000");

            BuildStability(1, req);

            // stability ≈ 4/5 after first decay (9 unchanged → halved to 4, total halved to 5) → multiplier ~3.4x → adaptive TTL > base
            var baseTtl = TimeSpan.FromSeconds(600);
            var adaptiveTtl = Subject.GetAdaptiveTtl(1, req, baseTtl);
            adaptiveTtl.TotalSeconds.Should().BeGreaterThan(600, "stable RSS should get extended TTL");
        }

        [Test]
        public void GetAdaptiveTtl_should_not_extend_for_volatile_rss_query()
        {
            var req = MakeRequest(cat: "5000");

            // Every refresh has different GUIDs for an adaptive-RSS request,
            // so Jaccard similarity stays at 0 and the tracker never records unchanged=true.
            for (var i = 0; i < 10; i++)
            {
                Subject.Set(1, req, MakeVolatileResults(i), TimeSpan.FromMinutes(10));
            }

            var baseTtl = TimeSpan.FromSeconds(600);
            var adaptiveTtl = Subject.GetAdaptiveTtl(1, req, baseTtl);
            adaptiveTtl.Should().Be(baseTtl, "volatile results should not extend TTL");
        }

        [Test]
        public void GetAdaptiveTtl_should_extend_when_jaccard_exceeds_threshold()
        {
            var req = MakeRequest(cat: "5000");
            var baseline = Enumerable.Range(1, 100).Select(i => $"guid-{i}").ToArray();
            var shifted = Enumerable.Range(6, 100).Select(i => $"guid-{i}").ToArray(); // 95 overlap => 95 / 105 > 0.9

            Subject.Set(1, req, MakeGuidResults(baseline), TimeSpan.FromMinutes(10));
            Subject.Set(1, req, MakeGuidResults(shifted), TimeSpan.FromMinutes(10));
            Subject.Set(1, req, MakeGuidResults(baseline), TimeSpan.FromMinutes(10));

            Subject.GetAdaptiveTtl(1, req, TimeSpan.FromSeconds(600)).TotalSeconds
                .Should().BeGreaterThan(600, "95% overlap across refreshes should count as unchanged");
        }

        [Test]
        public void GetAdaptiveTtl_should_not_extend_when_jaccard_stays_below_threshold()
        {
            var req = MakeRequest(cat: "5000");
            var baseline = Enumerable.Range(1, 100).Select(i => $"guid-{i}").ToArray();
            var shifted = Enumerable.Range(7, 100).Select(i => $"guid-{i}").ToArray(); // 94 overlap => 94 / 106 < 0.9

            Subject.Set(1, req, MakeGuidResults(baseline), TimeSpan.FromMinutes(10));
            Subject.Set(1, req, MakeGuidResults(shifted), TimeSpan.FromMinutes(10));
            Subject.Set(1, req, MakeGuidResults(baseline), TimeSpan.FromMinutes(10));

            Subject.GetAdaptiveTtl(1, req, TimeSpan.FromSeconds(600))
                .Should().Be(TimeSpan.FromSeconds(600), "94% overlap should remain below the 0.9 Jaccard threshold");
        }

        [TestCase(90, 10, 0, false, TestName = "Jaccard_exactly_0_8_should_record_changed")]
        [TestCase(95, 5, 2, true, TestName = "Jaccard_exactly_0_9_should_record_stable")]
        [TestCase(100, 5, 2, true, TestName = "Jaccard_above_0_9_should_record_stable")]
        public void GetAdaptiveTtl_should_respect_jaccard_threshold_boundaries(int setSize, int shift, int expectedUnchangedRefreshes, bool shouldExtend)
        {
            var req = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, req);

            Subject.Set(identity, req, MakeConsecutiveGuidResults(1, setSize), TimeSpan.FromMinutes(10));
            Subject.Set(identity, req, MakeConsecutiveGuidResults(1 + shift, setSize), TimeSpan.FromMinutes(10));
            Subject.Set(identity, req, MakeConsecutiveGuidResults(1 + (shift * 2), setSize), TimeSpan.FromMinutes(10));

            var trackerCounts = GetStabilityTrackerCounts(identity.Key);
            trackerCounts.Unchanged.Should().Be(expectedUnchangedRefreshes);
            trackerCounts.Total.Should().Be(3);

            var adaptiveTtl = Subject.GetAdaptiveTtl(identity, TimeSpan.FromSeconds(600));
            if (shouldExtend)
            {
                adaptiveTtl.TotalSeconds.Should().BeGreaterThan(600);
            }
            else
            {
                adaptiveTtl.Should().Be(TimeSpan.FromSeconds(600));
            }
        }

        [Test]
        public void GetAdaptiveTtl_should_ignore_no_guid_tuple_changes_when_any_guid_is_present()
        {
            var req = MakeRequest(cat: "5000");

            Subject.Set(
                1,
                req,
                new NewznabResults
                {
                    Releases = new List<ReleaseInfo>
                    {
                        new ReleaseInfo { Guid = "guid-1", Title = "Release 1" },
                        new ReleaseInfo { Title = "Tuple A", Size = 1000 }
                    }
                },
                TimeSpan.FromMinutes(10));

            Subject.Set(
                1,
                req,
                new NewznabResults
                {
                    Releases = new List<ReleaseInfo>
                    {
                        new ReleaseInfo { Guid = "guid-1", Title = "Release 1" },
                        new ReleaseInfo { Title = "Tuple B", Size = 2000 }
                    }
                },
                TimeSpan.FromMinutes(10));

            Subject.Set(
                1,
                req,
                new NewznabResults
                {
                    Releases = new List<ReleaseInfo>
                    {
                        new ReleaseInfo { Guid = "guid-1", Title = "Release 1" },
                        new ReleaseInfo { Title = "Tuple C", Size = 3000 }
                    }
                },
                TimeSpan.FromMinutes(10));

            Subject.GetAdaptiveTtl(1, req, TimeSpan.FromSeconds(600)).TotalSeconds
                .Should().BeGreaterThan(600, "when any GUID exists, no-GUID tuple-only releases must not affect identity comparison");
        }

        [Test]
        public void GetAdaptiveTtl_should_return_base_when_no_stability_data()
        {
            var req = MakeRequest(cat: "5000");
            var baseTtl = TimeSpan.FromSeconds(600);

            // No stability data because no qualifying adaptive-RSS Set call occurred
            var adaptiveTtl = Subject.GetAdaptiveTtl(1, req, baseTtl);
            adaptiveTtl.Should().Be(baseTtl, "no stability data means no extension");
        }

        [Test]
        public void GetAdaptiveTtl_should_not_track_non_rss_queries()
        {
            // Title search: same results every time, but request shape is not adaptive-RSS
            var req = MakeRequest(q: "specific.title", cat: "5000");
            for (var i = 0; i < 10; i++)
            {
                Subject.Set(1, req, MakeResults(3), TimeSpan.FromMinutes(10));
            }

            // No stability tracked → adaptive TTL returns base unchanged
            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, req, baseTtl)
                .Should().Be(baseTtl, "non-RSS queries are never tracked for stability");
        }

        [Test]
        public void GetAdaptiveTtl_should_cap_at_maximum()
        {
            var req = MakeRequest(cat: "5000");

            for (var i = 0; i < 10; i++)
            {
                Subject.Set(1, req, MakeResults(3), TimeSpan.FromMinutes(30));
            }

            // 1800s base × ~3.4 multiplier (score 4/5 after decay) = ~6120s, capped at 1800s
            var baseTtl = TimeSpan.FromSeconds(1800);
            var adaptiveTtl = Subject.GetAdaptiveTtl(1, req, baseTtl);
            adaptiveTtl.TotalSeconds.Should().BeLessOrEqualTo(1800, "adaptive TTL capped at 30 min");
        }

        [Test]
        public void GetAdaptiveTtl_should_track_per_key_not_per_indexer()
        {
            var rssReq = MakeRequest(cat: "5000");
            var rssReq2 = MakeRequest(cat: "7000");

            // cat=5000 RSS: stable
            BuildStability(1, rssReq);

            // cat=7000 RSS: volatile
            for (var i = 0; i < 10; i++)
            {
                Subject.Set(1, rssReq2, MakeVolatileResults(i, "rss2"), TimeSpan.FromMinutes(10));
            }

            var baseTtl = TimeSpan.FromSeconds(600);

            // cat=5000 should be extended (stable)
            Subject.GetAdaptiveTtl(1, rssReq, baseTtl).TotalSeconds
                .Should().BeGreaterThan(600, "stable cat=5000 key should get extended TTL");

            // cat=7000 should NOT be extended (volatile)
            Subject.GetAdaptiveTtl(1, rssReq2, baseTtl)
                .Should().Be(baseTtl, "volatile cat=7000 key should stay at base TTL");
        }

        // ── Clone hardening ──────────────────────────────────────────────────
        [Test]
        public void Set_should_freeze_releases_list()
        {
            Subject.Set(1, MakeRequest(q: "frozen"), MakeResults(3), TimeSpan.FromMinutes(10));

            var found = Subject.Find(1, MakeRequest(q: "frozen"));
            found.Should().NotBeNull();

            // Cached releases list should be frozen — mutation throws
            Assert.Throws<NotSupportedException>(() => found.Releases.Add(new ReleaseInfo()));
        }

        // ── Stability tracker reset ──────────────────────────────────────────
        [Test]
        public void InvalidateIndexer_should_reset_stability_tracker()
        {
            var req = MakeRequest(cat: "5000");

            BuildStability(1, req);

            Subject.InvalidateIndexer(1);

            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, req, baseTtl)
                .Should().Be(baseTtl, "invalidation resets stability tracker");
        }

        [Test]
        public void PruneExpiredKeys_should_preserve_stability_tracker_for_recent_rss_key()
        {
            var rssReq = MakeRequest(cat: "5000");

            // Build stability — 3 identical Sets → score > 0 (unchanged=2, total=3)
            for (var i = 0; i < 3; i++)
            {
                Subject.Set(1, rssReq, MakeResults(3), TimeSpan.FromMilliseconds(25));
            }

            // Flood 51 filler keys to push keys.Count above KeyPruneThreshold (50)
            for (var i = 0; i < 51; i++)
            {
                Subject.Set(1, MakeRequest(q: $"filler-{i}"), MakeResults(1), TimeSpan.FromMilliseconds(25));
            }

            // Wait for all entries to expire
            Thread.Sleep(75);

            // This Set pushes keys.Count > 50 → triggers PruneExpiredKeys
            Subject.Set(1, MakeRequest(q: "trigger"), MakeResults(1), TimeSpan.FromMinutes(10));

            // Stability tracker must survive PruneExpiredKeys
            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, rssReq, baseTtl).TotalSeconds
                .Should().BeGreaterThan(600, "PruneExpiredKeys must not destroy stability trackers for recent RSS keys");
        }

        [Test]
        public void PruneExpiredKeys_should_reclaim_stale_stability_tracker()
        {
            var rssReq = MakeRequest(cat: "5000");

            // Build stability with short TTL
            for (var i = 0; i < 3; i++)
            {
                Subject.Set(1, rssReq, MakeResults(3), TimeSpan.FromMilliseconds(25));
            }

            // Back-date tracker to simulate abandonment (IsStale = true)
            MakeTrackerStale(rssReq);

            // Flood 51 filler keys to push keys.Count above KeyPruneThreshold (50)
            for (var i = 0; i < 51; i++)
            {
                Subject.Set(1, MakeRequest(q: $"filler-{i}"), MakeResults(1), TimeSpan.FromMilliseconds(25));
            }

            // Wait for all entries to expire
            Thread.Sleep(75);

            // Trigger PruneExpiredKeys with all entries expired
            Subject.Set(1, MakeRequest(q: "trigger"), MakeResults(1), TimeSpan.FromMinutes(10));

            // Stale tracker must be reclaimed → adaptive TTL returns base unchanged
            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, rssReq, baseTtl)
                .Should().Be(baseTtl, "PruneExpiredKeys must reclaim stale stability trackers");
        }

        // ── Fingerprint edge cases ────────────────────────────────────────────
        [Test]
        public void GetAdaptiveTtl_should_detect_change_when_all_guids_null()
        {
            var req = MakeRequest(cat: "5000");

            for (var i = 0; i < 10; i++)
            {
                var titles = i % 2 == 0
                    ? new[] { "Alpha", "Beta", "Gamma" }
                    : new[] { "Delta", "Epsilon", "Zeta" };

                var results = new NewznabResults
                {
                    Releases = titles.Select((title, index) => new ReleaseInfo { Title = title, Size = 1000 + index }).ToList<ReleaseInfo>()
                };
                Subject.Set(1, req, results, TimeSpan.FromMinutes(10));
            }

            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, req, baseTtl)
                .Should().Be(baseTtl, "alternating null-GUID title/size tuples should be volatile");
        }

        [Test]
        public void Set_should_skip_adaptive_update_and_warn_when_all_release_identity_fields_are_missing()
        {
            var req = MakeRequest(cat: "5000");
            var key = ResolveIdentity(1, req).Key;
            var results = new NewznabResults
            {
                Releases = new List<ReleaseInfo>
                {
                    new ReleaseInfo(),
                    new ReleaseInfo()
                }
            };

            Subject.Set(1, req, results, TimeSpan.FromMinutes(10));

            Subject.GetAdaptiveTtl(1, req, TimeSpan.FromSeconds(600))
                .Should().Be(TimeSpan.FromSeconds(600));
            GetStringDictionary<HashSet<string>>("_previousGuidSets").ContainsKey(key).Should().BeFalse();
            ExceptionVerification.ExpectedWarns(1);
        }

        // Verifies GUIDs are sorted before hashing: [a,b,c] and [c,b,a] must
        // produce the same fingerprint and be counted as unchanged → stable →
        // adaptive TTL extended above base.
        [Test]
        public void GetAdaptiveTtl_should_be_order_independent()
        {
            var req = MakeRequest(cat: "5000");

            for (var i = 0; i < 10; i++)
            {
                var guids = i % 2 == 0
                    ? new[] { "guid-a", "guid-b", "guid-c" }
                    : new[] { "guid-c", "guid-b", "guid-a" };

                var results = new NewznabResults
                {
                    Releases = guids.Select(g => new ReleaseInfo { Title = g, Guid = g }).ToList<ReleaseInfo>()
                };
                Subject.Set(1, req, results, TimeSpan.FromMinutes(10));
            }

            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, req, baseTtl).TotalSeconds
                .Should().BeGreaterThan(600, "reordered identical GUIDs should be treated as stable");
        }

        // ── Stability decay ──────────────────────────────────────────────────

        // Verifies decay adapts to changing conditions: after building high stability,
        // sustained volatile refreshes halve the unchanged counter three times (at
        // total=10,20,30) until unchanged=0, score=0 → adaptive TTL returns base.
        [Test]
        public void GetAdaptiveTtl_should_decay_after_stable_to_volatile_transition()
        {
            var req = MakeRequest(cat: "5000");
            var baseTtl = TimeSpan.FromSeconds(600);

            BuildStability(1, req, 10);
            Subject.GetAdaptiveTtl(1, req, baseTtl).TotalSeconds
                .Should().BeGreaterThan(600, "precondition: stable phase must extend TTL");

            // 20 volatile refreshes trigger two more decay cycles:
            // cycle at refresh 5: unchanged=4→2; cycle at refresh 10: 2→1;
            // cycle at refresh 15: 1→0; score collapses to 0.
            for (var i = 0; i < 20; i++)
            {
                Subject.Set(1, req, MakeVolatileResults(i), TimeSpan.FromMinutes(10));
            }

            Subject.GetAdaptiveTtl(1, req, baseTtl)
                .Should().Be(baseTtl, "stability must decay to zero after sustained volatile period");
        }

        [Test]
        public void WriteCacheEntryWithMetadata_should_expose_consistent_pairs_under_concurrent_reads()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            Subject.Set(identity, request, MakeGuidResults(new[] { "old-guid" }), TimeSpan.FromMinutes(10), "Indexer");

            var stop = false;
            var mismatchedReads = 0;
            var reader = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var cachedEntry = Subject.FindWithMetadata(identity, request);
                    if (cachedEntry == null)
                    {
                        continue;
                    }

                    var guid = cachedEntry.Results.Releases.Single().Guid;
                    var ttlSecs = cachedEntry.Metadata.CachedTtlSecs;

                    if ((guid == "old-guid" && ttlSecs != 600) ||
                        (guid == "new-guid" && ttlSecs != 1200))
                    {
                        Interlocked.Increment(ref mismatchedReads);
                    }
                }
            });

            Subject.Set(identity, request, MakeGuidResults(new[] { "new-guid" }), TimeSpan.FromMinutes(20), "Indexer");
            Volatile.Write(ref stop, true);
            reader.Wait(TimeSpan.FromSeconds(5));

            mismatchedReads.Should().Be(0, "FindWithMetadata should never mix a result snapshot with the wrong metadata snapshot");

            var finalRead = Subject.FindWithMetadata(identity, request);
            finalRead.Should().NotBeNull("after the concurrent write completes, the final entry should be fully observable");
            finalRead.Results.Releases.Single().Guid.Should().Be("new-guid");
            finalRead.Metadata.CachedTtlSecs.Should().Be(1200);
        }

        [Test]
        public void Bypass_failure_logging_should_not_include_apikey_from_http_exception()
        {
            var controller = new NewznabController(null, null, null, null, null, null, null, null, TestLogger);
            var logMethod = typeof(NewznabController).GetMethod("LogBypassFailure", BindingFlags.Instance | BindingFlags.NonPublic);

            var sentinel = "APIKEY-SENTINEL-12345";
            var request = new HttpRequest($"https://example.test/api?t=search&cat=5000&apikey={sentinel}");
            var response = new HttpResponse(request, new HttpHeader(), new CookieCollection(), "upstream failure", statusCode: HttpStatusCode.BadGateway, version: new Version(1, 1));
            var exception = new HttpException(request, response);
            var identity = new CanonicalCacheIdentity(1, "v2:indexerId=1:cat=5000", Array.Empty<string>());
            var metadata = new CachedEntryMetadata(DateTime.UtcNow, 600);

            var target = new MemoryTarget("BypassFailureLogCapture") { Layout = "${message}" };
            var rule = new LoggingRule("*", LogLevel.Debug, target);
            LogManager.Configuration.AddTarget(target);
            LogManager.Configuration.LoggingRules.Add(rule);
            LogManager.ReconfigExistingLoggers();

            try
            {
                logMethod.Invoke(controller, new object[] { "Indexer", identity, metadata, 0.75, true, exception });

                target.Logs.Should().ContainSingle();
                target.Logs.Single().Should().NotContain(sentinel);
            }
            finally
            {
                LogManager.Configuration.LoggingRules.Remove(rule);
                LogManager.Configuration.RemoveTarget("BypassFailureLogCapture");
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void Canonical_identity_and_jaccard_stability_should_compose_across_equivalent_requests()
        {
            var capabilities = new IndexerCapabilities();
            capabilities.Categories.AddCategoryMapping(NewznabStandardCategory.TV.Id, NewznabStandardCategory.TV);
            capabilities.Categories.AddCategoryMapping(NewznabStandardCategory.TVSD.Id, NewznabStandardCategory.TVSD);
            capabilities.Categories.AddCategoryMapping(NewznabStandardCategory.TVHD.Id, NewznabStandardCategory.TVHD);
            capabilities.Categories.AddCategoryMapping(NewznabStandardCategory.TVUHD.Id, NewznabStandardCategory.TVUHD);

            var parentRequest = MakeRequest(cat: "5000", extended: "0");
            var explicitRequest = MakeRequest(cat: "5045, 5030, 5040, 5000", extended: null);
            var reorderedRequest = MakeRequest(cat: "5030,5000,5040,5045", extended: "anything");
            var parentIdentity = ResolveIdentity(1, parentRequest, capabilities);
            var explicitIdentity = ResolveIdentity(1, explicitRequest, capabilities);
            var reorderedIdentity = ResolveIdentity(1, reorderedRequest, capabilities);

            parentIdentity.Key.Should().Be(explicitIdentity.Key);
            explicitIdentity.Key.Should().Be(reorderedIdentity.Key);

            Subject.Set(parentIdentity, parentRequest, MakeConsecutiveGuidResults(1, 95), TimeSpan.FromMinutes(10), "Indexer");
            Subject.Set(explicitIdentity, explicitRequest, MakeConsecutiveGuidResults(6, 95), TimeSpan.FromMinutes(10), "Indexer");
            Subject.Set(reorderedIdentity, reorderedRequest, MakeConsecutiveGuidResults(11, 95), TimeSpan.FromMinutes(10), "Indexer");

            GetIndexerKeys()[1].Keys.Should().ContainSingle();
            Subject.FindWithMetadata(parentIdentity, parentRequest).Should().NotBeNull();
            Subject.FindWithMetadata(explicitIdentity, explicitRequest).Should().NotBeNull();
            Subject.FindWithMetadata(reorderedIdentity, reorderedRequest).Should().NotBeNull();

            var trackerCounts = GetStabilityTrackerCounts(parentIdentity.Key);
            trackerCounts.Unchanged.Should().Be(2);
            trackerCounts.Total.Should().Be(3);
            Subject.GetAdaptiveTtl(parentIdentity, TimeSpan.FromSeconds(600)).TotalSeconds.Should().BeGreaterThan(600);
        }

        [Test]
        public void Canonical_identity_and_age_scaled_bypass_should_share_metadata_and_match_curve()
        {
            var capabilities = new IndexerCapabilities();
            capabilities.Categories.AddCategoryMapping(NewznabStandardCategory.TV.Id, NewznabStandardCategory.TV);
            capabilities.Categories.AddCategoryMapping(NewznabStandardCategory.TVSD.Id, NewznabStandardCategory.TVSD);
            capabilities.Categories.AddCategoryMapping(NewznabStandardCategory.TVHD.Id, NewznabStandardCategory.TVHD);

            var parentRequest = MakeRequest(cat: "5000");
            var explicitRequest = MakeRequest(cat: "5040,5030,5000", extended: "0");
            var parentIdentity = ResolveIdentity(1, parentRequest, capabilities);
            var explicitIdentity = ResolveIdentity(1, explicitRequest, capabilities);
            parentIdentity.Key.Should().Be(explicitIdentity.Key);

            Subject.Set(parentIdentity, parentRequest, MakeResults(3), TimeSpan.FromSeconds(600), "Indexer");

            foreach (var ratio in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                GetStringDictionary<CachedEntryMetadata>("_entryMetadata")[parentIdentity.Key] =
                    new CachedEntryMetadata(DateTime.UtcNow.AddSeconds(-600 * ratio), 600);

                var cachedEntry = Subject.FindWithMetadata(explicitIdentity, explicitRequest);
                cachedEntry.Should().NotBeNull("canonically equivalent requests should share entry metadata");

                var ageRatio = Math.Clamp((DateTime.UtcNow - cachedEntry.Metadata.CachedAtUtc).TotalSeconds / cachedEntry.Metadata.CachedTtlSecs, 0.0, 1.0);
                var expectedProbability = ratio <= 0.5 ? 0.0 : ((ratio - 0.5) / 0.5) * 0.25;
                var actualProbability = NewznabCacheQueryPolicy.GetBypassProbability(ageRatio);

                actualProbability.Should().BeApproximately(expectedProbability, 0.01);
                NewznabCacheQueryPolicy.ShouldBypassCacheHit(explicitRequest, atQueryLimit: false, ageRatio, sample: 0.0)
                    .Should().Be(actualProbability > 0.0);

                if (actualProbability > 0.0)
                {
                    NewznabCacheQueryPolicy.ShouldBypassCacheHit(explicitRequest, atQueryLimit: false, ageRatio, sample: actualProbability)
                        .Should().BeFalse();
                }
            }
        }

        [Test]
        public void Adaptive_ttl_and_bypass_cooldown_should_compose()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);

            for (var i = 0; i < 3; i++)
            {
                Subject.Set(identity, request, MakeResults(3), TimeSpan.FromMinutes(10), "Indexer");
            }

            Subject.GetAdaptiveTtl(identity, TimeSpan.FromSeconds(600)).TotalSeconds.Should().BeGreaterThan(600);
            Subject.RecordBypassFailure(identity, DateTime.UtcNow).Should().BeTrue();

            NewznabCacheQueryPolicy.ShouldBypassCacheHit(
                    request,
                    atQueryLimit: false,
                    ageRatio: 1.0,
                    bypassSuppressedUntilUtc: Subject.GetBypassSuppressedUntilUtc(identity),
                    sample: 0.0)
                .Should().BeFalse("active cooldown suppresses bypass regardless of adaptive stability");

            GetStringDictionary<DateTime>("_bypassSuppressedUntilUtc")[identity.Key] = DateTime.UtcNow.AddSeconds(-1);
            Subject.GetBypassSuppressedUntilUtc(identity).Should().BeNull();

            NewznabCacheQueryPolicy.ShouldBypassCacheHit(
                    request,
                    atQueryLimit: false,
                    ageRatio: 1.0,
                    bypassSuppressedUntilUtc: Subject.GetBypassSuppressedUntilUtc(identity),
                    sample: 0.0)
                .Should().BeTrue("once cooldown expires, age-scaled bypass behavior resumes");
            Subject.GetAdaptiveTtl(identity, TimeSpan.FromSeconds(600)).TotalSeconds.Should().BeGreaterThan(600);
        }

        [Test]
        public void Bypass_failure_path_should_serve_cache_record_suppression_and_redact_log()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            Subject.Set(identity, request, MakeGuidResults(new[] { "cached-guid" }), TimeSpan.FromMinutes(10), "Indexer");
            var cachedEntry = Subject.FindWithMetadata(identity, request);

            var controller = new NewznabController(null, null, null, null, null, null, null, null, TestLogger);
            var logMethod = typeof(NewznabController).GetMethod("LogBypassFailure", BindingFlags.Instance | BindingFlags.NonPublic);
            var sentinel = "APIKEY-SENTINEL-FAILURE-COMPOSE";
            var httpRequest = new HttpRequest($"https://example.test/api?t=search&cat=5000&apikey={sentinel}");
            var response = new HttpResponse(httpRequest, new HttpHeader(), new CookieCollection(), "upstream failure", statusCode: HttpStatusCode.BadGateway, version: new Version(1, 1));
            var exception = new HttpException(httpRequest, response);
            var target = new MemoryTarget("BypassFailureComposeLogCapture") { Layout = "${message}" };
            var rule = new LoggingRule("*", LogLevel.Debug, target);

            LogManager.Configuration.AddTarget(target);
            LogManager.Configuration.LoggingRules.Add(rule);
            LogManager.ReconfigExistingLoggers();

            try
            {
                NewznabResults servedResults = null;

                try
                {
                    throw exception;
                }
                catch (Exception ex)
                {
                    var suppressionRecorded = Subject.RecordBypassFailure(identity, DateTime.UtcNow);
                    logMethod.Invoke(controller, new object[] { "Indexer", identity, cachedEntry.Metadata, 1.0, suppressionRecorded, ex });
                    servedResults = cachedEntry.Results;
                }

                servedResults.Should().NotBeNull();
                servedResults.Releases.Single().Guid.Should().Be("cached-guid");
                GetStringDictionary<DateTime>("_bypassSuppressedUntilUtc").ContainsKey(identity.Key).Should().BeTrue();
                Subject.GetBypassSuppressedUntilUtc(identity).Should().NotBeNull();
                target.Logs.Should().ContainSingle();
                target.Logs.Single().Should().NotContain(sentinel);
                target.Logs.Single().Should().NotContain("apikey=");
                target.Logs.Single().Should().NotContain("https://example.test");
            }
            finally
            {
                LogManager.Configuration.LoggingRules.Remove(rule);
                LogManager.Configuration.RemoveTarget("BypassFailureComposeLogCapture");
                LogManager.ReconfigExistingLoggers();
            }
        }

        [TestCase("cleanup-key-tracking")]
        [TestCase("invalidate-indexer")]
        [TestCase("clear")]
        [TestCase("prune-expired-remove-tracking")]
        public void Cleanup_paths_should_remove_entry_bound_state_and_previous_guid_sets(string cleanupPath)
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            PopulateAdjunctState(1, request, TimeSpan.FromMilliseconds(25));

            switch (cleanupPath)
            {
                case "cleanup-key-tracking":
                    GetCacheStore().Remove(identity.Key);
                    InvokeCleanupKeyTracking(identity.Key);
                    break;
                case "invalidate-indexer":
                    Subject.InvalidateIndexer(1);
                    break;
                case "clear":
                    Subject.Clear();
                    break;
                case "prune-expired-remove-tracking":
                    Thread.Sleep(75);
                    InvokePruneExpiredKeys(1, preserveTracking: false);
                    break;
                default:
                    Assert.Fail($"Unknown cleanup path {cleanupPath}");
                    break;
            }

            AssertAdjunctStateRemoved(identity.Key, previousGuidSetsRemoved: true);
        }

        [Test]
        public void PruneExpiredKeys_should_preserve_previous_guid_sets_for_recent_tracking_but_remove_entry_bound_state()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            PopulateAdjunctState(1, request, TimeSpan.FromMilliseconds(25));

            Thread.Sleep(75);
            InvokePruneExpiredKeys(1, preserveTracking: true);

            AssertAdjunctStateRemoved(identity.Key, previousGuidSetsRemoved: false);
            PrivateDictionaryContainsKey("_stabilityTrackers", identity.Key).Should().BeTrue();
        }

        [Test]
        public void PruneExpiredKeys_should_clear_orphaned_metadata_on_follow_up_sweep()
        {
            var request = MakeRequest(cat: "5000");
            var identity = ResolveIdentity(1, request);
            PopulateAdjunctState(1, request, TimeSpan.FromMilliseconds(25));

            Thread.Sleep(75);
            GetCacheStore().Find(identity.Key).Should().BeNull("direct cache read should inline-evict the expired entry");
            GetStringDictionary<CachedEntryMetadata>("_entryMetadata").ContainsKey(identity.Key).Should().BeTrue("metadata survives until prune reaps the orphan");

            InvokePruneExpiredKeys(1, preserveTracking: false);

            AssertAdjunctStateRemoved(identity.Key, previousGuidSetsRemoved: true);
        }

        // ── Clear resets stability ────────────────────────────────────────────
        [Test]
        public void Clear_should_reset_stability_tracker()
        {
            var req = MakeRequest(cat: "5000");
            BuildStability(1, req);

            Subject.Clear();

            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, req, baseTtl)
                .Should().Be(baseTtl, "Clear must wipe stability trackers");
        }
    }
}
