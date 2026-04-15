using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NLog;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.IndexerSearch
{
    public class NewznabCacheIdentityResolver : INewznabCacheIdentityResolver
    {
        private const char KeyDelimiter = '\x1F';
        private readonly Logger _logger;

        public NewznabCacheIdentityResolver(Logger logger)
        {
            _logger = logger;
        }

        public CanonicalCacheIdentity Resolve(int indexerId, NewznabRequest request, IndexerCapabilities capabilities)
        {
            var (canonicalCat, canonicalCategories) = ResolveCanonicalCategories(indexerId, request.cat, capabilities);

            var sb = new StringBuilder();
            sb.Append("v2:indexerId=").Append(indexerId.ToString(CultureInfo.InvariantCulture)).Append(':');

            AppendIfNotNull(sb, "t", request.t);
            AppendIfNotNull(sb, "q", request.q);
            AppendIfNotNull(sb, "cat", canonicalCat);
            AppendIfNotNull(sb, "imdbid", request.imdbid);
            AppendIfNotNull(sb, "tmdbid", request.tmdbid?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "rid", request.rid?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "tvdbid", request.tvdbid?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "tvmazeid", request.tvmazeid?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "traktid", request.traktid?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "doubanid", request.doubanid?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "season", request.season?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "ep", request.ep);
            AppendIfNotNull(sb, "album", request.album);
            AppendIfNotNull(sb, "artist", request.artist);
            AppendIfNotNull(sb, "label", request.label);
            AppendIfNotNull(sb, "track", request.track);
            AppendIfNotNull(sb, "year", request.year?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "genre", request.genre);
            AppendIfNotNull(sb, "author", request.author);
            AppendIfNotNull(sb, "title", request.title);
            AppendIfNotNull(sb, "publisher", request.publisher);
            AppendIfNotNull(sb, "extended", "1");
            AppendIfNotNull(sb, "limit", request.limit?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "offset", request.offset?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "minage", request.minage?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "maxage", request.maxage?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "minsize", request.minsize?.ToString(CultureInfo.InvariantCulture));
            AppendIfNotNull(sb, "maxsize", request.maxsize?.ToString(CultureInfo.InvariantCulture));

            return new CanonicalCacheIdentity(indexerId, sb.ToString(), canonicalCategories);
        }

        private (string SerializedCategories, IReadOnlyList<string> CanonicalCategories) ResolveCanonicalCategories(int indexerId, string rawCat, IndexerCapabilities capabilities)
        {
            var parsedCategories = ParseCategories(rawCat);
            if (parsedCategories.Length == 0)
            {
                return (null, Array.Empty<string>());
            }

            var fallbackCategories = parsedCategories
                .OrderBy(category => category, StringComparer.Ordinal)
                .ToArray();

            if (!TryParseCategories(parsedCategories, out var parsedCategoryIds))
            {
                _logger.Warn(
                    "Canonical cache identity category parse failed for indexer {0}: rawCat={1}",
                    indexerId,
                    rawCat);

                return (string.Join(",", fallbackCategories), fallbackCategories);
            }

            try
            {
                var expandedCategories = capabilities?.Categories?.ExpandTorznabQueryCategories(parsedCategoryIds, mapChildrenCatsToParent: false)
                                       ?? parsedCategoryIds.ToList();

                if (expandedCategories.Count == 0)
                {
                    return (null, Array.Empty<string>());
                }

                var canonicalCategories = expandedCategories
                    .Select(category => category.ToString(CultureInfo.InvariantCulture))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(category => category, StringComparer.Ordinal)
                    .ToArray();

                return (string.Join(",", canonicalCategories), canonicalCategories);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex,
                    "Canonical cache identity category expansion failed for indexer {0}: rawCat={1}, exceptionType={2}",
                    indexerId,
                    rawCat,
                    ex.GetType().Name);

                return (string.Join(",", fallbackCategories), fallbackCategories);
            }
        }

        private static bool TryParseCategories(string[] parsedCategories, out int[] parsedCategoryIds)
        {
            parsedCategoryIds = new int[parsedCategories.Length];

            for (var i = 0; i < parsedCategories.Length; i++)
            {
                if (!int.TryParse(parsedCategories[i], out parsedCategoryIds[i]))
                {
                    parsedCategoryIds = Array.Empty<int>();
                    return false;
                }
            }

            return true;
        }

        private static string[] ParseCategories(string cat)
        {
            if (cat == null)
            {
                return Array.Empty<string>();
            }

            return cat.Split(',')
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static void AppendIfNotNull(StringBuilder sb, string name, string value)
        {
            if (value != null)
            {
                sb.Append(name).Append('=').Append(value).Append(KeyDelimiter);
            }
        }
    }
}
