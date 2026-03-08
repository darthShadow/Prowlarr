using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
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
    /// Three-tier TTL: per-request cachetime → per-indexer CacheTtlMinutes → global default.
    /// cachetime=0 bypasses cache entirely. DeduplicateAsync prevents thundering herd on
    /// cold/expired cache by serializing concurrent identical requests — callers must
    /// re-check cache inside the factory (double-check locking pattern).
    /// </summary>
    public interface INewznabResultsCacheService
    {
        /// <summary>Returns cached results or null on miss. Updates hit/miss stats.</summary>
        NewznabResults Find(int indexerId, NewznabRequest request);

        /// <summary>
        /// Returns cached results for post-dedup cache re-check. Does not update hit/miss
        /// stats because the initial lookup was already recorded by Find on the fast path.
        /// </summary>
        NewznabResults FindForRecheck(int indexerId, NewznabRequest request);

        /// <summary>
        /// Stores results with the given TTL. Releases list is frozen via ReadOnlyCollection.
        /// Pass isRssLike=true for RSS-style queries to enable stability tracking for adaptive TTL.
        /// </summary>
        void Set(int indexerId, NewznabRequest request, NewznabResults results, TimeSpan ttl, string indexerName = null, bool isRssLike = false);

        /// <summary>Removes all cached entries and dedup locks for the given indexer.</summary>
        void InvalidateIndexer(int indexerId);

        /// <summary>Removes all cached entries and dedup locks across all indexers.</summary>
        void Clear();

        /// <summary>
        /// Resolves effective TTL. Returns null if caching should be bypassed (cachetime=0).
        /// Priority: per-request → per-indexer → global default. Floor: global minimum.
        /// </summary>
        TimeSpan? ResolveTtl(int indexerId, int? requestCacheTimeSecs, int? indexerCacheTtlMins);

        /// <summary>
        /// Serializes concurrent requests for the same cache key to prevent thundering herd.
        /// Factory MUST re-check cache before fetching (double-check pattern for dedup correctness).
        /// </summary>
        Task<T> DeduplicateAsync<T>(int indexerId, NewznabRequest request, Func<Task<T>> factory);

        /// <summary>
        /// Returns an adaptive TTL based on per-key result stability for RSS-like queries.
        /// Caller should only invoke this for RSS-like queries (no content-narrowing params).
        /// Returns baseTtl unchanged if insufficient stability data exists.
        /// </summary>
        TimeSpan GetAdaptiveTtl(int indexerId, NewznabRequest request, TimeSpan baseTtl);
    }

    public class NewznabResultsCacheService : INewznabResultsCacheService,
        IHandle<ProviderUpdatedEvent<IIndexer>>,
        IHandle<ProviderDeletedEvent<IIndexer>>,
        IHandle<ProviderBulkUpdatedEvent<IIndexer>>,
        IHandle<ProviderBulkDeletedEvent<IIndexer>>
    {
        // Hard cap: if probabilistic cleanup falls behind (e.g., sustained burst of
        // one-off title searches), full scan fires as safety net. ~27 MB at 544 bytes/entry.
        private const int MaxDedupLocks = 50_000;

        // Unit Separator (ASCII 31) as field delimiter — cannot appear in URL-decoded query
        // params, preventing cache key collisions from values containing '&' or '=' characters.
        private const char KeyDelimiter = '\x1F';

        // Threshold for opportunistic key pruning in Set()
        private const int KeyPruneThreshold = 50;

        // Adaptive TTL: max multiplier for stable RSS queries (4.0x = up to 4x base TTL)
        private const double MaxAdaptiveMultiplier = 4.0;

        // Adaptive TTL: absolute ceiling regardless of multiplier.
        // 30 min limits worst-case delay for morning-flood scenarios (quiet night
        // builds high stability, then new releases arrive before cache expires).
        private const int MaxAdaptiveTtlSecs = 1800;

        private static readonly TimeSpan StatsLogInterval = TimeSpan.FromMinutes(5);

        // Max time to retain stability trackers/fingerprints after their cache entry
        // expires. Bounds memory for abandoned RSS keys on low-volume indexers.
        // 1 hour ≈ 2–6× typical RSS poll interval, generous for transient gaps.
        private static readonly TimeSpan StabilityRetentionWindow = TimeSpan.FromHours(1);

        private readonly ICached<NewznabResults> _cache;
        private readonly IConfigService _configService;

        // Tracks cache keys per indexer for targeted invalidation (indexerId → set of cache keys)
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _indexerKeys;

        // Per-key semaphores for DeduplicateAsync (not disposed on removal — see InvalidateIndexer)
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks;
        private readonly ConcurrentDictionary<int, IndexerCacheStats> _indexerStats;
        private readonly ConcurrentDictionary<string, int> _fingerprints;
        private readonly ConcurrentDictionary<string, IndexerStabilityTracker> _stabilityTrackers;
        private readonly ConcurrentDictionary<int, string> _indexerNames;
        private readonly object _logLock = new();
        private readonly Logger _logger;
        private long _globalHits;
        private long _globalMisses;
        private DateTime _lastGlobalLogTime;

        public NewznabResultsCacheService(ICacheManager cacheManager, IConfigService configService, Logger logger)
        {
            _cache = cacheManager.GetCache<NewznabResults>(GetType(), "NewznabResults");
            _configService = configService;
            _indexerKeys = new ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>();
            _keyLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
            _indexerStats = new ConcurrentDictionary<int, IndexerCacheStats>();
            _fingerprints = new ConcurrentDictionary<string, int>();
            _stabilityTrackers = new ConcurrentDictionary<string, IndexerStabilityTracker>();
            _indexerNames = new ConcurrentDictionary<int, string>();
            _lastGlobalLogTime = DateTime.UtcNow;
            _logger = logger;
        }

        public NewznabResults Find(int indexerId, NewznabRequest request)
        {
            return FindInternal(indexerId, request, recordStats: true);
        }

        public NewznabResults FindForRecheck(int indexerId, NewznabRequest request)
        {
            return FindInternal(indexerId, request, recordStats: false);
        }

        private NewznabResults FindInternal(int indexerId, NewznabRequest request, bool recordStats)
        {
            var key = GenerateCacheKey(indexerId, request);
            var result = _cache.Find(key);

            if (result != null)
            {
                if (recordStats)
                {
                    var stats = _indexerStats.GetOrAdd(indexerId, _ => new IndexerCacheStats());
                    Interlocked.Increment(ref _globalHits);
                    stats.IncrementHits();
                    LogStatsIfDue(indexerId, stats);
                }

                return result;
            }

            if (!recordStats)
            {
                return null;
            }

            var missStats = _indexerStats.GetOrAdd(indexerId, _ => new IndexerCacheStats());
            Interlocked.Increment(ref _globalMisses);
            missStats.IncrementMisses();
            LogStatsIfDue(indexerId, missStats);

            // Probabilistic sweep of expired keys for this indexer. Fires on every miss
            // when count exceeds threshold, or ~2% of misses otherwise. Ensures abandoned
            // RSS keys are eventually reclaimed even on low-volume indexers. Mirrors the
            // dedup-lock cleanup pattern (see PruneStaleLocks probabilistic trigger).
            if (_indexerKeys.TryGetValue(indexerId, out var idxKeys)
                && (idxKeys.Count > KeyPruneThreshold || Random.Shared.Next(50) == 0))
            {
                PruneExpiredKeys(indexerId, idxKeys);
            }

            return null;
        }

        public void Set(int indexerId, NewznabRequest request, NewznabResults results, TimeSpan ttl, string indexerName = null, bool isRssLike = false)
        {
            if (indexerName != null)
            {
                _indexerNames[indexerId] = indexerName;
            }

            var key = GenerateCacheKey(indexerId, request);

            // Track result stability for adaptive TTL only for RSS-like queries.
            // Non-RSS queries (title/ID searches) are one-off and never use GetAdaptiveTtl,
            // so tracking them wastes memory and would pollute stability stats.
            if (isRssLike)
            {
                var fingerprint = ComputeFingerprint(results.Releases);
                var unchanged = _fingerprints.TryGetValue(key, out var previous) && previous == fingerprint;
                _fingerprints[key] = fingerprint;
                var tracker = _stabilityTrackers.GetOrAdd(key, _ => new IndexerStabilityTracker());
                tracker.RecordRefresh(unchanged);
            }

            // Freeze the releases list to prevent accidental mutation of cached data
            if (results.Releases != null)
            {
                results.Releases = new ReadOnlyCollection<ReleaseInfo>(results.Releases);
            }

            _cache.Set(key, results, ttl);

            var keys = _indexerKeys.GetOrAdd(indexerId, _ => new ConcurrentDictionary<string, byte>());
            keys.TryAdd(key, 0);

            // Opportunistically prune expired keys for this indexer to prevent unbounded
            // growth from unique title searches (e.g. q=Some.Movie.2024.1080p.BluRay).
            // Only runs when there are enough tracked keys to warrant the scan.
            if (keys.Count > KeyPruneThreshold)
            {
                PruneExpiredKeys(indexerId, keys);
            }

            _logger.Debug("Cached {0} releases for indexer {1}, TTL {2}s", results.Releases?.Count ?? 0, GetIndexerLabel(indexerId), ttl.TotalSeconds);
        }

        public void InvalidateIndexer(int indexerId)
        {
            if (_indexerKeys.TryRemove(indexerId, out var keys))
            {
                // Safe to remove semaphores here: cache is being invalidated, so any
                // in-flight factory stores immediately-stale results. Brief dedup gap
                // during admin-triggered invalidation is acceptable.
                foreach (var key in keys.Keys)
                {
                    _cache.Remove(key);
                    _keyLocks.TryRemove(key, out _);
                    CleanupKeyTracking(key);
                }

                _logger.Debug("Invalidated {0} cached entries for indexer {1}", keys.Count, GetIndexerLabel(indexerId));
            }

            // Clean up per-indexer tracking data
            _indexerStats.TryRemove(indexerId, out _);
            _indexerNames.TryRemove(indexerId, out _);
        }

        public void Clear()
        {
            _cache.Clear();
            _indexerKeys.Clear();
            _keyLocks.Clear();
            _fingerprints.Clear();
            _stabilityTrackers.Clear();
            _indexerStats.Clear();
            _indexerNames.Clear();

            _logger.Debug("Cleared all cached results");
        }

        /// <summary>
        /// Resolves effective cache TTL from three-tier config.
        /// Returns null if caching should be bypassed (cachetime=0).
        /// Priority: per-request → per-indexer → global default. Floor: global minimum.
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

            int effectiveSecs;

            if (requestCacheTimeSecs.HasValue)
            {
                effectiveSecs = requestCacheTimeSecs.Value;
            }
            else if (indexerCacheTtlMins.HasValue)
            {
                effectiveSecs = indexerCacheTtlMins.Value * 60;
            }
            else
            {
                effectiveSecs = globalDefaultMins * 60;
            }

            // Enforce global minimum floor
            var floorSecs = globalMinMins * 60;
            effectiveSecs = Math.Max(effectiveSecs, floorSecs);

            // TODO: Future enhancement — query budget preservation. When approaching the
            // configured query limit, auto-extend TTLs to preserve budget for new queries.
            // Would need IIndexerLimitService.GetQueryBudgetUsageRatio() or similar.
            return TimeSpan.FromSeconds(effectiveSecs);
        }

        /// <summary>
        /// Returns an adaptive TTL based on per-key result stability. Only meaningful
        /// for RSS-like queries where results stabilize over time. Returns baseTtl
        /// unchanged if insufficient observations or no stability data exists.
        /// Capped at MaxAdaptiveTtlSecs.
        /// </summary>
        public TimeSpan GetAdaptiveTtl(int indexerId, NewznabRequest request, TimeSpan baseTtl)
        {
            var key = GenerateCacheKey(indexerId, request);

            if (_stabilityTrackers.TryGetValue(key, out var tracker))
            {
                var score = tracker.GetStabilityScore();
                if (score > 0)
                {
                    var multiplier = 1.0 + (score * (MaxAdaptiveMultiplier - 1.0));
                    var adaptiveSecs = (int)(baseTtl.TotalSeconds * multiplier);
                    adaptiveSecs = Math.Min(adaptiveSecs, MaxAdaptiveTtlSecs);

                    _logger.Debug(
                        "Adaptive TTL for indexer {0}: stability {1:F2}, {2:F1}x, {3}s → {4}s",
                        GetIndexerLabel(indexerId),
                        score,
                        multiplier,
                        (int)baseTtl.TotalSeconds,
                        adaptiveSecs);

                    return TimeSpan.FromSeconds(adaptiveSecs);
                }
            }

            return baseTtl;
        }

        /// <summary>
        /// Serializes concurrent requests for the same cache key to prevent
        /// thundering herd. Uses per-key SemaphoreSlim with double-check pattern.
        /// </summary>
        public async Task<T> DeduplicateAsync<T>(int indexerId, NewznabRequest request, Func<Task<T>> factory)
        {
            // Probabilistic cleanup: ~2% of requests trigger a bounded scan of _keyLocks,
            // removing unheld semaphores to prevent growth from one-off title searches.
            // Hard cap: full scan (no batch limit) if count exceeds MaxDedupLocks.
            if (_keyLocks.Count > MaxDedupLocks)
            {
                PruneStaleLocks(maxScan: int.MaxValue, maxPrune: int.MaxValue);
            }
            else if (_keyLocks.Count > 100 && Random.Shared.Next(50) == 0)
            {
                PruneStaleLocks();
            }

            var key = GenerateCacheKey(indexerId, request);
            var semaphore = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await semaphore.WaitAsync();
            try
            {
                return await factory();
            }
            finally
            {
                // Semaphores are NOT removed here — removing races with GetOrAdd:
                // Thread A removes after Release(), Thread B already holds old ref,
                // Thread C creates new semaphore → B and C run concurrently.
                // Cleanup: InvalidateIndexer/Clear (admin ops) + probabilistic pruning.
                semaphore.Release();
            }
        }

        internal static string GenerateCacheKey(int indexerId, NewznabRequest request)
        {
            var sb = new StringBuilder();
            sb.Append(indexerId).Append(':');

            // Search-meaningful fields in deterministic order.
            // Excluded: source, host, server, configured, cachetime (volatile/meta)
            // q is trimmed and cat is sorted to match ReleaseSearchService normalization.
            AppendIfNotNull(sb, "t", request.t);
            AppendIfNotNull(sb, "q", request.q?.Trim());
            AppendIfNotNull(sb, "cat", NormalizeCat(request.cat));
            AppendIfNotNull(sb, "imdbid", request.imdbid);
            AppendIfNotNull(sb, "tmdbid", request.tmdbid?.ToString());
            AppendIfNotNull(sb, "rid", request.rid?.ToString());
            AppendIfNotNull(sb, "tvdbid", request.tvdbid?.ToString());
            AppendIfNotNull(sb, "tvmazeid", request.tvmazeid?.ToString());
            AppendIfNotNull(sb, "traktid", request.traktid?.ToString());
            AppendIfNotNull(sb, "doubanid", request.doubanid?.ToString());
            AppendIfNotNull(sb, "season", request.season?.ToString());
            AppendIfNotNull(sb, "ep", request.ep);
            AppendIfNotNull(sb, "album", request.album);
            AppendIfNotNull(sb, "artist", request.artist);
            AppendIfNotNull(sb, "label", request.label);
            AppendIfNotNull(sb, "track", request.track);
            AppendIfNotNull(sb, "year", request.year?.ToString());
            AppendIfNotNull(sb, "genre", request.genre);
            AppendIfNotNull(sb, "author", request.author);
            AppendIfNotNull(sb, "title", request.title);
            AppendIfNotNull(sb, "publisher", request.publisher);
            AppendIfNotNull(sb, "extended", request.extended);
            AppendIfNotNull(sb, "limit", request.limit?.ToString());
            AppendIfNotNull(sb, "offset", request.offset?.ToString());
            AppendIfNotNull(sb, "minage", request.minage?.ToString());
            AppendIfNotNull(sb, "maxage", request.maxage?.ToString());
            AppendIfNotNull(sb, "minsize", request.minsize?.ToString());
            AppendIfNotNull(sb, "maxsize", request.maxsize?.ToString());

            return sb.ToString();
        }

        /// <summary>
        /// Normalizes category string to match ReleaseSearchService parsing:
        /// splits on comma, filters blanks, sorts, and rejoins.
        /// "5000,2000,,3000" → "2000,3000,5000"
        /// </summary>
        private static string NormalizeCat(string cat)
        {
            if (cat == null)
            {
                return null;
            }

            var parts = cat.Split(',')
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            return parts.Length > 0 ? string.Join(",", parts) : null;
        }

        private static void AppendIfNotNull(StringBuilder sb, string name, string value)
        {
            if (value != null)
            {
                sb.Append(name).Append('=').Append(value).Append(KeyDelimiter);
            }
        }

        private string GetIndexerLabel(int indexerId) =>
            _indexerNames.TryGetValue(indexerId, out var name) ? $"{name} ({indexerId})" : indexerId.ToString();

        private void CleanupKeyTracking(string key)
        {
            _fingerprints.TryRemove(key, out _);
            _stabilityTrackers.TryRemove(key, out _);
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
        private void PruneExpiredKeys(int indexerId, ConcurrentDictionary<string, byte> keys)
        {
            var pruned = 0;

            foreach (var trackedKey in keys.Keys)
            {
                if (_cache.Find(trackedKey) == null)
                {
                    // Preserve recent stability data for RSS-like keys across cache expiry.
                    // Keep key in _indexerKeys so future prune cycles and InvalidateIndexer
                    // can find and clean it up. Stale trackers are cleaned up normally.
                    var preserveTracking = _stabilityTrackers.TryGetValue(trackedKey, out var tracker)
                                           && !tracker.IsStale(StabilityRetentionWindow);

                    if (!preserveTracking)
                    {
                        keys.TryRemove(trackedKey, out _);
                        CleanupKeyTracking(trackedKey);
                        pruned++;
                    }
                }
            }

            if (pruned > 0)
            {
                _logger.Debug("Pruned {0} expired keys for indexer {1}, {2} remaining", pruned, GetIndexerLabel(indexerId), keys.Count);
            }
        }

        /// <summary>
        /// Probabilistic cleanup of dedup semaphores to prevent unbounded growth from
        /// one-off title searches. Removes unheld semaphores (CurrentCount == 1).
        /// If a still-valid semaphore is removed, it's cheaply recreated on next GetOrAdd.
        /// Called probabilistically (~2% of requests) or as full scan when cap exceeded.
        /// </summary>
        private void PruneStaleLocks(int maxScan = 200, int maxPrune = 20)
        {
            var pruned = 0;
            var scanned = 0;

            foreach (var kvp in _keyLocks)
            {
                scanned++;

                // CurrentCount == 1 (max) means no thread is in WaitAsync or holding the lock.
                // Race window: a thread between GetOrAdd and WaitAsync could be affected,
                // but this window is nanoseconds — practically impossible to hit.
                if (kvp.Value.CurrentCount == 1)
                {
                    // Atomic compare-and-remove: only removes if key AND value match,
                    // preventing removal of a replacement semaphore added concurrently.
                    _keyLocks.TryRemove(kvp);
                    pruned++;
                    if (pruned >= maxPrune)
                    {
                        break;
                    }
                }

                if (scanned >= maxScan)
                {
                    break;
                }
            }

            if (pruned > 0)
            {
                _logger.Debug(
                    "Pruned {0}/{1} stale dedup semaphores (scanned {2}), {3} remaining",
                    pruned,
                    maxPrune,
                    scanned,
                    _keyLocks.Count);
            }
        }

        private (int TotalReleases, int StableKeys, double AvgStability) ComputeCacheStats(IEnumerable<string> keys)
        {
            var totalReleases = 0;
            var stableKeys = 0;
            var totalStability = 0.0;

            foreach (var key in keys)
            {
                var cached = _cache.Find(key);
                if (cached?.Releases != null)
                {
                    totalReleases += cached.Releases.Count;
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

        /// <summary>
        /// Computes a hash fingerprint to detect result changes. Order-independent:
        /// GUIDs (or titles when no GUIDs) are sorted before hashing so {A,B} and
        /// {B,A} produce the same fingerprint. Seeded with count so size changes are
        /// always detected, even when all GUIDs are null.
        /// </summary>
        private static int ComputeFingerprint(IList<ReleaseInfo> releases)
        {
            if (releases == null || releases.Count == 0)
            {
                return 0;
            }

            var hash = default(HashCode);

            // Seed with count so size changes are detected even when GUIDs are null
            hash.Add(releases.Count);

            var hasGuids = false;
            foreach (var guid in releases
                .Select(r => r.Guid)
                .Where(g => g != null)
                .OrderBy(g => g, StringComparer.Ordinal))
            {
                hash.Add(guid);
                hasGuids = true;
            }

            // Fallback: hash sorted titles when no GUIDs are available (some indexers omit them)
            if (!hasGuids)
            {
                foreach (var title in releases
                    .Select(r => r.Title)
                    .Where(t => t != null)
                    .OrderBy(t => t, StringComparer.Ordinal))
                {
                    hash.Add(title);
                }
            }

            return hash.ToHashCode();
        }

        /// <summary>
        /// Tracks per-cache-key result stability using a decaying counter pair.
        /// Stability score (0.0–1.0) indicates how often cached results match upstream
        /// on refresh. Higher score → results rarely change → safe to extend cache TTL.
        /// </summary>
        private class IndexerStabilityTracker
        {
            // With adaptive TTL reducing upstream call frequency to ~2-3/hr per key,
            // first decay at 20 takes 7-10 hours — too slow for day-night release cycles.
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
