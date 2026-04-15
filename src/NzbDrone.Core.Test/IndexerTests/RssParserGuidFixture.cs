using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerTests
{
    [TestFixture]
    public class RssParserGuidFixture : CoreTest
    {
        private sealed class TestableRssParser : RssParser
        {
            public string ParseGuid(XElement item)
            {
                return GetGuid(item);
            }
        }

        private class CleanupIndexer : TestIndexer
        {
            public CleanupIndexer(IIndexerHttpClient httpClient,
                NzbDrone.Core.Messaging.Events.IEventAggregator eventAggregator,
                IIndexerStatusService indexerStatusService,
                NzbDrone.Core.Configuration.IConfigService configService,
                IValidateNzbs nzbValidationService,
                NLog.Logger logger)
                : base(httpClient, eventAggregator, indexerStatusService, configService, nzbValidationService, logger)
            {
            }

            public IList<ReleaseInfo> RunCleanup(IEnumerable<ReleaseInfo> releases)
            {
                return CleanupReleases(releases, new BasicSearchCriteria());
            }
        }

        private sealed class TorrentCleanupIndexer : CleanupIndexer
        {
            public TorrentCleanupIndexer(IIndexerHttpClient httpClient,
                NzbDrone.Core.Messaging.Events.IEventAggregator eventAggregator,
                IIndexerStatusService indexerStatusService,
                NzbDrone.Core.Configuration.IConfigService configService,
                IValidateNzbs nzbValidationService,
                NLog.Logger logger)
                : base(httpClient, eventAggregator, indexerStatusService, configService, nzbValidationService, logger)
            {
            }

            public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        }

        private static XElement MakeItem(string guid = null)
        {
            return new XElement("item",
                guid == null ? null : new XElement("guid", guid),
                new XElement("title", "Release"),
                new XElement("pubDate", "Mon, 01 Jan 2024 00:00:00 GMT"),
                new XElement("link", "https://example.com/download.nzb"),
                new XElement("description", "Description"),
                new XElement("size", "12345"));
        }

        private static IndexerResponse CreateResponse(string content)
        {
            var httpRequest = new HttpRequest("https://example.com/api?t=search");
            var httpResponse = new HttpResponse(httpRequest, new HttpHeader { ContentType = "application/xml" }, new CookieCollection(), Encoding.UTF8.GetBytes(content), 0, HttpStatusCode.OK);

            return new IndexerResponse(new IndexerRequest(httpRequest), httpResponse);
        }

        private CleanupIndexer CreateCleanupIndexer<TIndexer>(int id = 1)
            where TIndexer : CleanupIndexer
        {
            var indexer = Mocker.Resolve<TIndexer>();
            indexer.Definition = new IndexerDefinition
            {
                Id = id,
                Name = "Test Indexer",
                Enable = true,
                Settings = new TestIndexerSettings()
            };

            return indexer;
        }

        [Test]
        public void GetGuid_should_return_guid_when_present()
        {
            var parser = new TestableRssParser();

            parser.ParseGuid(MakeItem("guid-value")).Should().Be("guid-value");
        }

        [Test]
        public void GetGuid_should_return_null_when_missing()
        {
            var parser = new TestableRssParser();

            parser.ParseGuid(MakeItem()).Should().BeNull();
        }

        [Test]
        public void GetGuid_should_return_null_when_empty()
        {
            var parser = new TestableRssParser();

            parser.ParseGuid(MakeItem(string.Empty)).Should().BeNull();
        }

        [Test]
        public void GetGuid_should_be_deterministic_when_missing()
        {
            var parser = new TestableRssParser();
            var item = MakeItem();

            parser.ParseGuid(item).Should().Be(parser.ParseGuid(item));
        }

        [Test]
        public void CleanupReleases_should_hash_download_url_into_guid_when_parser_guid_missing()
        {
            var parser = new RssParser();
            var response = CreateResponse("<rss><channel><item><title>Release</title><pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><link>https://example.com/download.nzb</link><description>Description</description><size>12345</size></item></channel></rss>");
            var parsedRelease = parser.ParseResponse(response).Single();
            var indexer = CreateCleanupIndexer<CleanupIndexer>();

            var cleaned = indexer.RunCleanup(new[] { parsedRelease });

            cleaned.Single().Guid.Should().Be("prowlarr:93bd4942cdc0f4ed1b2e85e5f1a4b0ee1fdeb8e19a3ba83001fdb73915785bbe");
        }

        [Test]
        public void CleanupReleases_should_hash_info_url_into_guid_when_only_info_url_exists()
        {
            var indexer = CreateCleanupIndexer<CleanupIndexer>();
            var release = new ReleaseInfo
            {
                Title = "Release",
                InfoUrl = "https://example.com/details/123"
            };

            var cleaned = indexer.RunCleanup(new[] { release });

            cleaned.Single().Guid.Should().Be("prowlarr:e0da168cc8d2e53dfcdba1fca6543addeb8b87bfb07a26428c33019c54e21971");
        }

        [Test]
        public void CleanupReleases_should_hash_magnet_url_into_guid_for_torrents()
        {
            var indexer = CreateCleanupIndexer<TorrentCleanupIndexer>();
            var release = new TorrentInfo
            {
                Title = "Release",
                MagnetUrl = "magnet:?xt=urn:btih:abcdef"
            };

            var cleaned = indexer.RunCleanup(new[] { release });

            cleaned.Single().Guid.Should().Be("prowlarr:39b9a5eb06a9ee15bf940bc1e193730f3feb98e9bb7af219e57232ca3a851441");
        }

        [Test]
        public void CleanupReleases_should_not_expose_fallback_url_credentials_in_guid()
        {
            var parser = new RssParser();
            var response = CreateResponse("<rss><channel><item><title>Release</title><pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><link>https://indexer.example.test/download?id=123&amp;apikey=GUID-TOKEN-SENTINEL-41C9</link><description>Description</description><size>12345</size></item></channel></rss>");
            var parsedRelease = parser.ParseResponse(response).Single();
            var indexer = CreateCleanupIndexer<CleanupIndexer>();

            var cleaned = indexer.RunCleanup(new[] { parsedRelease });

            var guid = cleaned.Single().Guid;
            guid.Should().MatchRegex("^prowlarr:[0-9a-f]{64}$");
            guid.Should().NotContain("GUID-TOKEN-SENTINEL-41C9");
            guid.Should().NotContain("indexer.example.test");
        }

        [Test]
        public void CleanupReleases_should_derive_fallback_guid_from_indexer_id_and_url()
        {
            ReleaseInfo NewRelease() => new ReleaseInfo { Title = "Release", DownloadUrl = "https://example.com/download.nzb" };

            var first = CreateCleanupIndexer<CleanupIndexer>(1).RunCleanup(new[] { NewRelease() }).Single().Guid;
            var again = CreateCleanupIndexer<CleanupIndexer>(1).RunCleanup(new[] { NewRelease() }).Single().Guid;
            var other = CreateCleanupIndexer<CleanupIndexer>(2).RunCleanup(new[] { NewRelease() }).Single().Guid;

            again.Should().Be(first);
            other.Should().NotBe(first);
        }

        [Test]
        public void CleanupReleases_should_prefer_download_url_for_fallback_guid()
        {
            var indexer = CreateCleanupIndexer<CleanupIndexer>();
            var release = new ReleaseInfo
            {
                Title = "Release",
                DownloadUrl = "https://example.com/download.nzb",
                InfoUrl = "https://example.com/details/123"
            };

            var cleaned = indexer.RunCleanup(new[] { release });

            cleaned.Single().Guid.Should().Be("prowlarr:93bd4942cdc0f4ed1b2e85e5f1a4b0ee1fdeb8e19a3ba83001fdb73915785bbe");
        }

        [Test]
        public void CleanupReleases_should_keep_guid_supplied_by_indexer()
        {
            var indexer = CreateCleanupIndexer<CleanupIndexer>();
            var release = new ReleaseInfo
            {
                Title = "Release",
                Guid = "indexer-guid-1",
                DownloadUrl = "https://example.com/download.nzb"
            };

            var cleaned = indexer.RunCleanup(new[] { release });

            cleaned.Single().Guid.Should().Be("indexer-guid-1");
        }

        [Test]
        public void CleanupReleases_should_warn_when_no_stable_identity_exists()
        {
            var indexer = CreateCleanupIndexer<CleanupIndexer>();

            indexer.RunCleanup(new[] { new ReleaseInfo { Title = "Release without identity" } });

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
