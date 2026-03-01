using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
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

        /// <summary>Stores results with the given TTL. Caller must clone before storing to avoid mutation.</summary>
        void Set(int indexerId, NewznabRequest request, NewznabResults results, TimeSpan ttl);

        /// <summary>Removes all cached entries and dedup locks for the given indexer.</summary>
        void InvalidateIndexer(int indexerId);

        /// <summary>Removes all cached entries and dedup locks across all indexers.</summary>
        void Clear();

        /// <summary>
        /// Resolves effective TTL. Returns null if caching should be bypassed (cachetime=0).
        /// Priority: per-request → per-indexer → global default. Floor: global minimum.
        /// </summary>
        TimeSpan? ResolveTtl(int? requestCacheTimeSecs, int? indexerCacheTtlMins);

        /// <summary>
        /// Serializes concurrent requests for the same cache key to prevent thundering herd.
        /// Factory MUST re-check cache before fetching (double-check pattern for dedup correctness).
        /// </summary>
        Task<T> DeduplicateAsync<T>(int indexerId, NewznabRequest request, Func<Task<T>> factory);
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

        private static readonly TimeSpan StatsLogInterval = TimeSpan.FromMinutes(5);

        private readonly ICached<NewznabResults> _cache;
        private readonly IConfigService _configService;

        // Tracks cache keys per indexer for targeted invalidation (indexerId → set of cache keys)
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _indexerKeys;

        // Per-key semaphores for DeduplicateAsync (not disposed on removal — see InvalidateIndexer)
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks;
        private readonly ConcurrentDictionary<int, IndexerCacheStats> _indexerStats;
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
            _lastGlobalLogTime = DateTime.UtcNow;
            _logger = logger;
        }

        public NewznabResults Find(int indexerId, NewznabRequest request)
        {
            var key = GenerateCacheKey(indexerId, request);
            var result = _cache.Find(key);

            var stats = _indexerStats.GetOrAdd(indexerId, _ => new IndexerCacheStats());

            if (result != null)
            {
                Interlocked.Increment(ref _globalHits);
                stats.IncrementHits();
                LogStatsIfDue(indexerId, stats);
                return result;
            }

            Interlocked.Increment(ref _globalMisses);
            stats.IncrementMisses();
            LogStatsIfDue(indexerId, stats);

            // Prune dead key from tracking (expired naturally)
            PruneKey(indexerId, key);

            return null;
        }

        public void Set(int indexerId, NewznabRequest request, NewznabResults results, TimeSpan ttl)
        {
            var key = GenerateCacheKey(indexerId, request);

            _cache.Set(key, results, ttl);

            var keys = _indexerKeys.GetOrAdd(indexerId, _ => new ConcurrentDictionary<string, byte>());
            keys.TryAdd(key, 0);

            // Opportunistically prune expired keys for this indexer to prevent unbounded
            // growth from unique title searches (e.g. q=Some.Movie.2024.1080p.BluRay).
            // Only runs when there are enough tracked keys to warrant the scan.
            if (keys.Count > 50)
            {
                PruneExpiredKeys(indexerId, keys);
            }

            _logger.Debug("Cached {0} releases for indexer {1}, TTL {2}s", results.Releases?.Count ?? 0, indexerId, ttl.TotalSeconds);
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
                }

                _logger.Debug("Invalidated {0} cached entries for indexer {1}", keys.Count, indexerId);
            }
        }

        public void Clear()
        {
            _cache.Clear();
            _indexerKeys.Clear();
            _keyLocks.Clear();

            // _indexerStats intentionally not cleared — stats span cache lifecycle and are
            // diagnostic only. Clearing them would lose trend data without functional benefit.
            _logger.Debug("Cleared all cached results");
        }

        /// <summary>
        /// Resolves effective cache TTL from three-tier config.
        /// Returns null if caching should be bypassed (cachetime=0).
        /// Priority: per-request → per-indexer → global default.
        /// Floor: global minimum (except cachetime=0 which bypasses entirely).
        /// </summary>
        public TimeSpan? ResolveTtl(int? requestCacheTimeSecs, int? indexerCacheTtlMins)
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

            return TimeSpan.FromSeconds(effectiveSecs);
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

        private void PruneKey(int indexerId, string key)
        {
            if (_indexerKeys.TryGetValue(indexerId, out var keys))
            {
                keys.TryRemove(key, out _);
            }
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
                    keys.TryRemove(trackedKey, out _);
                    pruned++;
                }
            }

            if (pruned > 0)
            {
                _logger.Debug("Pruned {0} expired keys for indexer {1}, {2} remaining", pruned, indexerId, keys.Count);
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

        private void LogStatsIfDue(int indexerId, IndexerCacheStats stats)
        {
            var now = DateTime.UtcNow;

            // Per-indexer stats
            if (stats.IsLogDue(now, StatsLogInterval))
            {
                var (hits, misses) = stats.ResetAndGetCounts(now);
                if (hits > 0 || misses > 0)
                {
                    var entryCount = _indexerKeys.TryGetValue(indexerId, out var k) ? k.Count : 0;
                    _logger.Info(
                        "Cache stats for indexer {0}: {1} hits, {2} misses ({3:F0}% hit rate), {4} entries",
                        indexerId,
                        hits,
                        misses,
                        hits + misses > 0 ? (double)hits / (hits + misses) * 100 : 0,
                        entryCount);
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
                        _logger.Info(
                            "Cache stats (global): {0} hits, {1} misses ({2:F0}% hit rate), {3} entries",
                            globalHits,
                            globalMisses,
                            globalHits + globalMisses > 0 ? (double)globalHits / (globalHits + globalMisses) * 100 : 0,
                            _cache.Count);
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

            public bool IsLogDue(DateTime now, TimeSpan interval)
            {
                lock (_lock)
                {
                    return now - _lastLogTime >= interval;
                }
            }

            public (long Hits, long Misses) ResetAndGetCounts(DateTime now)
            {
                lock (_lock)
                {
                    var hits = Interlocked.Exchange(ref _hits, 0);
                    var misses = Interlocked.Exchange(ref _misses, 0);
                    _lastLogTime = now;
                    return (hits, misses);
                }
            }
        }
    }
}
