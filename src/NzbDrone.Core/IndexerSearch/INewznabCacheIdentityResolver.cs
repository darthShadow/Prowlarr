using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.IndexerSearch
{
    public interface INewznabCacheIdentityResolver
    {
        CanonicalCacheIdentity Resolve(int indexerId, NewznabRequest request, IndexerCapabilities capabilities);
    }
}
