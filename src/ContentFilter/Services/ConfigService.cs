using System.Text.Json;
using ContentFilter.Models;

namespace ContentFilter.Services;

/// <summary>
/// Handles loading and saving the app configuration JSON.
/// Config is loaded on init via Technitium's InitializeAsync and reloaded
/// when the web UI saves changes through the Technitium API.
///
/// Thread safety: _config is treated as an immutable snapshot. Load() builds
/// a new AppConfig and atomically swaps the reference. Readers grab the
/// reference once and iterate freely without locks.
/// #26: SaveAsync uses SemaphoreSlim to prevent concurrent write corruption.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _configPath;
    private volatile AppConfig _config = new();

    // #26: Semaphore for serializing config writes
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Returns the current config snapshot. Safe to read from any thread --
    /// the returned object is never mutated after construction.
    /// </summary>
    public AppConfig Config => _config;

    public ConfigService(string appFolder)
    {
        _configPath = Path.Combine(appFolder, "dnsApp.config");
    }

    /// <summary>
    /// Deserializes a new AppConfig from the JSON string and atomically
    /// replaces the current snapshot. Previous snapshot remains safe for
    /// any in-flight readers.
    /// </summary>
    public void Load(string configJson)
    {
        var config = string.IsNullOrWhiteSpace(configJson)
            ? new AppConfig()
            : JsonSerializer.Deserialize<AppConfig>(configJson, JsonOptions) ?? new AppConfig();

        _config = config;
    }

    /// <summary>
    /// Atomically saves the current config to disk. Uses temp file + rename to
    /// prevent partial writes.
    /// #26: Thread-safe via SemaphoreSlim to prevent concurrent write corruption.
    /// </summary>
    public async Task SaveAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(_config, JsonOptions);

            // Atomic write: write to temp file, then rename
            var tempPath = _configPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(_config, JsonOptions);
    }
}
