using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace NzbDrone.Core.IndexerSearch
{
    public sealed class CanonicalCacheIdentity : IEquatable<CanonicalCacheIdentity>
    {
        public CanonicalCacheIdentity(int indexerId, string key, IEnumerable<string> canonicalCategories)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Canonical cache key must be populated.", nameof(key));
            }

            IndexerId = indexerId;
            Key = key;
            CanonicalCategories = new ReadOnlyCollection<string>((canonicalCategories ?? Array.Empty<string>()).ToList());
        }

        public int IndexerId { get; }

        public string Key { get; }

        public IReadOnlyList<string> CanonicalCategories { get; }

        public bool Equals(CanonicalCacheIdentity other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return IndexerId == other.IndexerId &&
                   Key == other.Key &&
                   CanonicalCategories.SequenceEqual(other.CanonicalCategories, StringComparer.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as CanonicalCacheIdentity);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = 17;
                hashCode = (hashCode * 31) + IndexerId.GetHashCode();
                hashCode = (hashCode * 31) + StringComparer.Ordinal.GetHashCode(Key);

                foreach (var category in CanonicalCategories)
                {
                    hashCode = (hashCode * 31) + StringComparer.Ordinal.GetHashCode(category);
                }

                return hashCode;
            }
        }

        public static bool operator ==(CanonicalCacheIdentity left, CanonicalCacheIdentity right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(CanonicalCacheIdentity left, CanonicalCacheIdentity right)
        {
            return !Equals(left, right);
        }
    }
}
