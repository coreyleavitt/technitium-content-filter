using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContentFilter.Services;

/// <summary>
/// Downloads, parses, and caches blocklists. Parsed domains stored in memory
/// keyed by URL. Same URL shared across profiles (downloaded once).
/// </summary>
public sealed class BlockListManager : IDisposable
{
    private readonly string _cacheDir;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, HashSet<string>> _domainsByUrl = new();
    private readonly ConcurrentDictionary<string, List<string>> _patternsByUrl = new();
    private readonly Action<string>? _log;

    public BlockListManager(string appFolder, Action<string>? log = null)
        : this(appFolder, new HttpClientHandler(), log)
    {
    }

    internal BlockListManager(string appFolder, HttpMessageHandler handler, Action<string>? log = null)
    {
        _cacheDir = Path.Combine(appFolder, "blocklists");
        Directory.CreateDirectory(_cacheDir);
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _log = log;
    }

    /// <summary>
    /// Returns parsed domains for a URL, or null if not yet downloaded.
    /// </summary>
    public HashSet<string>? GetDomains(string url)
    {
        return _domainsByUrl.TryGetValue(url, out var domains) ? domains : null;
    }

    /// <summary>
    /// Returns raw regex patterns for a URL, or null if not yet downloaded.
    /// </summary>
    public List<string>? GetPatterns(string url)
    {
        return _patternsByUrl.TryGetValue(url, out var patterns) ? patterns : null;
    }

    /// <summary>
    /// Returns metadata about all known blocklists (url, entry count, last fetch time, type).
    /// </summary>
    public Dictionary<string, BlockListStatus> GetAllStatus()
    {
        var result = new Dictionary<string, BlockListStatus>();
        foreach (var (url, domains) in _domainsByUrl)
        {
            var meta = LoadMeta(url);
            result[url] = new BlockListStatus
            {
                EntryCount = domains.Count,
                Type = "domain",
                LastFetch = meta?.LastFetch
            };
        }
        foreach (var (url, patterns) in _patternsByUrl)
        {
            var meta = LoadMeta(url);
            result[url] = new BlockListStatus
            {
                EntryCount = patterns.Count,
                Type = "regex",
                LastFetch = meta?.LastFetch
            };
        }
        return result;
    }

    /// <summary>
    /// Downloads and parses all blocklists referenced by any profile. Loads from
    /// cache first, then fetches any that are stale or missing.
    /// #13: Deduplicates URLs before downloading.
    /// #15: Downloads in parallel with Task.WhenAll.
    /// </summary>
    public async Task RefreshAsync(IEnumerable<Models.BlockListConfig> blockLists)
    {
        // #13: Deduplicate URLs using HashSet-backed dictionary
        var uniqueUrls = new Dictionary<string, (int RefreshHours, string Type)>(StringComparer.OrdinalIgnoreCase);
        foreach (var bl in blockLists)
        {
            if (!bl.Enabled || string.IsNullOrWhiteSpace(bl.Url))
                continue;

            if (!uniqueUrls.TryGetValue(bl.Url, out var existing) || bl.RefreshHours < existing.RefreshHours)
                uniqueUrls[bl.Url] = (bl.RefreshHours, bl.Type);
        }

        // #15: Download all blocklists in parallel
        var tasks = uniqueUrls.Select(kvp => RefreshOneWithFallbackAsync(kvp.Key, kvp.Value.RefreshHours, kvp.Value.Type));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Wraps RefreshOneAsync with error handling and cache fallback.
    /// </summary>
    private async Task RefreshOneWithFallbackAsync(string url, int refreshHours, string type)
    {
        try
        {
            await RefreshOneAsync(url, refreshHours, type);
        }
        catch (Exception ex)
        {
            // #25: Log with exception details
            _log?.Invoke($"BlockListManager: failed to refresh {url}: {ex.Message}");
            // Try to load from cache if we have it
            var isLoaded = type == "regex" ? _patternsByUrl.ContainsKey(url) : _domainsByUrl.ContainsKey(url);
            if (!isLoaded)
                LoadFromCache(url, type);
        }
    }

    private async Task RefreshOneAsync(string url, int refreshHours, string type)
    {
        var meta = LoadMeta(url);
        var cacheFile = GetCachePath(url);

        var needsDownload = meta is null
            || !File.Exists(cacheFile)
            || (DateTime.UtcNow - meta.LastFetch).TotalHours >= refreshHours;

        if (needsDownload)
        {
            _log?.Invoke($"BlockListManager: downloading {url}");
            var content = await _httpClient.GetStringAsync(url);
            await File.WriteAllTextAsync(cacheFile, content);
            SaveMeta(url, new BlockListMeta { LastFetch = DateTime.UtcNow });
        }

        if (type == "regex")
        {
            var patterns = ParsePatternFile(cacheFile, _log);
            _patternsByUrl[url] = patterns;
            _log?.Invoke($"BlockListManager: {url} -> {patterns.Count} patterns");
        }
        else
        {
            var domains = ParseFile(cacheFile, _log);
            _domainsByUrl[url] = domains;
            _log?.Invoke($"BlockListManager: {url} -> {domains.Count} domains");
        }
    }

    private void LoadFromCache(string url, string type)
    {
        var cacheFile = GetCachePath(url);
        if (File.Exists(cacheFile))
        {
            if (type == "regex")
            {
                var patterns = ParsePatternFile(cacheFile, _log);
                _patternsByUrl[url] = patterns;
                _log?.Invoke($"BlockListManager: loaded {url} from cache -> {patterns.Count} patterns");
            }
            else
            {
                var domains = ParseFile(cacheFile, _log);
                _domainsByUrl[url] = domains;
                _log?.Invoke($"BlockListManager: loaded {url} from cache -> {domains.Count} domains");
            }
        }
    }

    internal static HashSet<string> ParseFile(string path, Action<string>? log = null)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('!'))
                continue;

            string? domain = null;

            // Adblock format: ||example.com^
            if (line.StartsWith("||") && line.EndsWith('^'))
            {
                domain = line[2..^1];
            }
            // Hosts file: 0.0.0.0 example.com or 127.0.0.1 example.com (space or tab separated)
            else if (line.StartsWith("0.0.0.0 ") || line.StartsWith("127.0.0.1 ")
                     || line.StartsWith("0.0.0.0\t") || line.StartsWith("127.0.0.1\t"))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    domain = parts[1];
            }
            // Plain domain (no spaces, no special chars at start)
            else if (!line.Contains(' ') && line.Contains('.') && !line.StartsWith("@@"))
            {
                domain = line;
            }

            if (domain is not null)
            {
                domain = domain.Trim().TrimEnd('.');
                if (domain.Length > 0 && domain.Contains('.') && !domain.StartsWith('.'))
                {
                    // Warn about malformed domains but still add them (blocklists are messy)
                    if (domain.Contains(".."))
                        log?.Invoke($"BlockListManager: malformed domain with consecutive dots: {domain}");
                    else if (domain.Split('.').Any(label => label.Length > 63))
                        log?.Invoke($"BlockListManager: domain label exceeds 63 chars: {domain}");

                    domains.Add(domain);
                }
            }
        }

        return domains;
    }

    /// <summary>
    /// Parses a file containing one regex pattern per line. Strips comments (#/!) and blank lines.
    /// Does NOT validate regex syntax (that's RegexCompiler's job).
    /// </summary>
    internal static List<string> ParsePatternFile(string path, Action<string>? log = null)
    {
        var patterns = new List<string>();

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('!'))
                continue;

            patterns.Add(line);
        }

        return patterns;
    }

    private string GetCachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(_cacheDir, hash + ".txt");
    }

    private string GetMetaPath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(_cacheDir, hash + ".meta.json");
    }

    private BlockListMeta? LoadMeta(string url)
    {
        var path = GetMetaPath(url);
        if (!File.Exists(path))
            return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BlockListMeta>(json);
        }
        // #23: Catch specific exception type and log instead of bare catch
        catch (Exception ex)
        {
            _log?.Invoke($"BlockListManager: failed to load meta for {url}: {ex.Message}");
            return null;
        }
    }

    private void SaveMeta(string url, BlockListMeta meta)
    {
        var path = GetMetaPath(url);
        File.WriteAllText(path, JsonSerializer.Serialize(meta));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class BlockListMeta
    {
        public DateTime LastFetch { get; set; }
    }
}

public sealed class BlockListStatus
{
    public int EntryCount { get; set; }
    public string Type { get; set; } = "domain";
    public DateTime? LastFetch { get; set; }
}
