using System;
using System.Linq;
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

        // ── Cache CRUD ─────────────────────────────────────────────────────────
        [Test]
        public void Find_should_return_null_on_cache_miss()
        {
            var result = Subject.Find(1, MakeRequest(q: "test"));
            result.Should().BeNull();
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
            var ttl = Subject.ResolveTtl(requestCacheTimeSecs: 0, indexerCacheTtlMins: null);
            ttl.Should().BeNull("cachetime=0 is the bypass sentinel");
        }

        [Test]
        public void ResolveTtl_should_use_request_cachetime_when_provided()
        {
            // 900s > 300s floor → no clamping
            var ttl = Subject.ResolveTtl(requestCacheTimeSecs: 900, indexerCacheTtlMins: 20);
            ttl.Should().Be(TimeSpan.FromSeconds(900), "per-request takes priority over per-indexer");
        }

        [Test]
        public void ResolveTtl_should_use_indexer_ttl_when_no_request_override()
        {
            // 15 min = 900s > 300s floor
            var ttl = Subject.ResolveTtl(requestCacheTimeSecs: null, indexerCacheTtlMins: 15);
            ttl.Should().Be(TimeSpan.FromSeconds(900));
        }

        [Test]
        public void ResolveTtl_should_use_global_default_when_no_overrides()
        {
            // global default = 10 min = 600s > 300s floor
            var ttl = Subject.ResolveTtl(requestCacheTimeSecs: null, indexerCacheTtlMins: null);
            ttl.Should().Be(TimeSpan.FromSeconds(600));
        }

        [Test]
        public void ResolveTtl_should_enforce_global_minimum_floor()
        {
            // 60s < 300s floor → clamped to 300s
            var ttl = Subject.ResolveTtl(requestCacheTimeSecs: 60, indexerCacheTtlMins: null);
            ttl.Should().Be(TimeSpan.FromSeconds(300), "global minimum floor of 5 minutes applies");
        }

        [Test]
        public void ResolveTtl_should_clamp_negative_cachetime_to_minimum_floor()
        {
            // -1s < 300s floor → clamped to 300s (same as any below-floor value)
            var ttl = Subject.ResolveTtl(requestCacheTimeSecs: -1, indexerCacheTtlMins: null);
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
    }
}
