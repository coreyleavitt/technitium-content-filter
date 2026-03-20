using System.Net.Http.Json;
using System.Text.Json;

namespace ContentFilter.TestInfrastructure;

/// <summary>
/// Fixture that installs Technitium's built-in Advanced Blocking plugin from a pre-built ZIP.
/// </summary>
public sealed class AdvancedBlockingFixture : BaseTechnitiumFixture
{
    public override string PluginDisplayName => "Advanced Blocking";
    protected override string PluginName => "Advanced Blocking";
    protected override string ClassPath => "AdvancedBlocking.App";

    protected override async Task InstallPluginAsync(HttpClient http, string apiToken)
    {
        var zipPath = FindPluginZip("AdvancedBlockingApp.zip");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(apiToken), "token");
        form.Add(new StringContent(PluginName), "name");
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)), "appZip", "AdvancedBlockingApp.zip");

        var response = await http.PostAsync("/api/apps/install", form);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (body.GetProperty("status").GetString() != "ok")
            throw new Exception($"Advanced Blocking install failed: {body}");
    }

    private static string FindPluginZip(string fileName)
    {
        var candidates = new[]
        {
            $"/src/app/dist/{fileName}", // Docker build path
        };

        foreach (var path in candidates)
            if (File.Exists(path))
                return path;

        throw new FileNotFoundException(
            $"Plugin ZIP '{fileName}' not found. Build with Dockerfile.perf-comparison first.");
    }
}
