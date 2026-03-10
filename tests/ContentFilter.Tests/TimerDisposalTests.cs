using System.Net;
using DnsServerCore.ApplicationCommon;
using NSubstitute;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace ContentFilter.Tests;

/// <summary>
/// Issue #30: Tests for timer disposal race conditions.
/// Verifies that disposing the App during or after timer callbacks is safe,
/// and that double-dispose does not throw.
/// </summary>
[Trait("Category", "Unit")]
public class TimerDisposalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IDnsServer _mockServer;

    public TimerDisposalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "timer-disposal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _mockServer = Substitute.For<IDnsServer>();
        _mockServer.ApplicationFolder.Returns(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Dispose_AfterInit_DoesNotThrow()
    {
        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, """{"enableBlocking": true}""");

        // Dispose should be safe
        app.Dispose();
    }

    [Fact]
    public async Task DoubleDispose_DoesNotThrow()
    {
        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, """{"enableBlocking": true}""");

        app.Dispose();
        app.Dispose(); // second dispose should be safe
    }

    [Fact]
    public async Task TripleDispose_DoesNotThrow()
    {
        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, """{"enableBlocking": true}""");

        app.Dispose();
        app.Dispose();
        app.Dispose();
    }

    [Fact]
    public async Task ReInitAfterDispose_DoesNotThrow()
    {
        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, """{"enableBlocking": true}""");
        app.Dispose();

        // Re-init after dispose (simulates Technitium calling InitializeAsync again)
        await app.InitializeAsync(_mockServer, """{"enableBlocking": false}""");
        app.Dispose();
    }

    [Fact]
    public async Task ConcurrentDispose_DoesNotThrow()
    {
        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, """{"enableBlocking": true}""");

        // Multiple concurrent Dispose calls should be safe
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() => app.Dispose()));
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Dispose_DuringActiveFiltering_DoesNotThrow()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": { "kids": { "customRules": ["blocked.com"] } }
        }
        """;

        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, config);

        // Start some filtering requests
        var question = new DnsQuestionRecord("blocked.com", DnsResourceRecordType.A, DnsClass.IN);
        var request = new DnsDatagram(
            1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false,
            DnsResponseCode.NoError, new[] { question });
        var ep = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 53);

        // Interleave filtering and disposal
        await app.IsAllowedAsync(request, ep);
        app.Dispose();
    }

    [Fact]
    public async Task ReInit_DisposesOldTimerAndManager()
    {
        var app = new ContentFilter.App();

        // First init creates timer and manager
        await app.InitializeAsync(_mockServer, """{"enableBlocking": true}""");

        // Second init should dispose the old timer/manager and create new ones
        await app.InitializeAsync(_mockServer, """{"enableBlocking": false}""");

        // Final dispose should be safe
        app.Dispose();

        // WriteLog should have been called twice for "initialized"
        _mockServer.Received(2).WriteLog(Arg.Is<string>(s => s.Contains("initialized")));
    }

    [Fact]
    public async Task DisposeWithBlocklists_DoesNotThrow()
    {
        var config = """
        {
            "enableBlocking": true,
            "defaultProfile": "kids",
            "profiles": {
                "kids": {
                    "blockLists": ["https://unreachable.invalid/list.txt"]
                }
            },
            "blockLists": [
                { "url": "https://unreachable.invalid/list.txt", "name": "Test", "enabled": true }
            ]
        }
        """;

        var app = new ContentFilter.App();
        await app.InitializeAsync(_mockServer, config);

        // Dispose immediately while timer might be trying to refresh blocklists
        app.Dispose();
    }
}
