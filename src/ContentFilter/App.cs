using System.Collections.Concurrent;
using System.Net;
using DnsServerCore.ApplicationCommon;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.EDnsOptions;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter;

public sealed class App : IDnsApplication, IDnsRequestBlockingHandler
{
    /// <summary>Default TTL in seconds for rewrite DNS responses.</summary>
    internal const uint DefaultTtlSeconds = 300;

    /// <summary>TTL in seconds for blocking address responses (shorter for faster recovery on config changes).</summary>
    internal const uint BlockingTtlSeconds = 60;

    /// <summary>Background blocklist refresh interval in minutes.</summary>
    internal const int RefreshIntervalMinutes = 15;

    /// <summary>Initial delay before first blocklist refresh in seconds.</summary>
    internal const int InitialDelaySeconds = 5;

    /// <summary>Timeout in seconds after which pending entries are considered stale (#9).</summary>
    private const int PendingEntryTimeoutSeconds = 30;

    /// <summary>Interval in seconds for cleaning up stale pending entries.</summary>
    private const int CleanupIntervalSeconds = 10;

    private IDnsServer _dnsServer = null!;
    private ConfigService _configService = null!;
    private ServiceRegistry _serviceRegistry = null!;
    private FilteringService _filteringService = null!;
    private ProfileCompiler _profileCompiler = null!;
    private BlockListManager? _blockListManager;
    private Timer? _refreshTimer;
    private Timer? _cleanupTimer;
    private CancellationTokenSource? _cts;

    // #8: Use composite key (requestId + client IP) instead of just 16-bit request ID
    // to avoid collision risk. #9: Include timestamp for TTL-based expiration.
    private readonly ConcurrentDictionary<string, PendingRewrite> _pendingRewrites = new();
    private readonly ConcurrentDictionary<string, PendingBlock> _pendingBlocks = new();

    private DateTime? _lastBlockListRefresh;

    public string Description => "Parental controls with per-client profiles, blocked services, DNS rewrites, and time-based schedules.";

    public Task InitializeAsync(IDnsServer dnsServer, string config)
    {
        // Dispose previous timer/manager on re-init (Technitium calls this on every config reload)
        _cts?.Cancel();
        _cts?.Dispose();
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        _blockListManager?.Dispose();

        _cts = new CancellationTokenSource();
        _dnsServer = dnsServer;

        _serviceRegistry ??= new ServiceRegistry();
        _serviceRegistry.ExportToAppFolder(dnsServer.ApplicationFolder);
        _configService ??= new ConfigService(dnsServer.ApplicationFolder);
        _filteringService ??= new FilteringService(_configService);

        _blockListManager = new BlockListManager(dnsServer.ApplicationFolder, msg => _dnsServer.WriteLog($"ContentFilter: {msg}"));
        _profileCompiler = new ProfileCompiler(_serviceRegistry, _blockListManager);

        _configService.Load(config);
        _dnsServer.WriteLog("ContentFilter: config loaded.");
        ApplyConfig();

        // #10: Capture CTS token for timer callbacks to check cancellation
        var ct = _cts.Token;

        // Background refresh every RefreshIntervalMinutes (individual lists respect their own refreshHours)
        _refreshTimer = new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;
            _ = RefreshBlockListsAsync(ct);
        }, null, TimeSpan.FromSeconds(InitialDelaySeconds), TimeSpan.FromMinutes(RefreshIntervalMinutes));

        // #9: Periodic cleanup of stale pending entries
        _cleanupTimer = new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;
            CleanupStalePendingEntries();
        }, null, TimeSpan.FromSeconds(CleanupIntervalSeconds), TimeSpan.FromSeconds(CleanupIntervalSeconds));

        _dnsServer.WriteLog("ContentFilter initialized.");
        return Task.CompletedTask;
    }

    public Task<bool> IsAllowedAsync(DnsDatagram request, IPEndPoint remoteEP)
    {
        if (request.Question.Count == 0)
            return Task.FromResult(true);

        var domain = request.Question[0].Name;
        var result = _filteringService.Evaluate(request, remoteEP, domain);

        switch (result.Action)
        {
            case FilterAction.Rewrite:
            {
                var key = MakePendingKey(request.Identifier, remoteEP.Address);
                _pendingRewrites[key] = new PendingRewrite(result.Rewrite!, DateTime.UtcNow);
                _dnsServer.WriteLog($"ContentFilter: REWRITE {domain} -> {result.Rewrite!.Answer} | {result.DebugSummary}");
                return Task.FromResult(false);
            }
            case FilterAction.Block:
            {
                var key = MakePendingKey(request.Identifier, remoteEP.Address);
                _pendingBlocks[key] = new PendingBlock(result, DateTime.UtcNow);
                _dnsServer.WriteLog($"ContentFilter: BLOCKED {domain} | {result.DebugSummary}");
                return Task.FromResult(false);
            }
            default:
                return Task.FromResult(true);
        }
    }

    public Task<DnsDatagram> ProcessRequestAsync(DnsDatagram request, IPEndPoint remoteEP)
    {
        // #8: Use composite key for pending lookup
        var key = MakePendingKey(request.Identifier, remoteEP.Address);

        // Check pending rewrites first
        if (_pendingRewrites.TryRemove(key, out var pendingRewrite))
        {
            return Task.FromResult(BuildRewriteResponse(request, pendingRewrite.Rewrite));
        }

        // Check pending blocks for diagnostic response
        _pendingBlocks.TryRemove(key, out var pendingBlock);
        return Task.FromResult(BuildBlockResponse(request, pendingBlock?.Result));
    }

    /// <summary>
    /// #27: Returns a snapshot of plugin health/status information.
    /// </summary>
    public PluginStatus GetStatus()
    {
        return new PluginStatus
        {
            IsInitialized = _filteringService is not null,
            LoadedProfileCount = _filteringService?.CompiledProfileCount ?? 0,
            LastBlockListRefresh = _lastBlockListRefresh,
            BlockListStatuses = _blockListManager?.GetAllStatus() ?? new Dictionary<string, BlockListStatus>(),
            PendingRewriteCount = _pendingRewrites.Count,
            PendingBlockCount = _pendingBlocks.Count
        };
    }

    internal DnsDatagram BuildBlockResponse(DnsDatagram request, FilterResult? result)
    {
        var config = _configService.Config;
        string? report = null;

        if (config.AllowTxtBlockingReport && result?.BlockReason is not null)
        {
            report = BuildBlockingReport(result);
        }

        // TXT query + report -> NoError with TXT answer
        if (report is not null && request.Question.Count > 0
            && request.Question[0].Type == DnsResourceRecordType.TXT)
        {
            var question = request.Question[0];
            var txtRecord = new DnsResourceRecord(
                question.Name, DnsResourceRecordType.TXT, DnsClass.IN,
                DefaultTtlSeconds, new DnsTXTRecordData(report));

            return new DnsDatagram(
                request.Identifier, true, DnsOpcode.StandardQuery, false, false,
                request.RecursionDesired, true, false, false,
                DnsResponseCode.NoError, request.Question, new[] { txtRecord });
        }

        // Blocking address path: return NoError with address records instead of NXDOMAIN
        var addresses = result?.BlockingAddresses;
        if (addresses is not null && !addresses.IsEmpty && request.Question.Count > 0)
        {
            var records = BuildAddressRecords(request.Question[0], addresses, BlockingTtlSeconds);

            if (report is not null && request.EDNS is not null)
            {
                var edeOption = new EDnsOption(
                    EDnsOptionCode.EXTENDED_DNS_ERROR,
                    new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.Blocked, report));

                return new DnsDatagram(
                    request.Identifier, true, DnsOpcode.StandardQuery, false, false,
                    request.RecursionDesired, true, false, false,
                    DnsResponseCode.NoError, request.Question,
                    records, null, null,
                    _dnsServer.UdpPayloadSize, EDnsHeaderFlags.None, new[] { edeOption });
            }

            return new DnsDatagram(
                request.Identifier, true, request.OPCODE, true, false,
                request.RecursionDesired, true, false, false,
                DnsResponseCode.NoError, request.Question, records);
        }

        // Non-TXT + EDNS client + report -> NXDOMAIN with EDE code 15
        if (report is not null && request.EDNS is not null)
        {
            var edeOption = new EDnsOption(
                EDnsOptionCode.EXTENDED_DNS_ERROR,
                new EDnsExtendedDnsErrorOptionData(EDnsExtendedDnsErrorCode.Blocked, report));

            return new DnsDatagram(
                request.Identifier, true, DnsOpcode.StandardQuery, false, false,
                request.RecursionDesired, true, false, false,
                DnsResponseCode.NxDomain, request.Question,
                null, null, null,
                _dnsServer.UdpPayloadSize, EDnsHeaderFlags.None, new[] { edeOption });
        }

        // Default: plain NXDOMAIN
        return new DnsDatagram(
            request.Identifier, true, request.OPCODE, true, false,
            request.RecursionDesired, true, false, false,
            DnsResponseCode.NxDomain, request.Question);
    }

    private static string BuildBlockingReport(FilterResult result)
    {
        var report = $"source=content-filter; domain={result.QuestionDomain}; profile={result.ProfileName}";

        if (result.BlockReason!.Source == BlockSource.DomainBlocklist)
            report += $"; matchedDomain={result.BlockReason.MatchedDomain}";
        else
            report += $"; regex={result.BlockReason.MatchedRegex}";

        return report;
    }

    internal static List<DnsResourceRecord> BuildAddressRecords(
        DnsQuestionRecord question, BlockingAddressSet addresses, uint ttl)
    {
        var records = new List<DnsResourceRecord>();

        if (addresses.DomainNames.Length > 0)
        {
            // CNAME records for any query type
            foreach (var domain in addresses.DomainNames)
            {
                records.Add(new DnsResourceRecord(
                    question.Name, DnsResourceRecordType.CNAME, DnsClass.IN, ttl,
                    new DnsCNAMERecordData(domain)));
            }
        }
        else
        {
            // A records for A/ANY queries
            if (question.Type is DnsResourceRecordType.A or DnsResourceRecordType.ANY)
            {
                foreach (var ip in addresses.IPv4Addresses)
                {
                    records.Add(new DnsResourceRecord(
                        question.Name, DnsResourceRecordType.A, DnsClass.IN, ttl,
                        new DnsARecordData(ip)));
                }
            }

            // AAAA records for AAAA/ANY queries
            if (question.Type is DnsResourceRecordType.AAAA or DnsResourceRecordType.ANY)
            {
                foreach (var ip in addresses.IPv6Addresses)
                {
                    records.Add(new DnsResourceRecord(
                        question.Name, DnsResourceRecordType.AAAA, DnsClass.IN, ttl,
                        new DnsAAAARecordData(ip)));
                }
            }
        }

        return records;
    }

    private static DnsDatagram BuildRewriteResponse(DnsDatagram request, DnsRewriteConfig rewrite)
    {
        var question = request.Question[0];
        var answer = rewrite.Answer.Trim();
        var records = new List<DnsResourceRecord>();

        if (IPAddress.TryParse(answer, out var ip))
        {
            // IP rewrites are type-matched: A record only for A/ANY queries,
            // AAAA only for AAAA/ANY queries. Mismatched types produce an empty
            // answer section (NODATA response), which is correct per RFC 1035.
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && question.Type is DnsResourceRecordType.A or DnsResourceRecordType.ANY)
            {
                records.Add(new DnsResourceRecord(
                    question.Name, DnsResourceRecordType.A, DnsClass.IN, DefaultTtlSeconds,
                    new DnsARecordData(ip)));
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && question.Type is DnsResourceRecordType.AAAA or DnsResourceRecordType.ANY)
            {
                records.Add(new DnsResourceRecord(
                    question.Name, DnsResourceRecordType.AAAA, DnsClass.IN, DefaultTtlSeconds,
                    new DnsAAAARecordData(ip)));
            }
        }
        else
        {
            // Non-IP answers are treated as CNAME regardless of query type
            records.Add(new DnsResourceRecord(
                question.Name, DnsResourceRecordType.CNAME, DnsClass.IN, DefaultTtlSeconds,
                new DnsCNAMERecordData(answer)));
        }

        return new DnsDatagram(
            request.Identifier,
            true,
            request.OPCODE,
            true,
            false,
            request.RecursionDesired,
            true,
            false,
            false,
            DnsResponseCode.NoError,
            request.Question,
            records);
    }

    private void ApplyConfig()
    {
        var config = _configService.Config;
        _serviceRegistry.MergeCustomServices(config.CustomServices);
        var compiled = _profileCompiler.CompileAll(config);
        _filteringService.UpdateCompiledProfiles(compiled);
        _dnsServer.WriteLog($"ContentFilter: compiled {compiled.Count} profiles.");
    }

    private async Task RefreshBlockListsAsync(CancellationToken ct)
    {
        // #24: Wrap in try-catch to prevent uncaught failures from crashing the timer callback
        try
        {
            if (ct.IsCancellationRequested) return;

            var globalBlockLists = _configService.Config.BlockLists;

            if (globalBlockLists.Count == 0)
                return;

            _dnsServer.WriteLog("ContentFilter: starting blocklist refresh.");
            await _blockListManager!.RefreshAsync(globalBlockLists);
            _lastBlockListRefresh = DateTime.UtcNow;

            if (ct.IsCancellationRequested) return;

            // Recompile profiles with updated blocklist data
            var compiled = _profileCompiler.CompileAll(_configService.Config);
            _filteringService.UpdateCompiledProfiles(compiled);
            _dnsServer.WriteLog($"ContentFilter: blocklist refresh complete, recompiled {compiled.Count} profiles.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown, ignore
        }
        catch (Exception ex)
        {
            _dnsServer.WriteLog($"ContentFilter: blocklist refresh error: {ex.Message}");
        }
    }

    /// <summary>
    /// #9: Remove pending entries that are older than the timeout threshold.
    /// </summary>
    private void CleanupStalePendingEntries()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-PendingEntryTimeoutSeconds);
        foreach (var kvp in _pendingRewrites)
        {
            if (kvp.Value.CreatedUtc < cutoff)
            {
                _pendingRewrites.TryRemove(kvp.Key, out _);
            }
        }
        foreach (var kvp in _pendingBlocks)
        {
            if (kvp.Value.CreatedUtc < cutoff)
            {
                _pendingBlocks.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>
    /// #8: Creates a composite key from request ID and client IP to avoid 16-bit collision risk.
    /// </summary>
    private static string MakePendingKey(ushort requestId, IPAddress clientIp)
    {
        return $"{requestId}:{clientIp}";
    }

    public void Dispose()
    {
        // #10: Cancel token before disposing timers to prevent race conditions
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        _blockListManager?.Dispose();
        _blockListManager = null;
        cts?.Dispose();
    }

    // #9: Internal records to track creation time of pending entries
    private sealed record PendingRewrite(DnsRewriteConfig Rewrite, DateTime CreatedUtc);
    private sealed record PendingBlock(FilterResult Result, DateTime CreatedUtc);
}
