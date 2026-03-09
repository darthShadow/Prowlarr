using System;

namespace NzbDrone.Core.IndexerSearch
{
    public static class NewznabCacheQueryPolicy
    {
        private const double BypassProbabilityRss = 0.02;

        /// <summary>
        /// Returns true only for RSS-like queries that use configured TTLs.
        /// This intentionally matches adaptive TTL eligibility exactly.
        /// </summary>
        public static bool UsesAdaptiveRssCaching(NewznabRequest request) =>
            request.cachetime == null &&
            string.IsNullOrWhiteSpace(request.q) &&
            string.IsNullOrWhiteSpace(request.imdbid) &&
            !request.tmdbid.HasValue &&
            !request.rid.HasValue &&
            !request.tvdbid.HasValue &&
            !request.tvmazeid.HasValue &&
            !request.traktid.HasValue &&
            !request.doubanid.HasValue &&
            !request.season.HasValue &&
            string.IsNullOrWhiteSpace(request.ep) &&
            string.IsNullOrWhiteSpace(request.album) &&
            string.IsNullOrWhiteSpace(request.artist) &&
            string.IsNullOrWhiteSpace(request.label) &&
            string.IsNullOrWhiteSpace(request.track) &&
            !request.year.HasValue &&
            string.IsNullOrWhiteSpace(request.genre) &&
            string.IsNullOrWhiteSpace(request.author) &&
            string.IsNullOrWhiteSpace(request.title) &&
            string.IsNullOrWhiteSpace(request.publisher);

        public static bool ShouldBypassCacheHit(NewznabRequest request, bool atQueryLimit, double? sample = null)
        {
            if (!UsesAdaptiveRssCaching(request) || atQueryLimit)
            {
                return false;
            }

            return (sample ?? Random.Shared.NextDouble()) < BypassProbabilityRss;
        }
    }
}
