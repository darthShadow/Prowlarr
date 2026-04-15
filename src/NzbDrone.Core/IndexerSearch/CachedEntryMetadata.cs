using System;

namespace NzbDrone.Core.IndexerSearch
{
    public readonly record struct CachedEntryMetadata
    {
        public CachedEntryMetadata(DateTime cachedAtUtc, int cachedTtlSecs)
        {
            if (cachedAtUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("CachedAtUtc must be UTC.", nameof(cachedAtUtc));
            }

            if (cachedTtlSecs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cachedTtlSecs), "CachedTtlSecs must be greater than zero.");
            }

            CachedAtUtc = cachedAtUtc;
            CachedTtlSecs = cachedTtlSecs;
        }

        public DateTime CachedAtUtc { get; }

        public int CachedTtlSecs { get; }
    }
}
