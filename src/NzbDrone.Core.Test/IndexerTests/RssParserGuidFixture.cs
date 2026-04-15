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

        private CleanupIndexer CreateCleanupIndexer<TIndexer>()
            where TIndexer : CleanupIndexer
        {
            var indexer = Mocker.Resolve<TIndexer>();
            indexer.Definition = new IndexerDefinition
            {
                Id = 1,
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
        public void CleanupReleases_should_use_download_url_as_guid_when_parser_guid_missing()
        {
            var parser = new RssParser();
            var response = CreateResponse("<rss><channel><item><title>Release</title><pubDate>Mon, 01 Jan 2024 00:00:00 GMT</pubDate><link>https://example.com/download.nzb</link><description>Description</description><size>12345</size></item></channel></rss>");
            var parsedRelease = parser.ParseResponse(response).Single();
            var indexer = CreateCleanupIndexer<CleanupIndexer>();

            var cleaned = indexer.RunCleanup(new[] { parsedRelease });

            cleaned.Single().Guid.Should().Be("https://example.com/download.nzb");
        }

        [Test]
        public void CleanupReleases_should_use_info_url_as_guid_when_available()
        {
            var indexer = CreateCleanupIndexer<CleanupIndexer>();
            var release = new ReleaseInfo
            {
                Title = "Release",
                InfoUrl = "https://example.com/details/123"
            };

            var cleaned = indexer.RunCleanup(new[] { release });

            cleaned.Single().Guid.Should().Be("https://example.com/details/123");
        }

        [Test]
        public void CleanupReleases_should_use_magnet_url_as_guid_for_torrents()
        {
            var indexer = CreateCleanupIndexer<TorrentCleanupIndexer>();
            var release = new TorrentInfo
            {
                Title = "Release",
                MagnetUrl = "magnet:?xt=urn:btih:abcdef"
            };

            var cleaned = indexer.RunCleanup(new[] { release });

            cleaned.Single().Guid.Should().Be("magnet:?xt=urn:btih:abcdef");
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
