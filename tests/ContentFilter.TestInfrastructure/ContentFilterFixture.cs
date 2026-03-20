using System.Net.Http.Json;
using System.Text.Json;

namespace ContentFilter.TestInfrastructure;

/// <summary>
/// Fixture that installs the ContentFilter plugin from a pre-built ZIP.
/// </summary>
public sealed class ContentFilterFixture : BaseTechnitiumFixture
{
    public override string PluginDisplayName => "ContentFilter";
    protected override string PluginName => "ContentFilter";
    protected override string ClassPath => "ContentFilter.App";

    protected override async Task InstallPluginAsync(HttpClient http, string apiToken)
    {
        var zipPath = FindPluginZip("ContentFilter.zip");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(apiToken), "token");
        form.Add(new StringContent(PluginName), "name");
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(zipPath)), "appZip", "ContentFilter.zip");

        var response = await http.PostAsync("/api/apps/install", form);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (body.GetProperty("status").GetString() != "ok")
            throw new Exception($"Plugin install failed: {body}");
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

        // Walk up from test binary
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "dist", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        throw new FileNotFoundException(
            $"Plugin ZIP '{fileName}' not found. Run the appropriate Docker build first.");
    }
}
