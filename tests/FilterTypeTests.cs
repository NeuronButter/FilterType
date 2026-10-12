using DnsServerCore.ApplicationCommon;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary.Net;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;
using TechnitiumLibrary.Net.Proxy;
using Xunit;

namespace FilterType.Tests
{
    public class FilterTypeTests
    {
        private static readonly IPEndPoint DefaultClientEP = new IPEndPoint(IPAddress.Parse("192.168.10.50"), 54321);

        #region MockDnsServer

        private sealed class MockDnsServer : IDnsServer
        {
            public string ApplicationName => "FilterType";
            public string ApplicationFolder => AppContext.BaseDirectory;
            public string ServerDomain => "dns.local";
            public MailAddress ResponsiblePerson => new MailAddress("admin@dns.local");
            public IDnsCache DnsCache => null!;
            public NetProxy? Proxy => null;
            public IPv6Mode IPv6Mode => IPv6Mode.Enabled;
            public ushort UdpPayloadSize => 1232;

            public Func<DnsQuestionRecord, DnsDatagram>? DirectQueryHandler { get; set; }
            public List<string> Logs { get; } = new List<string>();

            public Task<DnsDatagram> DirectQueryAsync(DnsQuestionRecord question, int timeout = 4000, CancellationToken cancellationToken = default)
            {
                if (DirectQueryHandler is not null)
                    return Task.FromResult(DirectQueryHandler(question));

                return Task.FromResult(new DnsDatagram(1, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, DnsResponseCode.NxDomain, [question]));
            }

            public Task<DnsDatagram> DirectQueryAsync(DnsDatagram request, int timeout = 4000, CancellationToken cancellationToken = default)
            {
                return DirectQueryAsync(request.Question[0], timeout, cancellationToken);
            }

            public Task<DnsDatagram> DirectQueryAsync(DnsDatagram request, IPEndPoint remoteEP, int timeout = 4000, CancellationToken cancellationToken = default)
            {
                return DirectQueryAsync(request.Question[0], timeout, cancellationToken);
            }

            public Task<DnsDatagram> ResolveAsync(DnsQuestionRecord question, CancellationToken cancellationToken = default)
            {
                return DirectQueryAsync(question, 4000, cancellationToken);
            }

            public bool IsLocallyServedZone(string domain) => false;
            public void WriteLog(string message) => Logs.Add(message);
            public void WriteLog(Exception ex) => Logs.Add(ex.ToString());
            public void WriteLog(string message, Exception ex) => Logs.Add($"{message}: {ex}");
        }

        #endregion

        #region Helper Methods

        private static DnsDatagram CreateRequest(string domain, DnsResourceRecordType type, bool dnssecOk = false)
        {
            DnsQuestionRecord question = new DnsQuestionRecord(domain, type, DnsClass.IN);
            return new DnsDatagram(1234, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, [question], null, null, null, 1232, dnssecOk ? EDnsHeaderFlags.DNSSEC_OK : EDnsHeaderFlags.None);
        }

        private static DnsDatagram CreateResponse(DnsDatagram request, IReadOnlyList<DnsResourceRecord> answers, DnsResponseCode rcode = DnsResponseCode.NoError, IReadOnlyList<DnsResourceRecord>? authority = null)
        {
            return new DnsDatagram(
                request.Identifier,
                true,
                request.OPCODE,
                false,
                false,
                request.RecursionDesired,
                true,
                false,
                false,
                rcode,
                request.Question,
                answers,
                authority);
        }

        private static DnsResourceRecord CreateHttpsRecord(string domain, uint ttl = 300)
        {
            return new DnsResourceRecord(
                domain,
                DnsResourceRecordType.HTTPS,
                DnsClass.IN,
                ttl,
                new DnsSVCBRecordData(1, ".", new Dictionary<DnsSvcParamKey, DnsSvcParamValue>()));
        }

        #endregion

        [Fact]
        public async Task ConfigParsing_DirectArray_MatchesRequestedFormat()
        {
            // Exact JSON format from user prompt:
            string config = @"[
  {
    ""name"": ""example.com"",
    ""type"": ""HTTPS""
  },
  {
    ""name"": ""example.com"",
    ""type"": ""AAAA""
  }
]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();

            await app.InitializeAsync(server, config);

            Assert.Equal(30u, app.DefaultTtl);
            Assert.Equal(2, app.FilterRules.Count);

            Assert.Equal("example.com", app.FilterRules[0].Name);
            Assert.Equal(DnsResourceRecordType.HTTPS, app.FilterRules[0].Type);
            Assert.True(app.FilterRules[0].BlockSubDomains);
            Assert.False(app.FilterRules[0].RequireARecord);

            Assert.Equal("example.com", app.FilterRules[1].Name);
            Assert.Equal(DnsResourceRecordType.AAAA, app.FilterRules[1].Type);
            Assert.True(app.FilterRules[1].BlockSubDomains);
            Assert.False(app.FilterRules[1].RequireARecord);
        }

        [Fact]
        public async Task ConfigParsing_ObjectWrapper_LoadsOptionsAndFilters()
        {
            string config = @"{
  ""defaultTtl"": 60,
  ""filters"": [
    {
      ""name"": ""blocked.com"",
      ""types"": [ ""HTTPS"", ""AAAA"" ],
      ""blockSubDomains"": false,
      ""requireARecord"": true
    }
  ]
}";
            MockDnsServer server = new MockDnsServer();
            App app = new App();

            await app.InitializeAsync(server, config);

            Assert.Equal(60u, app.DefaultTtl);

            Assert.Equal(2, app.FilterRules.Count);
            Assert.Equal("blocked.com", app.FilterRules[0].Name);
            Assert.Equal(DnsResourceRecordType.HTTPS, app.FilterRules[0].Type);
            Assert.False(app.FilterRules[0].BlockSubDomains);
            Assert.True(app.FilterRules[0].RequireARecord);

            Assert.Equal("blocked.com", app.FilterRules[1].Name);
            Assert.Equal(DnsResourceRecordType.AAAA, app.FilterRules[1].Type);
            Assert.False(app.FilterRules[1].BlockSubDomains);
            Assert.True(app.FilterRules[1].RequireARecord);
        }

        [Fact]
        public async Task PostProcess_FiltersConfiguredDomainAndType()
        {
            string config = @"[
  { ""name"": ""example.com"", ""type"": ""HTTPS"" },
  { ""name"": ""example.com"", ""type"": ""AAAA"" }
]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            // 1. example.com HTTPS -> Filtered
            DnsDatagram reqHttps = CreateRequest("example.com", DnsResourceRecordType.HTTPS);
            DnsDatagram resHttps = CreateResponse(reqHttps, [CreateHttpsRecord("example.com")]);
            DnsDatagram? filteredHttps = await app.PostProcessAsync(reqHttps, DefaultClientEP, DnsTransportProtocol.Udp, resHttps);
            Assert.NotNull(filteredHttps);
            Assert.Equal(DnsResponseCode.NoError, filteredHttps.RCODE);
            Assert.Empty(filteredHttps.Answer);
            Assert.Single(filteredHttps.Authority);
            Assert.Equal(DnsResourceRecordType.SOA, filteredHttps.Authority[0].Type);

            // 2. test.example.com AAAA -> Filtered (subdomain)
            DnsDatagram reqAaaa = CreateRequest("test.example.com", DnsResourceRecordType.AAAA);
            DnsResourceRecord aaaaRec = new DnsResourceRecord("test.example.com", DnsResourceRecordType.AAAA, DnsClass.IN, 300, new DnsAAAARecordData(IPAddress.Parse("2606:4700::1")));
            DnsDatagram resAaaa = CreateResponse(reqAaaa, [aaaaRec]);
            DnsDatagram? filteredAaaa = await app.PostProcessAsync(reqAaaa, DefaultClientEP, DnsTransportProtocol.Udp, resAaaa);
            Assert.NotNull(filteredAaaa);
            Assert.Equal(DnsResponseCode.NoError, filteredAaaa.RCODE);
            Assert.Empty(filteredAaaa.Answer);
            Assert.Single(filteredAaaa.Authority);
            Assert.Equal(DnsResourceRecordType.SOA, filteredAaaa.Authority[0].Type);

            // 3. example.com A -> NOT filtered (allowed through)
            DnsDatagram reqA = CreateRequest("example.com", DnsResourceRecordType.A);
            DnsResourceRecord aRec = new DnsResourceRecord("example.com", DnsResourceRecordType.A, DnsClass.IN, 300, new DnsARecordData(IPAddress.Parse("1.2.3.4")));
            DnsDatagram resA = CreateResponse(reqA, [aRec]);
            DnsDatagram? filteredA = await app.PostProcessAsync(reqA, DefaultClientEP, DnsTransportProtocol.Udp, resA);
            Assert.NotNull(filteredA);
            Assert.Same(resA, filteredA);

            // 4. other.com HTTPS -> NOT filtered (allowed through)
            DnsDatagram reqOther = CreateRequest("other.com", DnsResourceRecordType.HTTPS);
            DnsDatagram resOther = CreateResponse(reqOther, [CreateHttpsRecord("other.com")]);
            DnsDatagram? filteredOther = await app.PostProcessAsync(reqOther, DefaultClientEP, DnsTransportProtocol.Udp, resOther);
            Assert.NotNull(filteredOther);
            Assert.Same(resOther, filteredOther);
        }

        [Fact]
        public async Task PostProcess_Dnssec_WhenClientDnssecOkAndSigned_LeavesUnmodified()
        {
            string config = @"[ { ""name"": ""example.com"", ""type"": ""AAAA"" } ]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            // When client specifies DnssecOk and response is signed with RRSIG, replicate FilterAaaa logic (leave unmodified to avoid breaking DNSSEC validation)
            DnsDatagram request = CreateRequest("example.com", DnsResourceRecordType.AAAA, dnssecOk: true);
            DnsResourceRecord aaaaRecord = new DnsResourceRecord(
                "example.com",
                DnsResourceRecordType.AAAA,
                DnsClass.IN,
                300,
                new DnsAAAARecordData(IPAddress.Parse("2606:4700:3036::ac43:cb6a")));
            DnsResourceRecord rrsigRecord = new DnsResourceRecord(
                "example.com",
                DnsResourceRecordType.RRSIG,
                DnsClass.IN,
                300,
                new DnsRRSIGRecordData(DnsResourceRecordType.AAAA, DnssecAlgorithm.ECDSAP256SHA256, 2, 300, 1000, 500, 1234, "example.com", [1, 2, 3]));
            DnsDatagram upstreamResponse = CreateResponse(request, [aaaaRecord, rrsigRecord]);

            DnsDatagram? result = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);

            Assert.NotNull(result);
            Assert.Same(upstreamResponse, result);
        }

        [Fact]
        public void FilterRule_NameMatching_WorksCorrectly()
        {
            FilterRule rule = new FilterRule("example.com", DnsResourceRecordType.HTTPS, blockSubDomains: true);

            Assert.True(rule.MatchesName("example.com"));
            Assert.True(rule.MatchesName("example.com."));
            Assert.True(rule.MatchesName("www.example.com"));
            Assert.True(rule.MatchesName("a.b.c.example.com"));
            Assert.False(rule.MatchesName("notexample.com"));
            Assert.False(rule.MatchesName("example.org"));
            Assert.False(rule.MatchesName("com"));

            FilterRule exactOnly = new FilterRule("example.com", DnsResourceRecordType.HTTPS, blockSubDomains: false);
            Assert.True(exactOnly.MatchesName("example.com"));
            Assert.False(exactOnly.MatchesName("www.example.com"));
        }

        [Fact]
        public async Task PostProcess_NonMatchingType_LeavesResponseUntouched()
        {
            string config = @"[ { ""name"": ""example.com"", ""type"": ""HTTPS"" } ]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            // Query for A record
            DnsDatagram request = CreateRequest("example.com", DnsResourceRecordType.A);
            DnsResourceRecord aRecord = new DnsResourceRecord(
                "example.com",
                DnsResourceRecordType.A,
                DnsClass.IN,
                300,
                new DnsARecordData(IPAddress.Parse("93.184.216.34")));
            DnsDatagram upstreamResponse = CreateResponse(request, [aRecord]);

            DnsDatagram? result = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);

            Assert.NotNull(result);
            Assert.Same(upstreamResponse, result);
            Assert.Single(result.Answer);
            Assert.Equal(DnsResourceRecordType.A, result.Answer[0].Type);
        }

        [Fact]
        public async Task PostProcess_NonMatchingDomain_LeavesResponseUntouched()
        {
            string config = @"[ { ""name"": ""example.com"", ""type"": ""HTTPS"" } ]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            DnsDatagram request = CreateRequest("other.com", DnsResourceRecordType.HTTPS);
            DnsResourceRecord httpsRecord = CreateHttpsRecord("other.com");
            DnsDatagram upstreamResponse = CreateResponse(request, [httpsRecord]);

            DnsDatagram? result = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);

            Assert.NotNull(result);
            Assert.Same(upstreamResponse, result);
        }

        [Fact]
        public async Task PostProcess_CnameChain_PreservesCnameAndAttachesSoaForTarget()
        {
            string config = @"[ { ""name"": ""example.com"", ""type"": ""HTTPS"" } ]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            DnsDatagram request = CreateRequest("www.example.com", DnsResourceRecordType.HTTPS);
            DnsResourceRecord cnameRecord = new DnsResourceRecord(
                "www.example.com",
                DnsResourceRecordType.CNAME,
                DnsClass.IN,
                300,
                new DnsCNAMERecordData("cdn.example.net"));
            DnsResourceRecord httpsRecord = CreateHttpsRecord("cdn.example.net");
            DnsDatagram upstreamResponse = CreateResponse(request, [cnameRecord, httpsRecord]);

            DnsDatagram? result = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);

            Assert.NotNull(result);
            Assert.Single(result.Answer);
            Assert.Equal(DnsResourceRecordType.CNAME, result.Answer[0].Type);
            Assert.Single(result.Authority);
            Assert.Equal("cdn.example.net", result.Authority[0].Name);
            Assert.Equal(DnsResourceRecordType.SOA, result.Authority[0].Type);
        }

        [Fact]
        public async Task PostProcess_NxDomainResponse_NotModified()
        {
            string config = @"[ { ""name"": ""example.com"", ""type"": ""HTTPS"" } ]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            DnsDatagram request = CreateRequest("example.com", DnsResourceRecordType.HTTPS);
            DnsDatagram upstreamResponse = CreateResponse(request, [], rcode: DnsResponseCode.NxDomain);

            DnsDatagram? result = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);

            Assert.NotNull(result);
            Assert.Same(upstreamResponse, result);
            Assert.Equal(DnsResponseCode.NxDomain, result.RCODE);
        }

        [Fact]
        public async Task PostProcess_AnyQuery_FiltersOnlyMatchingType()
        {
            string config = @"[ { ""name"": ""example.com"", ""type"": ""HTTPS"" } ]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            DnsDatagram request = CreateRequest("example.com", DnsResourceRecordType.ANY);
            DnsResourceRecord aRecord = new DnsResourceRecord(
                "example.com",
                DnsResourceRecordType.A,
                DnsClass.IN,
                300,
                new DnsARecordData(IPAddress.Parse("93.184.216.34")));
            DnsResourceRecord httpsRecord = CreateHttpsRecord("example.com");
            DnsDatagram upstreamResponse = CreateResponse(request, [aRecord, httpsRecord]);

            DnsDatagram? result = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);

            Assert.NotNull(result);
            Assert.Single(result.Answer);
            Assert.Equal(DnsResourceRecordType.A, result.Answer[0].Type);
        }

        [Fact]
        public async Task PostProcess_RequireARecord_FiltersOnlyIfAExists()
        {
            string config = @"[ { ""name"": ""example.com"", ""type"": ""AAAA"", ""requireARecord"": true } ]";
            MockDnsServer server = new MockDnsServer();
            App app = new App();
            await app.InitializeAsync(server, config);

            DnsDatagram request = CreateRequest("example.com", DnsResourceRecordType.AAAA);
            DnsResourceRecord aaaaRecord = new DnsResourceRecord(
                "example.com",
                DnsResourceRecordType.AAAA,
                DnsClass.IN,
                300,
                new DnsAAAARecordData(IPAddress.Parse("2606:4700:4700::1111")));
            DnsDatagram upstreamResponse = CreateResponse(request, [aaaaRecord]);

            // Scenario 1: A record DOES NOT exist -> AAAA should NOT be filtered
            server.DirectQueryHandler = (q) =>
            {
                return new DnsDatagram(1, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, DnsResponseCode.NoError, [q], []);
            };

            DnsDatagram? result1 = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);
            Assert.NotNull(result1);
            Assert.Single(result1.Answer);
            Assert.Equal(DnsResourceRecordType.AAAA, result1.Answer[0].Type);

            // Scenario 2: A record DOES exist -> AAAA SHOULD be filtered
            server.DirectQueryHandler = (q) =>
            {
                DnsResourceRecord a = new DnsResourceRecord("example.com", DnsResourceRecordType.A, DnsClass.IN, 300, new DnsARecordData(IPAddress.Parse("1.2.3.4")));
                return new DnsDatagram(1, true, DnsOpcode.StandardQuery, false, false, true, true, false, false, DnsResponseCode.NoError, [q], [a]);
            };

            DnsDatagram? result2 = await app.PostProcessAsync(request, DefaultClientEP, DnsTransportProtocol.Udp, upstreamResponse);
            Assert.NotNull(result2);
            Assert.Empty(result2.Answer);
            Assert.Single(result2.Authority);
            Assert.Equal(DnsResourceRecordType.SOA, result2.Authority[0].Type);
        }
    }
}
