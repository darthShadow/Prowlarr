using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.IndexerSearch;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class NewznabCacheQueryPolicyFixture
    {
        [TestCase(null, null, null, true, TestName = "UsesAdaptiveRssCaching_should_return_true_for_category_only_request")]
        [TestCase("specific.title", null, null, false, TestName = "UsesAdaptiveRssCaching_should_return_false_for_search_query")]
        [TestCase(null, 300, null, false, TestName = "UsesAdaptiveRssCaching_should_return_false_for_explicit_cachetime")]
        [TestCase(null, null, 1, false, TestName = "UsesAdaptiveRssCaching_should_return_false_for_tmdb_lookup")]
        public void UsesAdaptiveRssCaching_should_match_adaptive_ttl_scope(string q, int? cachetime, int? tmdbid, bool expected)
        {
            var request = new NewznabRequest
            {
                t = "search",
                cat = "5000",
                q = q,
                cachetime = cachetime,
                tmdbid = tmdbid
            };

            var actual = NewznabCacheQueryPolicy.UsesAdaptiveRssCaching(request);

            actual.Should().Be(expected,
                $"expected UsesAdaptiveRssCaching to return {expected} for q={q ?? "<null>"}, cachetime={(cachetime.HasValue ? cachetime.Value.ToString() : "<null>")}, tmdbid={(tmdbid.HasValue ? tmdbid.Value.ToString() : "<null>")}, but got {actual}");
        }

        [TestCase(0.0, 0.0)]
        [TestCase(0.49, 0.0)]
        [TestCase(0.5, 0.0)]
        [TestCase(0.75, 0.125)]
        [TestCase(1.0, 0.25)]
        [TestCase(1.5, 0.25)]
        public void GetBypassProbability_should_match_threshold_linear_curve(double ageRatio, double expected)
        {
            NewznabCacheQueryPolicy.GetBypassProbability(ageRatio)
                .Should().BeApproximately(expected, 0.000001);
        }

        [TestCase(null, null, false, 0.75, 0.124, true, TestName = "ShouldBypassCacheHit_should_allow_sample_below_age_scaled_probability")]
        [TestCase(null, null, false, 0.75, 0.125, false, TestName = "ShouldBypassCacheHit_should_reject_sample_at_age_scaled_probability")]
        [TestCase("specific.title", null, false, 0.75, 0.0, false, TestName = "ShouldBypassCacheHit_should_reject_non_rss_queries")]
        [TestCase(null, 300, false, 0.75, 0.0, false, TestName = "ShouldBypassCacheHit_should_reject_explicit_cachetime")]
        [TestCase(null, null, true, 0.75, 0.0, false, TestName = "ShouldBypassCacheHit_should_reject_requests_at_query_limit")]
        public void ShouldBypassCacheHit_should_enforce_scope_budget_and_probability(string q, int? cachetime, bool atQueryLimit, double ageRatio, double sample, bool expected)
        {
            var request = new NewznabRequest
            {
                t = "search",
                cat = "5000",
                q = q,
                cachetime = cachetime
            };

            var actual = NewznabCacheQueryPolicy.ShouldBypassCacheHit(request, atQueryLimit, ageRatio, sample: sample);

            actual.Should().Be(expected,
                $"expected ShouldBypassCacheHit to return {expected} for q={q ?? "<null>"}, cachetime={(cachetime.HasValue ? cachetime.Value.ToString() : "<null>")}, atQueryLimit={atQueryLimit}, ageRatio={ageRatio}, sample={sample}, but got {actual}");
        }

        [Test]
        public void ShouldBypassCacheHit_should_respect_active_cooldown()
        {
            var request = new NewznabRequest { t = "search", cat = "5000" };

            var actual = NewznabCacheQueryPolicy.ShouldBypassCacheHit(
                request,
                atQueryLimit: false,
                ageRatio: 1.0,
                bypassSuppressedUntilUtc: DateTime.UtcNow.AddSeconds(30),
                sample: 0.0);

            actual.Should().BeFalse();
        }

        [Test]
        public void ShouldBypassCacheHit_should_ignore_expired_cooldown()
        {
            var request = new NewznabRequest { t = "search", cat = "5000" };

            var actual = NewznabCacheQueryPolicy.ShouldBypassCacheHit(
                request,
                atQueryLimit: false,
                ageRatio: 1.0,
                bypassSuppressedUntilUtc: DateTime.UtcNow.AddSeconds(-1),
                sample: 0.0);

            actual.Should().BeTrue();
        }
    }
}
