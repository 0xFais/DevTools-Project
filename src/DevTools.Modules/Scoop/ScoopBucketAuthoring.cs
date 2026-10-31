using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevTools.Modules.Scoop;

public static class ScoopBucketAuthoring
{
    public static Uri Https(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) throw new ArgumentException("Enter an HTTPS URL without embedded credentials.");
        return uri;
    }
    public static JsonObject Manifest(string version, string url, string hash, string description, string homepage, string license, string architecture, string executable, string shortcut)
    {
        ScoopService.SafeSegment(version.Trim()); Https(url);
        if (!Regex.IsMatch(hash.Trim(), "^[a-fA-F0-9]{64}$")) throw new ArgumentException("Enter a SHA-256 hash (64 hexadecimal characters).");
        if (homepage.Trim().Length > 0) Https(homepage);
        var node = new JsonObject { ["version"] = version.Trim(), ["description"] = description.Trim(), ["homepage"] = homepage.Trim(), ["license"] = license.Trim() };
        var source = new JsonObject { ["url"] = url.Trim(), ["hash"] = hash.Trim().ToLowerInvariant() };
        if (architecture == "Any") { node["url"] = source["url"]!.DeepClone(); node["hash"] = source["hash"]!.DeepClone(); }
        else if (architecture is "64bit" or "32bit" or "arm64") node["architecture"] = new JsonObject { [architecture] = source };
        else throw new ArgumentException("Choose an architecture.");
        if (executable.Trim().Length > 0) { var exe = executable.Trim().Replace('\\', '/'); if (Path.IsPathRooted(exe) || exe.Split('/').Any(p => p is ".." or "." or "")) throw new ArgumentException("Executable must be a relative path inside the app."); node["bin"] = exe; if (shortcut.Trim().Length > 0) { ScoopService.SafeSegment(shortcut.Trim().Replace(' ', '_')); node["shortcuts"] = new JsonArray(new JsonArray(JsonValue.Create(exe), JsonValue.Create(shortcut.Trim()))); } }
        else if (shortcut.Trim().Length > 0) throw new ArgumentException("Enter an executable to create a shortcut.");
        return node;
    }
    // These checks are deliberately advisory: Scoop manifests support advanced formats and scripts.
    public static IReadOnlyList<string> Inspect(JsonObject manifest)
    {
        var findings = new List<string>();
        static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var value) ? value : "";
        if (string.IsNullOrWhiteSpace(Text(manifest["version"]))) findings.Add("ERROR: A version is required.");
        if (string.IsNullOrWhiteSpace(Text(manifest["description"]))) findings.Add("TIP: Add a short description so users can identify the app.");
        if (string.IsNullOrWhiteSpace(Text(manifest["homepage"]))) findings.Add("TIP: Add the official homepage.");
        void Source(JsonObject source, string label)
        {
            var url = source["url"];
            if (url is null) { findings.Add($"ERROR: {label} has no download URL."); return; }
            var urls = url is JsonArray arr ? arr.Select(Text).ToArray() : new[] { Text(url) };
            if (urls.Length == 0 || urls.Any(string.IsNullOrWhiteSpace)) findings.Add($"WARNING: {label} has a URL format that needs manual review.");
            foreach (var value in urls.Where(x => x.Length > 0))
                if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo)) findings.Add($"WARNING: {label} contains a URL that is not a plain HTTPS link.");
            var hash = source["hash"];
            if (hash is null || string.IsNullOrWhiteSpace(hash.ToJsonString().Trim('"', '[', ']', ' '))) findings.Add($"WARNING: {label} has no checksum. Verify the download before installation.");
            else if (hash is JsonArray hashes && urls.Length != hashes.Count) findings.Add($"WARNING: {label} has a different number of URLs and checksums.");
        }
        var architectures = manifest["architecture"] as JsonObject;
        if (manifest["url"] is not null) Source(manifest, "Default download");
        if (architectures is not null)
            foreach (var pair in architectures) if (pair.Value is JsonObject source) Source(source, pair.Key); else findings.Add($"WARNING: {pair.Key} architecture needs manual review.");
        if (manifest["url"] is null && (architectures is null || architectures.Count == 0)) findings.Add("ERROR: Add a download URL or an architecture-specific download.");
        if (manifest["installer"] is not null || manifest["pre_install"] is not null || manifest["post_install"] is not null || manifest["uninstaller"] is not null) findings.Add("WARNING: This manifest contains installer instructions or scripts. Review them manually before installation.");
        if (findings.Count == 0) findings.Add("Basic checks passed.");
        findings.Add("These checks do not verify the publisher, download contents, executable paths, or installation behavior.");
        return findings;
    }
    public static string ManifestPath(string root, string bucket, string name)
    {
        ScoopService.SafeSegment(bucket); ScoopService.SafeSegment(name);
        var dir = Path.Combine(root, "buckets", bucket);
        if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Select an existing local bucket folder.");
        var target = Path.Combine(dir, "bucket");
        if (!Directory.Exists(target)) target = dir;
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked manifest folders are not supported.");
        return Path.Combine(target, name + ".json");
    }
    public static async Task<string> HashDownload(string url, CancellationToken ct)
    {
        var current = Https(url);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        for (var i = 0; i < 6; i++)
        {
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { current = Https(new Uri(current, location).AbsoluteUri); continue; }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }
        throw new HttpRequestException("Too many redirects while downloading the file.");
    }
}
