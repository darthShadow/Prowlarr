using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.ThingiProvider.Events;

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
        public void GenerateCacheKey_should_be_deterministic()
        {
            var req = MakeRequest(q: "test");
            var key1 = NewznabResultsCacheService.GenerateCacheKey(1, req);
            var key2 = NewznabResultsCacheService.GenerateCacheKey(1, req);
            key1.Should().Be(key2);
        }

        [Test]
        public void GenerateCacheKey_should_differ_on_different_params()
        {
            var key1 = NewznabResultsCacheService.GenerateCacheKey(1, MakeRequest(q: "foo"));
            var key2 = NewznabResultsCacheService.GenerateCacheKey(1, MakeRequest(q: "bar"));
            key1.Should().NotBe(key2);
        }

        [Test]
        public void GenerateCacheKey_should_differ_on_different_extended()
        {
            var key1 = NewznabResultsCacheService.GenerateCacheKey(1, MakeRequest(extended: "1"));
            var key2 = NewznabResultsCacheService.GenerateCacheKey(1, MakeRequest(extended: null));
            key1.Should().NotBe(key2);
        }

        [Test]
        public void GenerateCacheKey_should_normalize_cat_order()
        {
            var key1 = NewznabResultsCacheService.GenerateCacheKey(1, new NewznabRequest { t = "search", cat = "5000,2000" });
            var key2 = NewznabResultsCacheService.GenerateCacheKey(1, new NewznabRequest { t = "search", cat = "2000,5000" });
            key1.Should().Be(key2, "category order should not affect cache key");
        }

        [Test]
        public void GenerateCacheKey_should_normalize_q_whitespace()
        {
            var key1 = NewznabResultsCacheService.GenerateCacheKey(1, new NewznabRequest { t = "search", q = "test" });
            var key2 = NewznabResultsCacheService.GenerateCacheKey(1, new NewznabRequest { t = "search", q = " test " });
            key1.Should().Be(key2, "whitespace around q should not affect cache key");
        }

        [Test]
        public void GenerateCacheKey_should_exclude_volatile_fields()
        {
            var req1 = new NewznabRequest { t = "search", q = "test", source = "a", host = "x", server = "s1", configured = "1", cachetime = 300 };
            var req2 = new NewznabRequest { t = "search", q = "test", source = "b", host = "y", server = "s2", configured = "0", cachetime = 600 };

            var key1 = NewznabResultsCacheService.GenerateCacheKey(1, req1);
            var key2 = NewznabResultsCacheService.GenerateCacheKey(1, req2);
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

            // Every refresh has different GUIDs for an adaptive-RSS request
            for (var i = 0; i < 10; i++)
            {
                Subject.Set(1, req, MakeVolatileResults(i), TimeSpan.FromMinutes(10));
            }

            var baseTtl = TimeSpan.FromSeconds(600);
            var adaptiveTtl = Subject.GetAdaptiveTtl(1, req, baseTtl);
            adaptiveTtl.Should().Be(baseTtl, "volatile results should not extend TTL");
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

        // Verifies count-seed + title fallback: two null-GUID sets with different
        // titles but identical count must produce distinct fingerprints and be
        // detected as volatile (score stays 0 → adaptive TTL returns base).
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
                    Releases = titles.Select(t => new ReleaseInfo { Title = t }).ToList<ReleaseInfo>()
                };
                Subject.Set(1, req, results, TimeSpan.FromMinutes(10));
            }

            var baseTtl = TimeSpan.FromSeconds(600);
            Subject.GetAdaptiveTtl(1, req, baseTtl)
                .Should().Be(baseTtl, "alternating null-GUID sets with distinct titles should be volatile");
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
