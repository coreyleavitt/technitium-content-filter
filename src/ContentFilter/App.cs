using System.Collections.Concurrent;
using System.Net;
using DnsServerCore.ApplicationCommon;
using ContentFilter.Models;
using ContentFilter.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter;

public sealed class App : IDnsApplication, IDnsRequestBlockingHandler
{
    /// <summary>Default TTL in seconds for rewrite DNS responses.</summary>
    internal const uint DefaultTtlSeconds = 300;

    /// <summary>Background blocklist refresh interval in minutes.</summary>
    internal const int RefreshIntervalMinutes = 15;

    /// <summary>Initial delay before first blocklist refresh in seconds.</summary>
    internal const int InitialDelaySeconds = 5;

    /// <summary>Timeout in seconds after which pending rewrite entries are considered stale (#9).</summary>
    private const int PendingRewriteTimeoutSeconds = 30;

    /// <summary>Interval in seconds for cleaning up stale pending rewrite entries.</summary>
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

        // #9: Periodic cleanup of stale pending rewrite entries
        _cleanupTimer = new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;
            CleanupStalePendingRewrites();
        }, null, TimeSpan.FromSeconds(CleanupIntervalSeconds), TimeSpan.FromSeconds(CleanupIntervalSeconds));

        _dnsServer.WriteLog("ContentFilter initialized.");
        return Task.CompletedTask;
    }

    public Task<bool> IsAllowedAsync(DnsDatagram request, IPEndPoint remoteEP)
    {
        if (request.Question.Count == 0)
            return Task.FromResult(true);

        var domain = request.Question[0].Name;
        var allowed = _filteringService.IsAllowed(request, remoteEP, domain, out var debugInfo, out var rewrite);

        if (rewrite is not null)
        {
            // #8: Use composite key for pending rewrites
            var key = MakePendingKey(request.Identifier, remoteEP.Address);
            _pendingRewrites[key] = new PendingRewrite(rewrite, DateTime.UtcNow);
            _dnsServer.WriteLog($"ContentFilter: REWRITE {domain} -> {rewrite.Answer} | {debugInfo}");
            return Task.FromResult(false);
        }

        if (!allowed)
            _dnsServer.WriteLog($"ContentFilter: BLOCKED {domain} | {debugInfo}");

        return Task.FromResult(allowed);
    }

    public Task<DnsDatagram> ProcessRequestAsync(DnsDatagram request, IPEndPoint remoteEP)
    {
        // #8: Use composite key for pending rewrite lookup
        var key = MakePendingKey(request.Identifier, remoteEP.Address);
        if (_pendingRewrites.TryRemove(key, out var pending))
        {
            return Task.FromResult(BuildRewriteResponse(request, pending.Rewrite));
        }

        // Default: NxDomain for blocked queries
        return Task.FromResult(new DnsDatagram(
            request.Identifier,
            true,
            request.OPCODE,
            true,
            false,
            request.RecursionDesired,
            true,
            false,
            false,
            DnsResponseCode.NxDomain,
            request.Question));
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
            PendingRewriteCount = _pendingRewrites.Count
        };
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
    /// #9: Remove pending rewrite entries that are older than the timeout threshold.
    /// </summary>
    private void CleanupStalePendingRewrites()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-PendingRewriteTimeoutSeconds);
        foreach (var kvp in _pendingRewrites)
        {
            if (kvp.Value.CreatedUtc < cutoff)
            {
                _pendingRewrites.TryRemove(kvp.Key, out _);
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

    // #9: Internal record to track creation time of pending rewrites
    private sealed record PendingRewrite(DnsRewriteConfig Rewrite, DateTime CreatedUtc);
}
