using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.IndexerSearch
{
    /// <summary>
    /// Caches Newznab API search results to reduce redundant upstream indexer requests
    /// when multiple *arr apps poll the same indexer. Fork-only feature.
    ///
    /// Three-tier TTL: per-request cachetime -> per-indexer CacheTtlMinutes -> global default.
    /// cachetime=0 bypasses cache entirely. DeduplicateAsync prevents thundering herd on
    /// cold/expired cache by serializing concurrent identical requests -- callers must
    /// re-check cache inside the factory (double-check locking pattern).
    /// </summary>
    public interface INewznabResultsCacheService
    {
        /// <summary>
        /// Returns cached results plus validated metadata using a canonical identity, or null on miss.
        /// Updates hit/miss stats only for adaptive-RSS queries.
        /// </summary>
        CachedEntry FindWithMetadata(CanonicalCacheIdentity identity, NewznabRequest request);

        /// <summary>
        /// Returns cached results for post-dedup cache re-check using a canonical identity. Does not
        /// update hit/miss stats because the initial lookup was already recorded by FindWithMetadata
        /// on the fast path.
        /// </summary>
        NewznabResults FindForRecheck(CanonicalCacheIdentity identity);

        /// <summary>
        /// Returns the indexer's current generation number, which is 0 for an indexer that was
        /// never invalidated, and retains no state. Capture it before reading the indexer
        /// settings and fetching results, and pass it to Set.
        /// </summary>
        long GetIndexerGeneration(int indexerId);

        /// <summary>
        /// Stores results with the given TTL. Releases list is frozen via ReadOnlyCollection.
        /// Stability tracking scope is derived from the request to keep adaptive TTL and stats aligned.
        /// Publishes only when the indexer's generation still equals the captured one; otherwise
        /// changes no cache state and returns false.
        /// </summary>
        bool Set(CanonicalCacheIdentity identity, NewznabRequest request, NewznabResults results, TimeSpan ttl, long indexerGeneration, string indexerName = null);

        /// <summary>Removes all cached entries and their tracking state for the given indexer.</summary>
        void InvalidateIndexer(int indexerId);

        /// <summary>Removes all cached entries and tracking state across all indexers.</summary>
        void Clear();

        /// <summary>
        /// Resolves effective TTL. Returns null if caching should be bypassed (cachetime=0).
        /// Priority: per-request -> per-indexer -> global default. Floor: global minimum.
        /// </summary>
        TimeSpan? ResolveTtl(int indexerId, int? requestCacheTimeSecs, int? indexerCacheTtlMins);

        /// <summary>
        /// Serializes concurrent requests for the same canonical cache key to prevent thundering herd.
        /// Factory MUST re-check cache before fetching (double-check pattern for dedup correctness).
        /// </summary>
        Task<T> DeduplicateAsync<T>(CanonicalCacheIdentity identity, Func<Task<T>> factory);

        /// <summary>
        /// Returns an adaptive TTL based on per-key result stability for RSS-like queries.
        /// Caller should only invoke this for RSS-like queries (no content-narrowing params).
        /// Returns baseTtl unchanged if insufficient stability data exists. Never returns less
        /// than baseTtl; an extension stops at 30 minutes in total.
        /// </summary>
        TimeSpan GetAdaptiveTtl(CanonicalCacheIdentity identity, TimeSpan baseTtl);

        /// <summary>Returns the active bypass suppression window for a canonical key, if any.</summary>
        DateTime? GetBypassSuppressedUntilUtc(CanonicalCacheIdentity identity);

        /// <summary>Records a 30-second bypass suppression window for a canonical key.</summary>
        bool RecordBypassFailure(CanonicalCacheIdentity identity, DateTime failedAtUtc);
    }

    public class NewznabResultsCacheService : INewznabResultsCacheService,
        IHandle<ProviderUpdatedEvent<IIndexer>>,
        IHandle<ProviderDeletedEvent<IIndexer>>,
        IHandle<ProviderBulkUpdatedEvent<IIndexer>>,
        IHandle<ProviderBulkDeletedEvent<IIndexer>>
    {
        // Unit Separator (U+001F) joins title and size in the stability identity of a release
        // without a GUID. A size never contains it, so the last delimiter splits the identity
        // unambiguously. Canonical cache keys are built, and their values escaped, by
        // NewznabCacheIdentityResolver.
        private const char KeyDelimiter = '\x1F';

        // Threshold for opportunistic key pruning in Set()
        private const int KeyPruneThreshold = 50;

        // Adaptive TTL: max multiplier for stable RSS queries (4.0x = up to 4x base TTL)
        private const double MaxAdaptiveMultiplier = 4.0;

        // Adaptive TTL: the extension stops at this many seconds in total, regardless of
        // multiplier. 30 min limits worst-case delay for morning-flood scenarios (quiet night
        // builds high stability, then new releases arrive before cache expires). A base TTL
        // at or above the ceiling is returned unchanged: adaptive caching never shortens it.
        private const int MaxAdaptiveTtlSecs = 1800;

        private static readonly TimeSpan StatsLogInterval = TimeSpan.FromMinutes(5);

        // Max time to retain stability trackers/fingerprints after their cache entry
        // expires. Bounds memory for abandoned RSS keys on low-volume indexers.
        // 1 hour ~ 2-6x typical RSS poll interval, generous for transient gaps.
        private static readonly TimeSpan StabilityRetentionWindow = TimeSpan.FromHours(1);

        private readonly ICached<CachedEntry> _cache;
        private readonly IConfigService _configService;

        // Tracks cache keys per indexer for targeted invalidation (indexerId -> set of cache keys)
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _indexerKeys;

        // Per-indexer generation numbers. InvalidateIndexer advances a number, creating it on the
        // first invalidation; an indexer without an entry reads as 0. Set publishes only when
        // the number still equals the one captured before the fetch. The numbers are never
        // removed. The gate covers the generation check and every state write of a publication,
        // and the number advance and entry removal of an invalidation, so a check-then-set race
        // cannot republish results fetched before an invalidation.
        private readonly Dictionary<int, long> _indexerGenerations;
        private readonly object _generationGate = new();

        // Per-key locks for DeduplicateAsync. An entry exists only while a caller holds or
        // awaits it; leases are counted and entries removed only under _keyLocksGate.
        private readonly Dictionary<string, KeyLock> _keyLocks;
        private readonly object _keyLocksGate = new();
        private readonly ConcurrentDictionary<int, IndexerCacheStats> _indexerStats;
        private readonly ConcurrentDictionary<string, HashSet<string>> _previousGuidSets;
        private readonly ConcurrentDictionary<string, IndexerStabilityTracker> _stabilityTrackers;
        private readonly ConcurrentDictionary<string, DateTime> _bypassSuppressedUntilUtc;
        private readonly ConcurrentDictionary<int, string> _indexerNames;
        private readonly object _logLock = new();
        private readonly Logger _logger;
        private long _globalHits;
        private long _globalMisses;
        private DateTime _lastGlobalLogTime;

        public NewznabResultsCacheService(ICacheManager cacheManager, IConfigService configService, Logger logger)
        {
            _cache = cacheManager.GetCache<CachedEntry>(GetType(), "NewznabResults");
            _configService = configService;
            _indexerKeys = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();
            _indexerGenerations = new Dictionary<int, long>();
            _keyLocks = new Dictionary<string, KeyLock>();
            _indexerStats = new ConcurrentDictionary<int, IndexerCacheStats>();
            _previousGuidSets = new ConcurrentDictionary<string, HashSet<string>>();
            _stabilityTrackers = new ConcurrentDictionary<string, IndexerStabilityTracker>();
            _bypassSuppressedUntilUtc = new ConcurrentDictionary<string, DateTime>();
            _indexerNames = new ConcurrentDictionary<int, string>();
            _lastGlobalLogTime = DateTime.UtcNow;
            _logger = logger;
        }

        public CachedEntry FindWithMetadata(CanonicalCacheIdentity identity, NewznabRequest request)
        {
            return FindInternal(identity.IndexerId, identity.Key, recordStats: true, NewznabCacheQueryPolicy.UsesAdaptiveRssCaching(request));
        }

        public NewznabResults FindForRecheck(CanonicalCacheIdentity identity)
        {
            return FindInternal(identity.IndexerId, identity.Key, recordStats: false, usesAdaptiveRssCaching: false)?.Results;
        }

        private CachedEntry FindInternal(int indexerId, string key, bool recordStats, bool usesAdaptiveRssCaching)
        {
            var entry = _cache.Find(key);
            if (entry != null)
            {
                RecordCacheHit(indexerId, recordStats, usesAdaptiveRssCaching);

                return entry;
            }

            RecordCacheMiss(indexerId, recordStats, usesAdaptiveRssCaching);

            return null;
        }

        public long GetIndexerGeneration(int indexerId)
        {
            lock (_generationGate)
            {
                // A missing entry reads as generation 0, which Set and InvalidateIndexer also assume,
                // so a request for an unknown indexer id retains no state.
                _indexerGenerations.TryGetValue(indexerId, out var generation);

                return generation;
            }
        }

        public bool Set(CanonicalCacheIdentity identity, NewznabRequest request, NewznabResults results, TimeSpan ttl, long indexerGeneration, string indexerName = null)
        {
            lock (_generationGate)
            {
                _indexerGenerations.TryGetValue(identity.IndexerId, out var currentGeneration);

                if (currentGeneration != indexerGeneration)
                {
                    _logger.Debug(
                        "Discarded results for indexer {0}: fetched under generation {1}, current generation {2}",
                        GetIndexerLabel(identity.IndexerId),
                        indexerGeneration,
                        currentGeneration);

                    return false;
                }

                SetInternal(identity.IndexerId, identity.Key, request, results, ttl, indexerName);

                return true;
            }
        }

        private void SetInternal(int indexerId, string key, NewznabRequest request, NewznabResults results, TimeSpan ttl, string indexerName)
        {
            if (indexerName != null)
            {
                _indexerNames[indexerId] = indexerName;
            }

            // Track result stability only for queries that participate in adaptive TTL.
            // Explicit cachetime overrides and content-specific searches are excluded
            // because they do not reuse adaptive stability data.
            if (NewznabCacheQueryPolicy.UsesAdaptiveRssCaching(request))
            {
                if (TryBuildIdentitySet(results.Releases, out var currentIdentitySet, out var shouldSkipAdaptiveUpdate))
                {
                    var unchanged = false;

                    _previousGuidSets.AddOrUpdate(
                        key,
                        currentIdentitySet,
                        (_, previousIdentitySet) =>
                        {
                            unchanged = ComputeJaccardSimilarity(previousIdentitySet, currentIdentitySet) >= 0.9;
                            return currentIdentitySet;
                        });

                    var tracker = _stabilityTrackers.GetOrAdd(key, _ => new IndexerStabilityTracker());
                    tracker.RecordRefresh(unchanged);
                }
                else if (shouldSkipAdaptiveUpdate)
                {
                    _logger.Warn(
                        "Skipping adaptive cache identity update for indexer {0}, key {1}: all {2} releases lack guid/title/size",
                        indexerId,
                        key,
                        results.Releases?.Count ?? 0);
                }
            }

            // Freeze the releases list to prevent accidental mutation of cached data
            if (results.Releases != null)
            {
                results.Releases = new ReadOnlyCollection<ReleaseInfo>(results.Releases);
            }

            WriteCacheEntry(key, results, ttl);

            var keys = _indexerKeys.GetOrAdd(indexerId, _ => new ConcurrentDictionary<string, byte>());
            keys.TryAdd(key, 0);

            // Opportunistically prune expired keys for this indexer to prevent unbounded
            // growth from unique title searches (e.g. q=Some.Movie.2024.1080p.BluRay).
            // Only runs when there are enough tracked keys to warrant the scan.
            if (keys.Count > KeyPruneThreshold)
            {
                PruneExpiredKeys(indexerId, keys, preserveTracking: true);
            }

            _logger.Debug("Cached {0} releases for indexer {1}, TTL {2}s", results.Releases?.Count ?? 0, GetIndexerLabel(indexerId), ttl.TotalSeconds);
        }

        public void InvalidateIndexer(int indexerId)
        {
            lock (_generationGate)
            {
                // Results fetched under the previous generation are rejected by Set from here on.
                _indexerGenerations.TryGetValue(indexerId, out var generation);
                _indexerGenerations[indexerId] = generation + 1;

                if (_indexerKeys.TryRemove(indexerId, out var keys))
                {
                    foreach (var key in keys.Keys)
                    {
                        _cache.Remove(key);
                        CleanupKeyTracking(key);
                    }

                    _logger.Debug("Invalidated {0} cached entries for indexer {1}", keys.Count, GetIndexerLabel(indexerId));
                }

                // Clean up per-indexer tracking data
                _indexerStats.TryRemove(indexerId, out _);
                _indexerNames.TryRemove(indexerId, out _);
            }
        }

        public void Clear()
        {
            _cache.Clear();
            _indexerKeys.Clear();
            _previousGuidSets.Clear();
            _stabilityTrackers.Clear();
            _bypassSuppressedUntilUtc.Clear();
            _indexerStats.Clear();
            _indexerNames.Clear();

            _logger.Debug("Cleared all cached results");
        }

        /// <summary>
        /// Resolves effective cache TTL from three-tier config.
        /// Returns null if caching should be bypassed (cachetime=0).
        /// Priority: per-request -> per-indexer -> global default. Floor: global minimum.
        /// For RSS-like queries, caller should also call GetAdaptiveTtl to extend based on stability.
        /// </summary>
        public TimeSpan? ResolveTtl(int indexerId, int? requestCacheTimeSecs, int? indexerCacheTtlMins)
        {
            // cachetime=0 bypasses cache entirely
            if (requestCacheTimeSecs is 0)
            {
                return null;
            }

            var globalDefaultMins = _configService.CacheDefaultTtlMinutes;
            var globalMinMins = _configService.CacheMinimumTtlMinutes;

            // Minutes are converted in long: a stored value above the validated range keeps its
            // meaning instead of wrapping around int.
            long effectiveSecs;

            if (requestCacheTimeSecs.HasValue)
            {
                effectiveSecs = requestCacheTimeSecs.Value;
            }
            else if (indexerCacheTtlMins.HasValue)
            {
                effectiveSecs = indexerCacheTtlMins.Value * 60L;
            }
            else
            {
                effectiveSecs = globalDefaultMins * 60L;
            }

            // Enforce global minimum floor
            var floorSecs = globalMinMins * 60L;
            effectiveSecs = Math.Max(effectiveSecs, floorSecs);

            // TODO: Future enhancement -- query budget preservation. When approaching the
            // configured query limit, auto-extend TTLs to preserve budget for new queries.
            // Would need IIndexerLimitService.GetQueryBudgetUsageRatio() or similar.
            return TimeSpan.FromSeconds(effectiveSecs);
        }

        /// <summary>
        /// Returns an adaptive TTL based on per-key result stability. Only meaningful
        /// for RSS-like queries where results stabilize over time. Returns baseTtl
        /// unchanged if insufficient observations or no stability data exists.
        /// The result is max(baseTtl, min(baseTtl x multiplier, MaxAdaptiveTtlSecs)): never below
        /// baseTtl, and an extension stops at MaxAdaptiveTtlSecs in total.
        /// </summary>
        public TimeSpan GetAdaptiveTtl(CanonicalCacheIdentity identity, TimeSpan baseTtl)
        {
            return GetAdaptiveTtl(identity.IndexerId, identity.Key, baseTtl);
        }

        public DateTime? GetBypassSuppressedUntilUtc(CanonicalCacheIdentity identity)
        {
            if (!_bypassSuppressedUntilUtc.TryGetValue(identity.Key, out var suppressedUntilUtc))
            {
                return null;
            }

            if (suppressedUntilUtc <= DateTime.UtcNow)
            {
                return null;
            }

            return suppressedUntilUtc;
        }

        public bool RecordBypassFailure(CanonicalCacheIdentity identity, DateTime failedAtUtc)
        {
            if (failedAtUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Bypass suppression timestamps must be UTC.", nameof(failedAtUtc));
            }

            try
            {
                _bypassSuppressedUntilUtc[identity.Key] = failedAtUtc + TimeSpan.FromSeconds(30);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to record bypass suppression for cache key {0}", identity.Key);
                return false;
            }
        }

        private TimeSpan GetAdaptiveTtl(int indexerId, string key, TimeSpan baseTtl)
        {
            if (_stabilityTrackers.TryGetValue(key, out var tracker))
            {
                var score = tracker.GetStabilityScore();
                if (score > 0)
                {
                    var multiplier = 1.0 + (score * (MaxAdaptiveMultiplier - 1.0));

                    // max(base, min(base x multiplier, ceiling)), computed in double so no TimeSpan
                    // overflows an integer.
                    var extendedSecs = Math.Min(baseTtl.TotalSeconds * multiplier, MaxAdaptiveTtlSecs);
                    var adaptiveTtl = extendedSecs > baseTtl.TotalSeconds ? TimeSpan.FromSeconds(extendedSecs) : baseTtl;

                    _logger.Debug(
                        "Adaptive TTL for indexer {0}: stability {1:F2}, {2:F1}x, {3:F0}s -> {4:F0}s",
                        GetIndexerLabel(indexerId),
                        score,
                        multiplier,
                        baseTtl.TotalSeconds,
                        adaptiveTtl.TotalSeconds);

                    return adaptiveTtl;
                }
            }

            return baseTtl;
        }

        /// <summary>
        /// Serializes concurrent requests for the same cache key to prevent
        /// thundering herd. Uses a per-key lease-counted lock with double-check pattern.
        /// </summary>
        public async Task<T> DeduplicateAsync<T>(CanonicalCacheIdentity identity, Func<Task<T>> factory)
        {
            return await DeduplicateAsync(identity.Key, factory);
        }

        private async Task<T> DeduplicateAsync<T>(string key, Func<Task<T>> factory)
        {
            var keyLock = AcquireLease(key);

            try
            {
                await keyLock.Semaphore.WaitAsync();

                try
                {
                    return await factory();
                }
                finally
                {
                    keyLock.Semaphore.Release();
                }
            }
            finally
            {
                ReleaseLease(key, keyLock);
            }
        }

        // A lease counts one holder or waiter. Taking a lease, releasing it, and removing the
        // entry at zero leases all happen under _keyLocksGate, so an entry is never retired
        // while a caller holds or awaits it, and the table holds only keys with callers in flight.
        private KeyLock AcquireLease(string key)
        {
            lock (_keyLocksGate)
            {
                if (!_keyLocks.TryGetValue(key, out var keyLock))
                {
                    keyLock = new KeyLock();
                    _keyLocks[key] = keyLock;
                }

                keyLock.Leases++;

                return keyLock;
            }
        }

        private void ReleaseLease(string key, KeyLock keyLock)
        {
            lock (_keyLocksGate)
            {
                if (--keyLock.Leases == 0)
                {
                    _keyLocks.Remove(key);
                }
            }
        }

        private string GetIndexerLabel(int indexerId) =>
            _indexerNames.TryGetValue(indexerId, out var name) ? $"{name} ({indexerId})" : indexerId.ToString();

        private void CleanupKeyTracking(string key, bool preserveTracking = false)
        {
            if (!preserveTracking)
            {
                _previousGuidSets.TryRemove(key, out _);
                _stabilityTrackers.TryRemove(key, out _);
            }

            _bypassSuppressedUntilUtc.TryRemove(key, out _);
        }

        private void PruneKey(int indexerId, string key)
        {
            if (_indexerKeys.TryGetValue(indexerId, out var keys))
            {
                keys.TryRemove(key, out _);
            }

            CleanupKeyTracking(key);
        }

        /// <summary>
        /// Scans tracked keys for an indexer and removes any whose cache entries have expired.
        /// Called opportunistically from Set() to amortize cleanup without a background timer.
        /// </summary>
        private void PruneExpiredKeys(int indexerId, ConcurrentDictionary<string, byte> keys, bool preserveTracking)
        {
            var pruned = 0;

            foreach (var trackedKey in keys.Keys)
            {
                if (_cache.Find(trackedKey) == null)
                {
                    // _cache.Find may inline-evict an expired entry before this sweep reaps the
                    // suppression, GUID-set, and stability state bound to its key.
                    // Preserve recent stability data for RSS-like keys across cache expiry.
                    // Keep key in _indexerKeys so future prune cycles and InvalidateIndexer
                    // can find and clean it up. Stale trackers are cleaned up normally.
                    var keepTracking = preserveTracking
                        && _stabilityTrackers.TryGetValue(trackedKey, out var tracker)
                        && !tracker.IsStale(StabilityRetentionWindow);

                    CleanupKeyTracking(trackedKey, preserveTracking: keepTracking);

                    if (!keepTracking)
                    {
                        keys.TryRemove(trackedKey, out _);
                        pruned++;
                    }
                }
            }

            if (pruned > 0)
            {
                _logger.Debug("Pruned {0} expired keys for indexer {1}, {2} remaining", pruned, GetIndexerLabel(indexerId), keys.Count);
            }
        }

        private void RecordCacheHit(int indexerId, bool recordStats, bool usesAdaptiveRssCaching)
        {
            if (!recordStats || !usesAdaptiveRssCaching)
            {
                return;
            }

            var stats = _indexerStats.GetOrAdd(indexerId, _ => new IndexerCacheStats());
            Interlocked.Increment(ref _globalHits);
            stats.IncrementHits();
            LogStatsIfDue(indexerId, stats);
        }

        private void RecordCacheMiss(int indexerId, bool recordStats, bool usesAdaptiveRssCaching)
        {
            if (recordStats && usesAdaptiveRssCaching)
            {
                var missStats = _indexerStats.GetOrAdd(indexerId, _ => new IndexerCacheStats());
                Interlocked.Increment(ref _globalMisses);
                missStats.IncrementMisses();
                LogStatsIfDue(indexerId, missStats);
            }

            if (!recordStats)
            {
                return;
            }

            // Probabilistic sweep of expired keys for this indexer. Fires on every miss
            // when count exceeds threshold, or ~2% of misses otherwise. Ensures abandoned
            // RSS keys are eventually reclaimed even on low-volume indexers.
            if (_indexerKeys.TryGetValue(indexerId, out var idxKeys)
                && (idxKeys.Count > KeyPruneThreshold || Random.Shared.Next(50) == 0))
            {
                PruneExpiredKeys(indexerId, idxKeys, preserveTracking: true);
            }
        }

        private void WriteCacheEntry(string key, NewznabResults results, TimeSpan ttl)
        {
            // Non-positive TTL is a caller bug; CachedEntryMetadata rejects it before anything is stored.
            var metadata = new CachedEntryMetadata(DateTime.UtcNow, (long)Math.Ceiling(ttl.TotalSeconds));

            // Results and metadata are published as one record, so a reader never pairs
            // results with the metadata of another write.
            _cache.Set(key, new CachedEntry(results, metadata), ttl);
        }

        private (int TotalReleases, int StableKeys, double AvgStability) ComputeCacheStats(IEnumerable<string> keys)
        {
            var totalReleases = 0;
            var stableKeys = 0;
            var totalStability = 0.0;

            foreach (var key in keys)
            {
                var cached = _cache.Find(key);
                if (cached?.Results.Releases != null)
                {
                    totalReleases += cached.Results.Releases.Count;
                }

                if (_stabilityTrackers.TryGetValue(key, out var st))
                {
                    var s = st.GetStabilityScore();
                    if (s > 0)
                    {
                        stableKeys++;
                        totalStability += s;
                    }
                }
            }

            return (totalReleases, stableKeys, stableKeys > 0 ? totalStability / stableKeys : 0);
        }

        private void LogStatsIfDue(int indexerId, IndexerCacheStats stats)
        {
            var now = DateTime.UtcNow;

            // Per-indexer stats (atomic check-and-reset to avoid TOCTOU race)
            var counts = stats.TryResetAndGetCounts(now, StatsLogInterval);
            if (counts.HasValue)
            {
                var (hits, misses) = counts.Value;
                if (hits > 0 || misses > 0)
                {
                    var entryCount = 0;
                    IEnumerable<string> indexerCacheKeys = Array.Empty<string>();

                    if (_indexerKeys.TryGetValue(indexerId, out var k))
                    {
                        entryCount = k.Count;
                        indexerCacheKeys = k.Keys;
                    }

                    var (totalReleases, stableKeys, avgStability) = ComputeCacheStats(indexerCacheKeys);

                    _logger.Info(
                        "Cache stats for indexer {0}: {1} hits, {2} misses ({3:F0}% hit rate), {4} entries ({5} releases), {6} stable keys (avg {7:F2})",
                        GetIndexerLabel(indexerId),
                        hits,
                        misses,
                        hits + misses > 0 ? (double)hits / (hits + misses) * 100 : 0,
                        entryCount,
                        totalReleases,
                        stableKeys,
                        avgStability);
                }
            }

            // Global stats (checked under lock to avoid duplicate logs)
            lock (_logLock)
            {
                if (now - _lastGlobalLogTime >= StatsLogInterval)
                {
                    var globalHits = Interlocked.Exchange(ref _globalHits, 0);
                    var globalMisses = Interlocked.Exchange(ref _globalMisses, 0);
                    _lastGlobalLogTime = now;

                    if (globalHits > 0 || globalMisses > 0)
                    {
                        var (globalReleases, globalStableKeys, globalAvgStability) =
                            ComputeCacheStats(_indexerKeys.SelectMany(e => e.Value.Keys));

                        _logger.Info(
                            "Cache stats (global): {0} hits, {1} misses ({2:F0}% hit rate), {3} entries ({4} releases), {5} stable keys (avg {6:F2})",
                            globalHits,
                            globalMisses,
                            globalHits + globalMisses > 0 ? (double)globalHits / (globalHits + globalMisses) * 100 : 0,
                            _cache.Count,
                            globalReleases,
                            globalStableKeys,
                            globalAvgStability);
                    }
                }
            }
        }

        public void Handle(ProviderUpdatedEvent<IIndexer> message)
        {
            InvalidateIndexer(message.Definition.Id);
        }

        public void Handle(ProviderDeletedEvent<IIndexer> message)
        {
            InvalidateIndexer(message.ProviderId);
        }

        public void Handle(ProviderBulkUpdatedEvent<IIndexer> message)
        {
            foreach (var definition in message.Definitions)
            {
                InvalidateIndexer(definition.Id);
            }
        }

        public void Handle(ProviderBulkDeletedEvent<IIndexer> message)
        {
            foreach (var providerId in message.ProviderIds)
            {
                InvalidateIndexer(providerId);
            }
        }

        private static bool TryBuildIdentitySet(IList<ReleaseInfo> releases, out HashSet<string> identitySet, out bool shouldSkipAdaptiveUpdate)
        {
            if (releases == null || releases.Count == 0)
            {
                identitySet = new HashSet<string>(StringComparer.Ordinal);
                shouldSkipAdaptiveUpdate = false;
                return true;
            }

            var guidIdentitySet = releases
                .Select(release => release.Guid)
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .ToHashSet(StringComparer.Ordinal);

            if (guidIdentitySet.Count > 0)
            {
                identitySet = guidIdentitySet;
                shouldSkipAdaptiveUpdate = false;
                return true;
            }

            identitySet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var release in releases)
            {
                var normalizedTitle = string.IsNullOrWhiteSpace(release.Title) ? null : release.Title;

                if (normalizedTitle == null && !release.Size.HasValue)
                {
                    continue;
                }

                identitySet.Add($"{normalizedTitle ?? string.Empty}{KeyDelimiter}{release.Size?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}");
            }

            shouldSkipAdaptiveUpdate = identitySet.Count == 0;
            return !shouldSkipAdaptiveUpdate;
        }

        private static double ComputeJaccardSimilarity(HashSet<string> previousIdentitySet, HashSet<string> currentIdentitySet)
        {
            if (previousIdentitySet.Count == 0 && currentIdentitySet.Count == 0)
            {
                return 1.0;
            }

            if (previousIdentitySet.Count == 0 || currentIdentitySet.Count == 0)
            {
                return 0.0;
            }

            var intersectionCount = previousIdentitySet.Count(identity => currentIdentitySet.Contains(identity));
            var unionCount = previousIdentitySet.Count + currentIdentitySet.Count - intersectionCount;

            return unionCount == 0 ? 1.0 : (double)intersectionCount / unionCount;
        }

        /// <summary>
        /// One per-key dedup lock. Leases counts holders and waiters and is guarded by _keyLocksGate.
        /// </summary>
        private sealed class KeyLock
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public int Leases { get; set; }
        }

        /// <summary>
        /// Tracks per-cache-key result stability using a decaying counter pair.
        /// Stability score (0.0-1.0) indicates how often cached results match upstream
        /// on refresh. Higher score -> results rarely change -> safe to extend cache TTL.
        /// </summary>
        private class IndexerStabilityTracker
        {
            // With adaptive TTL reducing upstream call frequency to ~2-3/hr per key,
            // first decay at 20 takes 7-10 hours -- too slow for day-night release cycles.
            // 10 gives ~3-5 hr half-life: stable enough to extend TTL, fast enough to adapt.
            private const int DecayThreshold = 10;

            // Minimum stability observations before applying adaptive multiplier
            private const int MinStabilityObservations = 3;
            private long _unchangedRefreshes;
            private long _totalRefreshes;
            private long _lastUpdatedTicks = DateTime.UtcNow.Ticks;

            /// <summary>
            /// Returns true if the tracker has not been updated within the given window,
            /// indicating the key is likely abandoned and tracking data can be reclaimed.
            /// </summary>
            public bool IsStale(TimeSpan maxAge) =>
                DateTime.UtcNow.Ticks - Volatile.Read(ref _lastUpdatedTicks) > maxAge.Ticks;

            public void RecordRefresh(bool unchanged)
            {
                Volatile.Write(ref _lastUpdatedTicks, DateTime.UtcNow.Ticks);
                if (unchanged)
                {
                    Interlocked.Increment(ref _unchangedRefreshes);
                }

                var total = Interlocked.Increment(ref _totalRefreshes);

                // Decay counters to weight toward recent observations
                if (total >= DecayThreshold)
                {
                    // Not perfectly atomic across both fields, but close enough
                    // for a heuristic that feeds into a soft TTL multiplier.
                    Interlocked.Exchange(ref _unchangedRefreshes,
                        Volatile.Read(ref _unchangedRefreshes) / 2);
                    Interlocked.Exchange(ref _totalRefreshes,
                        Volatile.Read(ref _totalRefreshes) / 2);
                }
            }

            public double GetStabilityScore()
            {
                var total = Volatile.Read(ref _totalRefreshes);
                if (total < MinStabilityObservations)
                {
                    return 0;
                }

                // Clamp: non-atomic decay can transiently make unchanged > total
                return Math.Min((double)Volatile.Read(ref _unchangedRefreshes) / total, 1.0);
            }
        }

        /// <summary>
        /// Thread-safe per-indexer cache statistics with periodic reset.
        /// </summary>
        private class IndexerCacheStats
        {
            private readonly object _lock = new();
            private long _hits;
            private long _misses;
            private DateTime _lastLogTime = DateTime.UtcNow;

            public void IncrementHits() => Interlocked.Increment(ref _hits);
            public void IncrementMisses() => Interlocked.Increment(ref _misses);

            /// <summary>
            /// Atomically checks if logging is due and resets counters if so.
            /// Returns null if not due, preventing TOCTOU race of separate check-then-reset.
            /// </summary>
            public (long Hits, long Misses)? TryResetAndGetCounts(DateTime now, TimeSpan interval)
            {
                lock (_lock)
                {
                    if (now - _lastLogTime < interval)
                    {
                        return null;
                    }

                    var hits = Interlocked.Exchange(ref _hits, 0);
                    var misses = Interlocked.Exchange(ref _misses, 0);
                    _lastLogTime = now;
                    return (hits, misses);
                }
            }
        }
    }
}
