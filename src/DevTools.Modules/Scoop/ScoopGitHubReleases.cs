using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevTools.Modules.Scoop;

public sealed record ScoopGitHubAsset(string Name, string Url, long Size)
{
    public override string ToString() => Name;
}
public sealed record ScoopGitHubRelease(string Tag, string Name, IReadOnlyList<ScoopGitHubAsset> Assets)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) || Name == Tag ? Tag : $"{Name} ({Tag})";
}

/// <summary>Reads public release metadata; never executes or downloads release assets.</summary>
public static class ScoopGitHubReleases
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Regex Segment = new(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);

    public static (string Owner, string Repository) ParseRepository(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Enter an HTTPS GitHub repository URL, such as https://github.com/owner/repository.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(p => !Segment.IsMatch(p) || p is "." or ".."))
            throw new ArgumentException("Enter a GitHub repository URL with an owner and repository name.");
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (repo.Length == 0) throw new ArgumentException("Enter a GitHub repository name.");
        return (parts[0], repo);
    }

    public static async Task<IReadOnlyList<ScoopGitHubRelease>> GetAsync(string repositoryUrl, CancellationToken ct)
        => await GetAsync(repositoryUrl, Client, ct);

    public static async Task<IReadOnlyList<ScoopGitHubRelease>> GetAsync(string repositoryUrl, HttpClient client, CancellationToken ct)
    {
        var (owner, repo) = ParseRepository(repositoryUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=30");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevTools-Studio", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Repository not found or releases are not publicly accessible.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new InvalidOperationException("GitHub denied the request or its rate limit was reached. Try again later.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException("GitHub release response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("GitHub release response is too large.");
            buffer.Write(bytes, 0, count);
        }
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub returned an invalid release list.");
        var releases = new List<ScoopGitHubRelease>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || (entry.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)) continue;
            var tag = String(entry, "tag_name");
            if (tag.Length == 0) continue;
            var assets = new List<ScoopGitHubAsset>();
            if (entry.TryGetProperty("assets", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = String(item, "name"); var url = String(item, "browser_download_url");
                    if (name.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var assetUri) || assetUri.Scheme != Uri.UriSchemeHttps || !assetUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) continue;
                    var size = item.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var n) ? n : 0;
                    assets.Add(new(name, assetUri.AbsoluteUri, size));
                }
            releases.Add(new(tag, String(entry, "name"), assets));
        }
        return releases;
    }
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
