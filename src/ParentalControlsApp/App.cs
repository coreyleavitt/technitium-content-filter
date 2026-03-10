using System.Collections.Concurrent;
using System.Net;
using DnsServerCore.ApplicationCommon;
using ParentalControlsApp.Models;
using ParentalControlsApp.Services;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ParentalControlsApp;

public sealed class App : IDnsApplication, IDnsRequestBlockingHandler
{
    private IDnsServer _dnsServer = null!;
    private ConfigService _configService = null!;
    private ServiceRegistry _serviceRegistry = null!;
    private FilteringService _filteringService = null!;
    private ProfileCompiler _profileCompiler = null!;
    private BlockListManager? _blockListManager;
    private Timer? _refreshTimer;

    // Keyed by DNS message ID (16-bit). Technitium assigns random IDs, so collision
    // risk is negligible under normal load. Each entry is consumed atomically via
    // TryRemove in ProcessRequestAsync.
    private readonly ConcurrentDictionary<ushort, DnsRewriteConfig> _pendingRewrites = new();

    public string Description => "Parental controls with per-client profiles, blocked services, DNS rewrites, and time-based schedules.";

    public Task InitializeAsync(IDnsServer dnsServer, string config)
    {
        // Dispose previous timer/manager on re-init (Technitium calls this on every config reload)
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _blockListManager?.Dispose();

        _dnsServer = dnsServer;

        _serviceRegistry ??= new ServiceRegistry();
        _serviceRegistry.ExportToAppFolder(dnsServer.ApplicationFolder);
        _configService ??= new ConfigService(dnsServer.ApplicationFolder);
        _filteringService ??= new FilteringService(_configService);

        _blockListManager = new BlockListManager(dnsServer.ApplicationFolder, msg => _dnsServer.WriteLog($"ParentalControlsApp: {msg}"));
        _profileCompiler = new ProfileCompiler(_serviceRegistry, _blockListManager);

        _configService.Load(config);
        ApplyConfig();

        // Background refresh every 15 minutes (individual lists respect their own refreshHours)
        _refreshTimer = new Timer(_ => _ = RefreshBlockListsAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15));

        _dnsServer.WriteLog("ParentalControlsApp initialized.");
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
            _pendingRewrites[request.Identifier] = rewrite;
            _dnsServer.WriteLog($"ParentalControlsApp: REWRITE {domain} -> {rewrite.Answer} | {debugInfo}");
            return Task.FromResult(false);
        }

        if (!allowed)
            _dnsServer.WriteLog($"ParentalControlsApp: BLOCKED {domain} | {debugInfo}");

        return Task.FromResult(allowed);
    }

    public Task<DnsDatagram> ProcessRequestAsync(DnsDatagram request, IPEndPoint remoteEP)
    {
        // Check for pending rewrite
        if (_pendingRewrites.TryRemove(request.Identifier, out var rewrite))
        {
            return Task.FromResult(BuildRewriteResponse(request, rewrite));
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
                    question.Name, DnsResourceRecordType.A, DnsClass.IN, 300,
                    new DnsARecordData(ip)));
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && question.Type is DnsResourceRecordType.AAAA or DnsResourceRecordType.ANY)
            {
                records.Add(new DnsResourceRecord(
                    question.Name, DnsResourceRecordType.AAAA, DnsClass.IN, 300,
                    new DnsAAAARecordData(ip)));
            }
        }
        else
        {
            // Non-IP answers are treated as CNAME regardless of query type
            records.Add(new DnsResourceRecord(
                question.Name, DnsResourceRecordType.CNAME, DnsClass.IN, 300,
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
    }

    private async Task RefreshBlockListsAsync()
    {
        try
        {
            var globalBlockLists = _configService.Config.BlockLists;

            if (globalBlockLists.Count == 0)
                return;

            await _blockListManager!.RefreshAsync(globalBlockLists);

            // Recompile profiles with updated blocklist data
            var compiled = _profileCompiler.CompileAll(_configService.Config);
            _filteringService.UpdateCompiledProfiles(compiled);
        }
        catch (Exception ex)
        {
            _dnsServer.WriteLog($"ParentalControlsApp: blocklist refresh error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _blockListManager?.Dispose();
    }
}
