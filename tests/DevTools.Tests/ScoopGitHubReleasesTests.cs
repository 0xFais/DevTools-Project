using System.Net;
using System.Net.Http;
using DevTools.Modules.Scoop;

namespace DevTools.Tests;

internal static class ScoopGitHubReleasesTests
{
    internal static void RunAll(Action<string, Action> run)
    {
        run("GitHub release discovery validates repositories and parses assets", () =>
        {
            var repository = ScoopGitHubReleases.ParseRepository("https://github.com/example/project.git");
            if (repository != ("example", "project")) throw new Exception("Repository parsing failed.");
            foreach (var invalid in new[] { "http://github.com/a/b", "https://github.com.evil.test/a/b", "https://github.com/a/b/releases", "https://user@github.com/a/b" })
            {
                try { ScoopGitHubReleases.ParseRepository(invalid); throw new Exception("Invalid URL accepted."); }
                catch (ArgumentException) { }
            }
            using var client = new HttpClient(new Handler(HttpStatusCode.OK, """
                [{"tag_name":"v1.2","name":"Release 1.2","draft":false,"assets":[{"name":"app.zip","browser_download_url":"https://github.com/example/project/releases/download/v1.2/app.zip","size":123},{"name":"bad","browser_download_url":"http://evil.test/app.zip"}]}]
                """));
            var releases = ScoopGitHubReleases.GetAsync("https://github.com/example/project", client, CancellationToken.None).GetAwaiter().GetResult();
            if (releases.Count != 1 || releases[0].Tag != "v1.2" || releases[0].Assets.Count != 1 || releases[0].Assets[0].Size != 123) throw new Exception("Release parsing failed.");
        });
        run("GitHub release discovery handles rate limits and cancellation", () =>
        {
            using var client = new HttpClient(new Handler(HttpStatusCode.Forbidden, "{}"));
            try { ScoopGitHubReleases.GetAsync("https://github.com/a/b", client, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Rate limit was ignored."); }
            catch (InvalidOperationException e) when (e.Message.Contains("rate limit")) { }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { ScoopGitHubReleases.GetAsync("https://github.com/a/b", client, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
        });
    }
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
