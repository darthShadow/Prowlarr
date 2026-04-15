using System.Globalization;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class NewznabCacheIdentityResolverFixture : CoreTest<NewznabCacheIdentityResolver>
    {
        private static NewznabRequest MakeRequest(string cat = null, string extended = null, string q = null)
        {
            return new NewznabRequest
            {
                t = "search",
                cat = cat,
                extended = extended,
                q = q
            };
        }

        private static IndexerCapabilities MakeCapabilities(params IndexerCategory[] categories)
        {
            var capabilities = new IndexerCapabilities();

            foreach (var category in categories)
            {
                capabilities.Categories.AddCategoryMapping(category.Id, category);
            }

            return capabilities;
        }

        [Test]
        public void Resolve_should_namespace_key_by_indexer_id()
        {
            var request = MakeRequest(cat: "5000");
            var capabilities = MakeCapabilities(NewznabStandardCategory.TV);

            var identityA = Subject.Resolve(1, request, capabilities);
            var identityB = Subject.Resolve(2, request, capabilities);

            identityA.Key.Should().NotBe(identityB.Key);
            identityA.Key.Should().StartWith("v2:indexerId=1:");
            identityB.Key.Should().StartWith("v2:indexerId=2:");
        }

        [Test]
        public void Resolve_should_collapse_equivalent_expanded_categories()
        {
            var capabilities = MakeCapabilities(
                NewznabStandardCategory.TV,
                NewznabStandardCategory.TVSD,
                NewznabStandardCategory.TVHD,
                NewznabStandardCategory.TVUHD);

            var parentOnly = Subject.Resolve(1, MakeRequest(cat: "5000"), capabilities);
            var explicitChildren = Subject.Resolve(1, MakeRequest(cat: "5000,5030,5040,5045"), capabilities);

            parentOnly.Key.Should().Be(explicitChildren.Key);
            parentOnly.CanonicalCategories.Should().Contain(new[] { "5000", "5030", "5040", "5045" });
        }

        [Test]
        public void Resolve_should_keep_top_level_category_parents_distinct()
        {
            var capabilities = MakeCapabilities(
                NewznabStandardCategory.Movies,
                NewznabStandardCategory.MoviesSD,
                NewznabStandardCategory.MoviesHD,
                NewznabStandardCategory.TV,
                NewznabStandardCategory.TVSD,
                NewznabStandardCategory.TVHD);

            var moviesParent = Subject.Resolve(1, MakeRequest(cat: "2000"), capabilities);
            var tvParent = Subject.Resolve(1, MakeRequest(cat: "5000"), capabilities);

            moviesParent.Key.Should().NotBe(tvParent.Key);
            moviesParent.CanonicalCategories.Should().Contain("2000");
            tvParent.CanonicalCategories.Should().Contain("5000");
        }

        [Test]
        public void Resolve_should_normalize_category_order()
        {
            var left = Subject.Resolve(1, MakeRequest(cat: "5040,5045"), MakeCapabilities(NewznabStandardCategory.TVHD, NewznabStandardCategory.TVUHD));
            var right = Subject.Resolve(1, MakeRequest(cat: "5045,5040"), MakeCapabilities(NewznabStandardCategory.TVHD, NewznabStandardCategory.TVUHD));

            left.Key.Should().Be(right.Key);
        }

        [Test]
        public void Resolve_should_collapse_extended_variants_to_constant()
        {
            var capabilities = MakeCapabilities(NewznabStandardCategory.TV);

            var withoutExtended = Subject.Resolve(1, MakeRequest(cat: "5000", extended: null), capabilities);
            var explicitExtended = Subject.Resolve(1, MakeRequest(cat: "5000", extended: "1"), capabilities);
            var arbitraryExtended = Subject.Resolve(1, MakeRequest(cat: "5000", extended: "anything"), capabilities);

            withoutExtended.Key.Should().Be(explicitExtended.Key);
            explicitExtended.Key.Should().Be(arbitraryExtended.Key);
        }

        [Test]
        public void Resolve_should_include_extended_constant_in_key()
        {
            var identity = Subject.Resolve(1, MakeRequest(cat: "5000", extended: "0"), MakeCapabilities(NewznabStandardCategory.TV));

            identity.Key.Should().Contain("extended=1");
        }

        [Test]
        public void Resolve_should_prefix_keys_with_v2_version_marker()
        {
            var identity = Subject.Resolve(1, MakeRequest(cat: "5000"), MakeCapabilities(NewznabStandardCategory.TV));

            identity.Key.Should().StartWith("v2:");
        }

        [Test]
        public void Resolve_should_not_include_apikey_material()
        {
            var identity = Subject.Resolve(1, MakeRequest(cat: "5000", q: "normal-query"), MakeCapabilities(NewznabStandardCategory.TV));

            foreach (var apiKey in new[] { "secret-api-key", "another-secret", "1234567890abcdef" })
            {
                identity.Key.Should().NotContain(apiKey);
            }
        }

        [Test]
        public void Resolve_should_not_include_realistic_indexer_settings_apikey_material()
        {
            var sentinel = "APIKEY-A6-SENTINEL-1a2b3c4d5e6f";
            var settings = new NewznabSettings
            {
                BaseUrl = "https://indexer.example.test",
                ApiKey = sentinel
            };
            var request = new NewznabRequest
            {
                t = "search",
                cat = "5000",
                q = "representative-query",
                limit = 100,
                offset = 0
            };

            var identity = Subject.Resolve(12, request, MakeCapabilities(NewznabStandardCategory.TV));

            identity.Key.Should().NotContain(settings.ApiKey);
        }

        [Test]
        public void CanonicalCacheIdentity_should_use_value_equality()
        {
            var left = new CanonicalCacheIdentity(1, "v2:indexerId=1:cat=5000", new[] { "5000", "5030" });
            var right = new CanonicalCacheIdentity(1, "v2:indexerId=1:cat=5000", new[] { "5000", "5030" });

            left.Should().Be(right);
            (left == right).Should().BeTrue();
            left.GetHashCode().Should().Be(right.GetHashCode());
            left.Should().NotBe(new CanonicalCacheIdentity(2, left.Key, left.CanonicalCategories));
            left.Should().NotBe(new CanonicalCacheIdentity(1, "v2:indexerId=1:cat=2000", left.CanonicalCategories));
            left.Should().NotBe(new CanonicalCacheIdentity(1, left.Key, new[] { "5000", "5040" }));
        }

        [Test]
        public void Resolve_should_serialize_integer_fields_with_invariant_culture()
        {
            var previousCulture = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
                var request = new NewznabRequest
                {
                    t = "search",
                    cat = "5000",
                    tmdbid = 12345,
                    rid = 234,
                    tvdbid = 345,
                    tvmazeid = 456,
                    traktid = 567,
                    doubanid = 678,
                    season = 9,
                    year = 2024,
                    limit = 100,
                    offset = 25,
                    minage = 1,
                    maxage = 2,
                    minsize = 123456789,
                    maxsize = 987654321
                };

                var identity = Subject.Resolve(987, request, MakeCapabilities(NewznabStandardCategory.TV));

                identity.Key.Should().StartWith("v2:indexerId=987:");
                identity.Key.Should().Contain("cat=5000");
                identity.Key.Should().Contain("tmdbid=12345");
                identity.Key.Should().Contain("rid=234");
                identity.Key.Should().Contain("tvdbid=345");
                identity.Key.Should().Contain("tvmazeid=456");
                identity.Key.Should().Contain("traktid=567");
                identity.Key.Should().Contain("doubanid=678");
                identity.Key.Should().Contain("season=9");
                identity.Key.Should().Contain("year=2024");
                identity.Key.Should().Contain("limit=100");
                identity.Key.Should().Contain("offset=25");
                identity.Key.Should().Contain("minage=1");
                identity.Key.Should().Contain("maxage=2");
                identity.Key.Should().Contain("minsize=123456789");
                identity.Key.Should().Contain("maxsize=987654321");
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        [Test]
        public void Resolve_should_preserve_q_whitespace()
        {
            var trimmed = Subject.Resolve(1, MakeRequest(q: "test"), MakeCapabilities(NewznabStandardCategory.TV));
            var padded = Subject.Resolve(1, MakeRequest(q: " test "), MakeCapabilities(NewznabStandardCategory.TV));

            padded.Key.Should().NotBe(trimmed.Key);
            padded.Key.Should().Contain("q= test ");
        }

        [Test]
        public void Resolve_should_use_capabilities_for_category_expansion()
        {
            var request = MakeRequest(cat: "5000");
            var narrowCapabilities = MakeCapabilities(NewznabStandardCategory.TV);
            var expandedCapabilities = MakeCapabilities(
                NewznabStandardCategory.TV,
                NewznabStandardCategory.TVSD,
                NewznabStandardCategory.TVHD,
                NewznabStandardCategory.TVUHD);

            var narrow = Subject.Resolve(1, request, narrowCapabilities);
            var expanded = Subject.Resolve(1, request, expandedCapabilities);

            narrow.Key.Should().NotBe(expanded.Key);
        }

        [Test]
        public void Resolve_should_fallback_to_normalized_raw_categories_when_category_parse_fails()
        {
            var capabilities = MakeCapabilities(NewznabStandardCategory.TV);

            var identity = Subject.Resolve(1, MakeRequest(cat: "5000, invalid, 5040"), capabilities);

            identity.Key.Should().Contain("cat=5000,5040,invalid");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void Resolve_should_fallback_to_normalized_raw_categories_when_expansion_throws()
        {
            var capabilities = MakeCapabilities(NewznabStandardCategory.TV);
            var parentCategory = capabilities.Categories.GetTorznabCategoryTree().Single(category => category.Id == NewznabStandardCategory.TV.Id);
            var subCategoriesProperty = typeof(IndexerCategory).GetProperty(nameof(IndexerCategory.SubCategories), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            subCategoriesProperty.SetValue(parentCategory, null);

            var identity = Subject.Resolve(1, MakeRequest(cat: "5000"), capabilities);

            identity.Key.Should().Contain("cat=5000");
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void Set_and_find_should_round_trip_with_resolved_identity()
        {
            var cacheService = Mocker.Resolve<NewznabResultsCacheService>();
            var request = MakeRequest(cat: "5000");
            var identity = Subject.Resolve(1, request, MakeCapabilities(NewznabStandardCategory.TV));
            var results = new NewznabResults
            {
                Releases = new[] { new NzbDrone.Core.Parser.Model.ReleaseInfo { Guid = "guid-1", Title = "Release 1" } }.ToList<NzbDrone.Core.Parser.Model.ReleaseInfo>()
            };

            cacheService.Set(identity, request, results, System.TimeSpan.FromMinutes(10), "Indexer");

            cacheService.Find(identity, request).Should().NotBeNull();
        }
    }
}
