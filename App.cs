using DnsServerCore.ApplicationCommon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace FilterType
{
    public sealed class App : IDnsApplication, IDnsPostProcessor
    {
        #region variables

        private static readonly JsonDocumentOptions _jsonParseOptions = new JsonDocumentOptions()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private IDnsServer? _dnsServer;

        private uint _defaultTtl = 30u;
        private FilterRule[] _filterRules = [];

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // Nothing to dispose
        }

        #endregion

        #region public

        public Task InitializeAsync(IDnsServer dnsServer, string? config)
        {
            _dnsServer = dnsServer;

            if (string.IsNullOrWhiteSpace(config))
            {
                _filterRules = [];
                return Task.CompletedTask;
            }

            using JsonDocument jsonDocument = JsonDocument.Parse(config, _jsonParseOptions);
            JsonElement root = jsonDocument.RootElement;

            List<FilterRule> rules = new List<FilterRule>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Direct array format:
                // [
                //   { "name": "example.com", "type": "HTTPS" },
                //   { "name": "example.com", "type": "AAAA" }
                // ]
                _defaultTtl = 30u;

                foreach (JsonElement element in root.EnumerateArray())
                {
                    ParseRule(element, rules);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Object wrapper format:
                // {
                //   "defaultTtl": 30,
                //   "filterRules": [ ... ]
                // }
                _defaultTtl = root.GetPropertyValue("defaultTtl", 30u);

                // Attempt to read rules from standard property names: filterRules, filters, blocked, rules, zones
                JsonElement arrayElement = default;
                bool foundArray = false;

                if (root.TryGetProperty("filterRules", out arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
                    foundArray = true;
                else if (root.TryGetProperty("filters", out arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
                    foundArray = true;
                else if (root.TryGetProperty("blocked", out arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
                    foundArray = true;
                else if (root.TryGetProperty("rules", out arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
                    foundArray = true;
                else if (root.TryGetProperty("zones", out arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
                    foundArray = true;

                if (foundArray)
                {
                    foreach (JsonElement element in arrayElement.EnumerateArray())
                    {
                        ParseRule(element, rules);
                    }
                }
            }

            _filterRules = rules.ToArray();
            _dnsServer.WriteLog($"FilterType app initialized with {_filterRules.Length} rule(s).");

            return Task.CompletedTask;
        }

        public async Task<DnsDatagram?> PostProcessAsync(DnsDatagram request, IPEndPoint remoteEP, DnsTransportProtocol protocol, DnsDatagram response)
        {
            if (response.RCODE != DnsResponseCode.NoError)
                return response;

            if (request.Question.Count == 0 || response.Answer.Count == 0 || _filterRules.Length == 0)
                return response;

            DnsQuestionRecord question = request.Question[0];
            string qname = question.Name;

            // Handle ANY query specifically by stripping matching records
            if (question.Type == DnsResourceRecordType.ANY)
            {
                List<DnsResourceRecord> filteredAnswer = new List<DnsResourceRecord>(response.Answer.Count);
                bool modified = false;

                foreach (DnsResourceRecord record in response.Answer)
                {
                    bool recordBlocked = false;
                    foreach (FilterRule rule in _filterRules)
                    {
                        if (rule.MatchesName(qname) && rule.MatchesType(record.Type))
                        {
                            recordBlocked = true;
                            break;
                        }
                    }

                    if (recordBlocked)
                        modified = true;
                    else
                        filteredAnswer.Add(record);
                }

                if (!modified)
                    return response;

                return new DnsDatagram(
                    response.Identifier,
                    true,
                    response.OPCODE,
                    response.AuthoritativeAnswer,
                    response.Truncation,
                    response.RecursionDesired,
                    response.RecursionAvailable,
                    response.AuthenticData,
                    response.CheckingDisabled,
                    response.RCODE,
                    response.Question,
                    filteredAnswer,
                    response.Authority,
                    response.Additional)
                {
                    Tag = response.Tag
                };
            }

            // Find matching rule for query domain and question type
            FilterRule? matchedRule = null;
            foreach (FilterRule rule in _filterRules)
            {
                if (rule.MatchesName(qname) && rule.MatchesType(question.Type))
                {
                    matchedRule = rule;
                    break;
                }
            }

            if (matchedRule is null)
                return response;

            bool hasTarget = false;

            if (request.DnssecOk)
            {
                foreach (DnsResourceRecord record in response.Answer)
                {
                    if (record.Type == question.Type)
                    {
                        hasTarget = true;
                    }
                    else if (record.Type == DnsResourceRecordType.RRSIG)
                    {
                        //response is signed and the client is DNSSEC aware; must not be modified
                        return response;
                    }
                }
            }
            else
            {
                foreach (DnsResourceRecord record in response.Answer)
                {
                    if (record.Type == question.Type)
                    {
                        hasTarget = true;
                        break;
                    }
                }
            }

            if (!hasTarget)
                return response;

            // If rule specifies requireARecord, check if domain has an A record first
            if (matchedRule.RequireARecord)
            {
                DnsDatagram aResponse = await _dnsServer!.DirectQueryAsync(new DnsQuestionRecord(qname, DnsResourceRecordType.A, DnsClass.IN), 2000);

                if (aResponse.RCODE != DnsResponseCode.NoError)
                    return response;

                bool hasA = false;
                foreach (DnsResourceRecord record in aResponse.Answer)
                {
                    if (record.Type == DnsResourceRecordType.A)
                    {
                        hasA = true;
                        break;
                    }
                }

                if (!hasA)
                    return response;
            }

            // Filter current response by returning NO DATA response (preserving any CNAME chain)
            List<DnsResourceRecord> answer = new List<DnsResourceRecord>();

            foreach (DnsResourceRecord record2 in response.Answer)
            {
                if (record2.Type == DnsResourceRecordType.CNAME)
                {
                    answer.Add(record2);
                    qname = (record2.RDATA as DnsCNAMERecordData)!.Domain;
                }
            }

            string serverDomain = _dnsServer?.ServerDomain ?? "localhost";
            string responsiblePerson = _dnsServer?.ResponsiblePerson?.Address ?? ("admin." + serverDomain);

            DnsResourceRecord[] authority = [new DnsResourceRecord(qname, DnsResourceRecordType.SOA, DnsClass.IN, _defaultTtl, new DnsSOARecordData(serverDomain, responsiblePerson, 1, 3600, 900, 86400, _defaultTtl))];

            return new DnsDatagram(
                response.Identifier,
                true,
                response.OPCODE,
                false,
                false,
                response.RecursionDesired,
                response.RecursionAvailable,
                false,
                false,
                DnsResponseCode.NoError,
                response.Question,
                answer,
                authority)
            {
                Tag = response.Tag
            };
        }

        #endregion

        #region private

        private static void ParseRule(JsonElement element, List<FilterRule> rules)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return;

            string name = string.Empty;
            if (element.TryGetProperty("name", out JsonElement jsonName))
                name = jsonName.GetString() ?? string.Empty;
            else if (element.TryGetProperty("zone", out JsonElement jsonZone))
                name = jsonZone.GetString() ?? string.Empty;
            else if (element.TryGetProperty("domain", out JsonElement jsonDomain))
                name = jsonDomain.GetString() ?? string.Empty;

            bool blockSubDomains = element.GetPropertyValue("blockSubDomains", true);
            bool requireARecord = element.GetPropertyValue("requireARecord", false);

            List<DnsResourceRecordType> types = new List<DnsResourceRecordType>();

            if (element.TryGetProperty("type", out JsonElement jsonType))
            {
                if (TryParseRecordType(jsonType, out DnsResourceRecordType parsedType))
                    types.Add(parsedType);
            }

            if (element.TryGetProperty("types", out JsonElement jsonTypes) && jsonTypes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in jsonTypes.EnumerateArray())
                {
                    if (TryParseRecordType(t, out DnsResourceRecordType parsedType))
                        types.Add(parsedType);
                }
            }

            foreach (DnsResourceRecordType type in types)
            {
                rules.Add(new FilterRule(name, type, blockSubDomains, requireARecord));
            }
        }

        private static bool TryParseRecordType(JsonElement element, out DnsResourceRecordType recordType)
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetUInt16(out ushort val))
                {
                    recordType = (DnsResourceRecordType)val;
                    return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                string? str = element.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    str = str.Trim();
                    if (Enum.TryParse<DnsResourceRecordType>(str, true, out recordType))
                        return true;

                    if (ushort.TryParse(str, out ushort val))
                    {
                        recordType = (DnsResourceRecordType)val;
                        return true;
                    }
                }
            }

            recordType = DnsResourceRecordType.Unknown;
            return false;
        }

        #endregion

        #region properties

        public string Description => "Filters configured DNS resource record types for configured zones by returning a NO DATA response.";

        public IReadOnlyList<FilterRule> FilterRules => _filterRules;

        public uint DefaultTtl => _defaultTtl;

        #endregion
    }

    public sealed class FilterRule
    {
        public string Name { get; }
        public DnsResourceRecordType Type { get; }
        public bool BlockSubDomains { get; }
        public bool RequireARecord { get; }

        public FilterRule(string name, DnsResourceRecordType type, bool blockSubDomains = true, bool requireARecord = false)
        {
            Name = (name ?? string.Empty).Trim().TrimEnd('.');
            Type = type;
            BlockSubDomains = blockSubDomains;
            RequireARecord = requireARecord;
        }

        public bool MatchesName(string domain)
        {
            if (string.IsNullOrEmpty(domain))
                return false;

            domain = domain.Trim().TrimEnd('.');

            if (Name.Length == 0 || Name == "*")
                return true;

            if (domain.Equals(Name, StringComparison.OrdinalIgnoreCase))
                return true;

            if (BlockSubDomains && domain.EndsWith("." + Name, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public bool MatchesType(DnsResourceRecordType queryOrRecordType)
        {
            if (Type == DnsResourceRecordType.ANY)
                return true;

            return Type == queryOrRecordType;
        }
    }
}
