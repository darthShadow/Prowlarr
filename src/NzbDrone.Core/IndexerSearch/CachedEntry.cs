using System;

namespace NzbDrone.Core.IndexerSearch
{
    public sealed class CachedEntry
    {
        public CachedEntry(NewznabResults results, CachedEntryMetadata metadata)
        {
            Results = results ?? throw new ArgumentNullException(nameof(results));
            Metadata = metadata;
        }

        public NewznabResults Results { get; }

        public CachedEntryMetadata Metadata { get; }
    }
}
