using System.Reflection;
using System.Text.Json;
using ParentalControlsApp.Models;

namespace ParentalControlsApp.Services;

/// <summary>
/// Loads known service definitions (YouTube, TikTok, etc.) from the embedded
/// blocked-services.json, merges with custom services from config, and provides
/// fast domain-to-service lookups.
/// </summary>
public sealed class ServiceRegistry
{
    private readonly Dictionary<string, BlockedServiceDefinition> _embeddedServices;
    private Dictionary<string, BlockedServiceDefinition> _services;
    private Dictionary<string, string> _domainToServiceId;

    public IReadOnlyDictionary<string, BlockedServiceDefinition> Services => _services;

    public ServiceRegistry()
    {
        _embeddedServices = LoadEmbeddedServices();
        _services = new Dictionary<string, BlockedServiceDefinition>(_embeddedServices, StringComparer.OrdinalIgnoreCase);
        _domainToServiceId = BuildDomainIndex(_services);
    }

    /// <summary>
    /// Merges custom services from config with embedded services. Custom services
    /// with the same ID as an embedded service override it. Rebuilds the domain index.
    /// </summary>
    public void MergeCustomServices(Dictionary<string, BlockedServiceDefinition> customServices)
    {
        var merged = new Dictionary<string, BlockedServiceDefinition>(_embeddedServices, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, svc) in customServices)
            merged[id] = svc;

        _services = merged;
        _domainToServiceId = BuildDomainIndex(merged);
    }

    /// <summary>
    /// Given a queried domain name, returns the service ID if it matches a known
    /// blocked service, or null if no match. Handles subdomain matching --
    /// "www.youtube.com" matches "youtube.com".
    /// </summary>
    public string? FindServiceForDomain(string domain)
    {
        // Trim trailing FQDN dot; OrdinalIgnoreCase on the dictionary handles case
        var trimmed = domain.AsSpan().TrimEnd('.');

        // Check exact match first, then walk up subdomains
        while (true)
        {
            if (_domainToServiceId.TryGetValue(trimmed.ToString(), out var serviceId))
                return serviceId;

            var dotIndex = trimmed.IndexOf('.');
            if (dotIndex < 0 || dotIndex == trimmed.Length - 1)
                break;

            trimmed = trimmed[(dotIndex + 1)..];
        }

        return null;
    }

    /// <summary>
    /// Exports embedded JSON resources to the app folder so the web UI
    /// can read them from the shared volume (single source of truth).
    /// </summary>
    public void ExportToAppFolder(string appFolder)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var fileName in new[] { "blocked-services.json", "default-blocklists.json" })
        {
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName));
            if (resourceName is null) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            using var fs = File.Create(Path.Combine(appFolder, fileName));
            stream.CopyTo(fs);
        }
    }

    private static Dictionary<string, BlockedServiceDefinition> LoadEmbeddedServices()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("blocked-services.json"))
            ?? throw new InvalidOperationException(
                "Embedded resource 'blocked-services.json' not found. Ensure it is included as an EmbeddedResource in the .csproj.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not load embedded resource stream for '{resourceName}'.");
        return JsonSerializer.Deserialize<Dictionary<string, BlockedServiceDefinition>>(stream)
               ?? new Dictionary<string, BlockedServiceDefinition>();
    }

    private static Dictionary<string, string> BuildDomainIndex(
        Dictionary<string, BlockedServiceDefinition> services)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (serviceId, definition) in services)
        {
            foreach (var domain in definition.Domains)
            {
                index[domain] = serviceId;
            }
        }

        return index;
    }
}
