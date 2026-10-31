using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using DevTools.Contracts;
using DevTools.Infrastructure;

namespace DevTools.Modules.Scoop;

public sealed class ScoopModule(IScoopSetupBackend? setupBackend = null) : IDevToolsModule
{
    public string Id => "scoop";
    public ModulePage CreatePage(IModuleContext context)
    {
        var root = ScoopService.DetectRoot(context.Get("scoop.root"));
        var workspace = new ScoopWorkspace { Root = root, InstallLocation = string.IsNullOrWhiteSpace(root) ? Path.Combine(AppContext.BaseDirectory, "Scoop") : root };
        var page = new ModulePage(Id, "Scoop manager", "Installed packages, bucket search, cache and custom portable applications.", "Apps Manager", "\uE8F1") { ScoopWorkspace = workspace };
        _ = new ScoopController(workspace, page, context, setupBackend);
        return page;
    }

}

public sealed class ScoopController
{
    private readonly ScoopWorkspace w;
    private readonly ModulePage page;
    private readonly IModuleContext c;
    private sealed record Inputs(string Root, ScoopPackage? Selected, ScoopPackage[] Installed, ScoopPackage[] Search, ScoopBucket[] Buckets, Dictionary<string, string> Fields, string Query, string Version, string Paths, string BucketName, string BucketUrl, string InstallLocation);
    private readonly Queue<(string Label, Func<CancellationToken, Task> Work, Inputs Inputs)> queue = new();
    private Inputs? active;
    private Inputs Capture() => new(w.Root, w.Selected, w.Selection.ToArray(), w.SearchSelection.ToArray(), w.BucketSelection.ToArray(), w.CustomFields.ToDictionary(f => f.Key, f => f.Value), w.Query, w.Version, w.PathEntries, w.BucketName, w.BucketUrl, w.InstallLocation);
    private readonly Progress<string> log;
    private List<ScoopPackage> all = [];
    private bool running;
    private string loadedManifestRoot = "", loadedManifestPath = "", loadedManifestContents = "";
    private Dictionary<string, string> formBaseline = new();
    private readonly ScoopSetupService setup;
    private static bool IsBucketManifest(string root, string file) => ScoopService.Buckets(root).Any(b => { try { return string.Equals(ScoopBucketAuthoring.ManifestPath(root, b.Name, Path.GetFileNameWithoutExtension(file)), file, StringComparison.OrdinalIgnoreCase); } catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException) { return false; } });
    private string rulesFile => Path.Combine(c.DataDirectory, "scoop-paths.json");
    public ScoopController(ScoopWorkspace workspace, ModulePage page, IModuleContext context, IScoopSetupBackend? setupBackend = null)
    {
        (w, this.page, c) = (workspace, page, context);
        setup = new ScoopSetupService(setupBackend ?? new WindowsScoopSetupBackend());
        page.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModulePage.Status)) w.OperationStatus = page.Status; };
        log = new Progress<string>(line => c.Log("Scoop", line));
        w.FilterChanged += Filter;
        w.SelectionChanged += Select;
        foreach (var (key, label, value) in new[] { ("name", "App name", ""), ("version", "Version", "1.0"), ("folder", "App folder", ""), ("exe", "Main executable", ""), ("shortcut", "Shortcut name", ""), ("shim", "Shim name (optional)", ""), ("description", "Description", "") })
            w.CustomFields.Add(new(key, label, value));
        var method = new ModuleField("method", "Add method", "Copy", FieldKind.Choice); method.Choices.Add("Copy"); method.Choices.Add("Move"); w.CustomFields.Add(method);
        Add("Refresh", Refresh);
        Add("CheckSetup", Refresh);
        Add("AddScoopPath", async ct => { var root = Root(); await Confirm("Add Scoop to PATH", "Add " + Path.Combine(root, "shims") + " to your user PATH? Existing entries will be preserved. Newly opened terminals will recognize Scoop."); await setup.AddToPath(root, previous => c.Save("scoop.setup.pathBackup", previous), ct, log); await Refresh(ct); });
        Add("PortableSetup", async ct => { var root = Root(); await Confirm("Set up portable Scoop on this PC", "Set the user SCOOP environment variable to " + root + " and add its shims folder to your user PATH? Previous values are backed up. PowerShell execution policy and global Git settings will not be changed."); await setup.ConfigurePortable(root, previous => c.Save("scoop.setup.pathBackup", previous), previous => c.Save("scoop.setup.scoopBackup", previous), ct, log); await Refresh(ct); });
        Add("BrowseInstallLocation", async ct => { if (await c.PickFolderAsync("Choose an empty folder for Scoop") is { } location) w.InstallLocation = location; });
        Add("InstallScoop", async ct =>
        {
            if (w.HasInstallation) throw new InvalidOperationException("Scoop is already installed. Use Add to PATH or choose another existing installation.");
            var location = ScoopSetupService.ValidateDestination(active?.InstallLocation ?? w.InstallLocation);
            await Confirm("Install Scoop", "Download and run the official Scoop installer?\n\nInstall location: " + location + "\nSource: " + ScoopSetupService.InstallerUrl + "\n\nScoop's shims folder will be added to your user PATH. Existing PATH entries will be preserved.");
            w.SetupState = ScoopSetupState.Installing; w.SetupMessage = "Downloading and installing Scoop. Progress appears in Activity.";
            try { await setup.Install(location, Path.Combine(c.DataDirectory, "Downloads"), previous => c.Save("scoop.setup.pathBackup", previous), ct, log); w.Root = location; c.Save("scoop.root", location); if (active is not null) active = active with { Root = location }; await Refresh(ct); }
            finally { if (w.SetupState == ScoopSetupState.Installing) ApplySetup(setup.Probe(location)); }
        });
        Add("ChooseRoot", async ct => { if (await c.PickFolderAsync("Select Scoop root (containing apps and buckets)") is { } root) { w.Root = root; if (active is not null) active = active with { Root = root }; await Refresh(ct); } });
        Add("Search", async ct => { var root = Root(); var query = active?.Query ?? w.Query; var rows = await Task.Run(() => ScoopService.Search(root, query, ct), ct); Replace(w.Results, rows); w.Summary = $"{rows.Count} search results" + (rows.Count == 500 ? " · Limited to 500; refine your search" : ""); });
        foreach (var action in new[] { "update", "hold", "unhold", "reset", "uninstall" })
        {
            var verb = action;
            w.Commands[action] = new Command(() =>
            {
                var packages = w.Selection.Count > 0 ? w.Selection.ToArray() : w.Selected is { } selected ? [selected] : throw new InvalidOperationException("Select installed apps.");
                var names = packages.Select(a => ScoopService.SafeSegment(a.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); var root = Path.GetFullPath(w.Root);
                Enqueue(verb, async ct => {
                    if (verb == "uninstall") await Confirm("Uninstall packages", string.Join(", ", names));
                    if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Scoop root changed while this operation was queued. Refresh and select packages again.");
                    _ = ScoopService.Script(root, []);
                    var installed = await Task.Run(() => ScoopService.Installed(root, ct), ct);
                    var stale = packages.Where(p => !installed.Any(a => string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFullPath(a.Directory), Path.GetFullPath(p.Directory), StringComparison.OrdinalIgnoreCase) && string.Equals(a.Version, p.Version, StringComparison.OrdinalIgnoreCase))).Select(p => p.Name).ToArray();
                    if (stale.Length > 0) throw new InvalidOperationException("Installed packages changed since selection: " + string.Join(", ", stale) + ". Refresh and select them again.");
                    try { await Run(root, [verb, .. names], ct); }
                    catch { try { await Refresh(CancellationToken.None); } catch (Exception refreshError) { c.Log("Scoop", "Refresh after failure: " + refreshError.Message); } throw; }
                    await Refresh(ct);
                    if (verb is "hold" or "unhold")
                    {
                        var expected = verb == "hold";
                        var failed = names.Where(n => n != "scoop" && all.FirstOrDefault(a => a.Name == n)?.Held != expected).ToArray();
                        if (failed.Length > 0) throw new IOException("Scoop did not confirm the requested hold state for: " + string.Join(", ", failed));
                    }
                    else
                    {
                        var failed = names.Where(n => (all.Any(a => string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase))) == (verb == "uninstall")).ToArray();
                        if (failed.Length > 0) throw new IOException("Scoop did not confirm the expected installed state for: " + string.Join(", ", failed));
                        await SyncPaths(root, ct);
                    }
                });
            }, Error);
        }
        Add("Status", ct => Run(Root(), ["status"], ct));
        w.Commands["UpdateSelectedPackages"] = new Command(() => {
            var packages = w.UpdateSelection.ToArray();
            if (packages.Length == 0) throw new InvalidOperationException("Select at least one package with an available update.");
            if (packages.Any(p => p.Held)) throw new InvalidOperationException("Held packages cannot be updated. Allow updates first or remove them from the selection.");
            foreach (var package in packages) ScoopService.SafeSegment(package.Name);
            var root = Path.GetFullPath(w.Root);
            Enqueue("Update selected packages", async ct => {
                await Confirm("Update selected packages", "Update these packages?\n\n" + string.Join("\n", packages.Select(p => $"{p.Name}: {p.Version} → {p.AvailableVersion}")));
                if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Scoop root changed. Refresh and select packages again.");
                var current = await Task.Run(() => ScoopService.Installed(root, ct), ct);
                var stale = packages.Where(p => !current.Any(a => string.Equals(a.Name, p.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Version, p.Version, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Directory, p.Directory, StringComparison.OrdinalIgnoreCase) && !a.Held && a.AvailableVersion == p.AvailableVersion && a.AvailableVersion.Length > 0)).Select(p => p.Name).ToArray();
                if (stale.Length > 0) throw new InvalidOperationException("Package state changed: " + string.Join(", ", stale) + ". Refresh and select packages again.");
                var results = new List<string>();
                foreach (var package in packages)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await Run(root, ["update", package.Name], ct);
                        var installed = await Task.Run(() => ScoopService.Installed(root, ct), ct);
                        var updated = installed.FirstOrDefault(a => string.Equals(a.Name, package.Name, StringComparison.OrdinalIgnoreCase));
                        results.Add(updated is not null && string.Equals(updated.Version, package.AvailableVersion, StringComparison.OrdinalIgnoreCase) ? package.Name + ": updated to " + updated.Version : package.Name + ": unable to verify the expected version");
                    }
                    catch (OperationCanceledException) { w.UpdateCenterMessage = "Cancelled. " + string.Join("; ", results); try { await Refresh(CancellationToken.None); } catch (Exception e) { c.Log("Scoop", "Refresh after cancellation: " + e.Message); } throw; }
                    catch (Exception e) { results.Add(package.Name + ": failed — " + e.Message); }
                }
                w.UpdateCenterMessage = string.Join("; ", results);
                try { await Refresh(ct); await SyncPaths(root, ct); }
                finally { c.Log("Scoop", w.UpdateCenterMessage); }
                await c.ShowReportAsync("Package update results", string.Join("\n", results));
            });
        }, Error);
        Add("UpdateAll", async ct => { var root = Root(); await Run(root, ["update", "*"], ct); await Refresh(ct); await SyncPaths(root, ct); });
        Add("ResetAll", async ct => { await Confirm("Reset all apps", "Recreate shims, shortcuts and PATH entries for every Scoop app?"); await Run(Root(), ["reset", "*"], ct); await Refresh(ct); });
        Add("Cleanup", async ct => { await Confirm("Cleanup all old versions", "Remove old versions of every app? You cannot switch back to those versions afterward."); await Run(Root(), ["cleanup", "*"], ct); await Refresh(ct); });
        Add("Install", Install);
        Add("Download", Download);
        Add("Info", async ct => {
            var a = (active?.Search ?? w.SearchSelection.ToArray()).FirstOrDefault() ?? throw new InvalidOperationException("Select a search result.");
            var manifest = ScoopService.Read(a.ManifestPath);
            var info = $"{a.Name} · {a.Version}\nBucket: {a.Bucket} · {a.Status}\n\n{a.Description}\n\nHomepage: {a.Homepage}\nLicense: {a.License}\nDependencies: {manifest["depends"]}\nDownload: {manifest["url"] ?? manifest["architecture"]}";
            await c.ShowReportAsync("Package information · " + a.Name, info);
        });
        Add("Details", Details);
        Add("SwitchVersion", async ct => { var a = One(); var version = ScoopService.SafeSegment(active?.Version ?? w.Version); if (!Directory.Exists(Path.Combine(a.Directory, version)) || version == "current") throw new ArgumentException("Choose an installed version."); await Confirm("Switch version", $"Reset {a.Name} to {version}?"); await Run(Root(), ["reset", a.Name + "@" + version], ct); await Refresh(ct); });
        Add("OpenFolder", ct => { c.Open(One().Directory); return Task.CompletedTask; });
        Add("Homepage", ct => { if (Uri.TryCreate(One().Homepage, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") c.Open(uri.AbsoluteUri); else throw new InvalidOperationException("No HTTP homepage in this manifest."); return Task.CompletedTask; });
        IReadOnlyList<ScoopGitHubRelease> releases = [];
        w.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ScoopWorkspace.SelectedGitHubRelease)) {
                var release = releases.FirstOrDefault(r => r.ToString() == w.SelectedGitHubRelease);
                w.GitHubAssetNames.Clear(); if (release is not null) foreach (var asset in release.Assets) w.GitHubAssetNames.Add(asset.Name);
                w.SelectedGitHubAsset = "";
                if (release is not null) w.AuthorVersion = release.Tag.TrimStart('v', 'V');
            } else if (e.PropertyName == nameof(ScoopWorkspace.SelectedGitHubAsset)) {
                var release = releases.FirstOrDefault(r => r.ToString() == w.SelectedGitHubRelease);
                var asset = release?.Assets.FirstOrDefault(a => a.Name == w.SelectedGitHubAsset);
                if (asset is not null) { w.AuthorUrl = asset.Url; w.AuthorHash = ""; }
            }
        };
        Add("LoadGitHubReleases", async ct => { var repo = w.GitHubRepository; releases = await ScoopGitHubReleases.GetAsync(repo, ct); w.GitHubReleaseNames.Clear(); foreach (var release in releases) w.GitHubReleaseNames.Add(release.ToString()); w.SelectedGitHubRelease = ""; w.GitHubAssetNames.Clear(); if (releases.Count == 0) throw new InvalidOperationException("No published releases with assets were found."); });
        w.Commands["CalculateAuthorHash"] = new Command(() => { var url = w.AuthorUrl; Enqueue("Calculate SHA-256", async ct => { var hash = await ScoopBucketAuthoring.HashDownload(url, ct); if (w.AuthorUrl == url) w.AuthorHash = hash; else throw new InvalidOperationException("The download URL changed; calculate the hash again."); }); }, Error);
        w.Commands["CreateLocalBucket"] = new Command(() => { var root = Root(); var name = ScoopService.SafeSegment(w.NewBucketName.Trim()); Enqueue("Create bucket", async ct => {
            var parent = Path.Combine(root, "buckets"); var target = Path.Combine(parent, name);
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("A bucket with this name already exists.");
            await Confirm("Create local bucket", $"Create a local Git bucket at {target}? It will not be published online.");
            if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase) || Directory.Exists(target) || File.Exists(target)) throw new InvalidOperationException("The root or target changed. Try again.");
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, ".devtools-bucket-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(staging, "bucket"));
                await ScoopService.RunScript("$ErrorActionPreference='Stop'; & git init -- " + ScoopService.Literal(staging) + "; if ($LASTEXITCODE -ne 0) { throw 'Git initialization failed' }", ct, log);
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Scoop root changed. Try again.");
                Directory.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging) && (File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0) Directory.Delete(staging, true);
            }
            await Refresh(ct); w.AuthorBucket = name;
        }); }, Error);
        w.Commands["SaveBucketManifest"] = new Command(() => { var root = Root(); var bucket = w.AuthorBucket; var name = w.AuthorName.Trim(); var manifest = NewManifest(); var destination = ScoopBucketAuthoring.ManifestPath(root, bucket, name); var contents = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); Enqueue("Save manifest", async ct => {
            if (File.Exists(destination)) throw new IOException("A manifest with this name already exists. Choose another name.");
            await Confirm("Save app manifest", $"Bucket: {bucket}\nFile: {destination}\n\n{contents}\n\nSave this manifest? It does not install the app.");
            if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase) || ScoopBucketAuthoring.ManifestPath(root, bucket, name) != destination) throw new InvalidOperationException("The bucket or Scoop root changed. Try again.");
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".devtools-" + Guid.NewGuid().ToString("N") + ".tmp");
            try { await File.WriteAllTextAsync(temporary, contents, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, false); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            await Refresh(ct);
        }); }, Error);
        w.PropertyChanged += (_, e) => {
            if (e.PropertyName != nameof(ScoopWorkspace.EditorBucket)) return;
            w.EditorManifestNames.Clear(); w.EditorManifest = "";
            if (string.IsNullOrWhiteSpace(w.EditorBucket)) return;
            try {
                var root = Path.GetFullPath(w.Root);
                if (!ScoopService.Buckets(root).Any(b => string.Equals(b.Name, w.EditorBucket, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Choose an added bucket.");
                var sample = ScoopBucketAuthoring.ManifestPath(root, w.EditorBucket, "example");
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(sample)!, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) w.EditorManifestNames.Add(Path.GetFileNameWithoutExtension(file));
                }
            } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException) { Error(ex); }
        };
        w.Commands["LoadSelectedBucketJson"] = new Command(() => {
            var root = Path.GetFullPath(Root()); var bucket = ScoopService.SafeSegment(w.EditorBucket); var name = ScoopService.SafeSegment(w.EditorManifest);
            var file = ScoopBucketAuthoring.ManifestPath(root, bucket, name);
            Enqueue("Load bucket manifest", async ct => {
                if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase) || !IsBucketManifest(root, file) || !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("The selected bucket or manifest changed. Choose it again.");
                var contents = await File.ReadAllTextAsync(file, ct);
                if (JsonNode.Parse(contents) is not JsonObject) throw new InvalidDataException("The manifest must contain a JSON object.");
                loadedManifestRoot = root; loadedManifestPath = file; loadedManifestContents = contents;
                w.ManifestEditorFile = file; w.ManifestJson = contents; LoadManifestForm(file, contents);
            });
        }, Error);
        Add("LoadBucketJson", async ct => {
            var root = Root();
            var file = await c.PickFileAsync("Choose a JSON manifest in an added bucket", "JSON files|*.json");
            if (file is null) return;
            file = Path.GetFullPath(file);
            if (!IsBucketManifest(root, file) || !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Choose a JSON manifest directly inside an added bucket.");
            var contents = await File.ReadAllTextAsync(file, ct);
            if (JsonNode.Parse(contents) is not JsonObject) throw new InvalidDataException("The manifest must contain a JSON object.");
            loadedManifestRoot = root; loadedManifestPath = file; loadedManifestContents = contents;
            w.ManifestEditorFile = file; w.ManifestJson = contents; LoadManifestForm(file, contents);
        });
        Add("PreviewNewManifest", async ct => {
            var node = NewManifest();
            await c.ShowReportAsync("Manifest preview and checks", string.Join("\n", ScoopBucketAuthoring.Inspect(node)) + "\n\nGenerated JSON:\n" + node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        });
        Add("InspectEditedManifest", async ct => {
            if (loadedManifestPath.Length == 0) throw new InvalidOperationException("Load a bucket manifest first.");
            var node = JsonNode.Parse(w.ManifestJson) as JsonObject ?? throw new InvalidDataException("The manifest must contain a JSON object.");
            ApplyForm(node);
            await c.ShowReportAsync("Manifest preview and checks", string.Join("\n", ScoopBucketAuthoring.Inspect(node)) + "\n\nPreview with current form changes:\n" + node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        });
        w.Commands["ApplyManifestForm"] = new Command(() => {
            if (loadedManifestPath.Length == 0) throw new InvalidOperationException("Load a bucket manifest first.");
            var node = JsonNode.Parse(w.ManifestJson) as JsonObject ?? throw new InvalidDataException("The manifest must contain a JSON object.");
            ApplyForm(node); w.ManifestJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); formBaseline = FormValues();
        }, Error);
        w.Commands["SaveBucketJson"] = new Command(() => {
            var root = loadedManifestRoot; var file = loadedManifestPath; var original = loadedManifestContents; var contents = w.ManifestJson;
            if (file.Length == 0) throw new InvalidOperationException("Load a bucket manifest first.");
            if (JsonNode.Parse(contents) is not JsonObject) throw new InvalidDataException("The manifest must contain a JSON object.");
            Enqueue("Save edited manifest", async ct => {
                if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase) || !IsBucketManifest(root, file) || !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || await File.ReadAllTextAsync(file, ct) != original) throw new InvalidOperationException("The Scoop root or manifest changed. Reload it before saving.");
                await Confirm("Replace bucket manifest", $"Replace {file} with the edited JSON?\n\nThis changes the bucket manifest; it does not install the app.");
                if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase) || !IsBucketManifest(root, file) || !File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || await File.ReadAllTextAsync(file, ct) != original) throw new InvalidOperationException("The Scoop root or manifest changed. Reload it before saving.");
                var temp = Path.Combine(Path.GetDirectoryName(file)!, ".devtools-" + Guid.NewGuid().ToString("N") + ".tmp");
                try { await File.WriteAllTextAsync(temp, contents, ct); ct.ThrowIfCancellationRequested(); File.Move(temp, file, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                if (loadedManifestPath == file) loadedManifestContents = contents; await Refresh(ct);
            });
        }, Error);
        w.Commands["ExportManifestJson"] = new Command(() => {
            var name = ScoopService.SafeSegment(w.AuthorName.Trim());
            var manifest = NewManifest();
            var contents = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            Enqueue("Export manifest", async ct => {
                var folder = await c.PickFolderAsync("Choose where to export the JSON manifest"); if (folder is null) return;
                var target = Path.Combine(Path.GetFullPath(folder), name + ".json");
                if (File.Exists(target)) throw new IOException("The export file already exists. Choose a different folder or app name.");
                await Confirm("Export app manifest", $"Save {target}?\n\n{contents}");
                var temp = Path.Combine(Path.GetDirectoryName(target)!, ".devtools-" + Guid.NewGuid().ToString("N") + ".tmp");
                try { await File.WriteAllTextAsync(temp, contents, ct); ct.ThrowIfCancellationRequested(); File.Move(temp, target, false); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            });
        }, Error);
        Add("AddBucket", async ct => { var name = ScoopService.SafeSegment((active?.BucketName ?? w.BucketName).Trim()); var root = Root(); var args = new List<string> { "bucket", "add", name }; if (!string.IsNullOrWhiteSpace(active?.BucketUrl ?? w.BucketUrl)) { if (!Uri.TryCreate((active?.BucketUrl ?? w.BucketUrl).Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Use an HTTPS bucket repository URL without embedded credentials."); args.Add(uri.AbsoluteUri); } if (ScoopService.Buckets(root).Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This bucket is already added: " + name); await Confirm("Add bucket", string.Join(" ", args)); if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Scoop root changed. Try again."); if (ScoopService.Buckets(root).Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This bucket is already added: " + name); await Run(root, args, ct); await Refresh(ct); });
        Add("RemoveBucket", async ct => { var names = (active?.Buckets ?? w.BucketSelection.ToArray()).Select(b => ScoopService.SafeSegment(b.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (names.Length == 0) throw new InvalidOperationException("Select a bucket."); var root = Root(); await Confirm("Remove buckets", string.Join(", ", names)); if (!string.Equals(Path.GetFullPath(w.Root), root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Scoop root changed. Select buckets again."); var current = ScoopService.Buckets(root); var missing = names.Where(n => !current.Any(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase))).ToArray(); if (missing.Length > 0) throw new InvalidOperationException("Buckets no longer present: " + string.Join(", ", missing)); foreach (var name in names) await Run(root, ["bucket", "rm", name], ct); await Refresh(ct); });
        Add("UpdateBuckets", async ct => { await Run(Root(), ["update"], ct); await Refresh(ct); });
        Add("ShowCache", ct => Run(Root(), ["cache", "show"], ct));
        Add("ClearCache", async ct => { await Confirm("Clear cache", "Delete all cached Scoop downloads?"); await Run(Root(), ["cache", "rm", "*"], ct); });
        Add("BrowseFolder", async ct => { if (await c.PickFolderAsync("Choose portable app folder") is { } path) { w.Field("folder").Value = path; if (w.Field("name").Value.Length == 0) w.Field("name").Value = SuggestCustomName(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))); } });
        Add("BrowseExe", async ct => { if (await c.PickFileAsync("Choose main executable inside app folder", "Executable|*.exe") is { } path) { w.Field("exe").Value = path; if (w.Field("folder").Value.Length == 0) w.Field("folder").Value = Path.GetDirectoryName(path) ?? ""; if (w.Field("name").Value.Length == 0) w.Field("name").Value = SuggestCustomName(Path.GetFileNameWithoutExtension(path)); if (w.Field("shortcut").Value.Length == 0) w.Field("shortcut").Value = Path.GetFileNameWithoutExtension(path); } });
        Add("AddCustom", AddCustom);
        void AuthorListCommand(string name, Action action) => w.Commands[name] = new Command(() => { action(); w.AuthorListMessage = ""; }, e => w.AuthorListMessage = e.Message);
        static void AddUnique(System.Collections.ObjectModel.ObservableCollection<string> list, string item)
        {
            if (list.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("This entry is already in the list.");
            list.Add(item);
        }
        AuthorListCommand("AddAuthorExecutable", () => { var value = w.AuthorExecutableDraft.Trim(); ValidateExecutable(value); AddUnique(w.AuthorExecutableItems, value); w.AuthorExecutableDraft = ""; });
        AuthorListCommand("RemoveAuthorExecutable", () => { if (w.SelectedAuthorExecutable is not { } value) throw new ArgumentException("Select an executable to remove."); w.AuthorExecutableItems.Remove(value); });
        AuthorListCommand("AddAuthorShortcut", () => { var exe = w.AuthorShortcutExecutableDraft.Trim(); var label = w.AuthorShortcutNameDraft.Trim(); if (exe.Contains('|') || label.Contains('|')) throw new ArgumentException("The character | is not supported in shortcut fields."); var value = exe + " | " + label; ApplyLists(new JsonObject(), value, ""); AddUnique(w.AuthorShortcutItems, value); w.AuthorShortcutExecutableDraft = ""; w.AuthorShortcutNameDraft = ""; });
        AuthorListCommand("RemoveAuthorShortcut", () => { if (w.SelectedAuthorShortcut is not { } value) throw new ArgumentException("Select a shortcut to remove."); w.AuthorShortcutItems.Remove(value); });
        AuthorListCommand("AddAuthorPath", () => { var value = w.AuthorPathDraft.Trim(); if (value.Length == 0) throw new ArgumentException("Enter a folder path."); ApplyLists(new JsonObject(), "", value); AddUnique(w.AuthorPathItems, value); w.AuthorPathDraft = ""; });
        AuthorListCommand("RemoveAuthorPath", () => { if (w.SelectedAuthorPath is not { } value) throw new ArgumentException("Select a folder to remove."); w.AuthorPathItems.Remove(value); });
        void PathCommand(string name, Action action) => w.Commands[name] = new Command(() => { action(); w.PathMessage = ""; }, e => w.PathMessage = e.Message);
        PathCommand("AddPathEntry", () => {
            var entry = w.PathDraft.Trim();
            if (entry.Length == 0) throw new ArgumentException("Enter a folder path first.");
            ValidateEntry(One(), entry);
            if (w.PathItems.Any(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("This folder is already in the list.");
            w.PathItems.Add(entry); w.SelectedPath = entry;
        });
        PathCommand("ReplacePathEntry", () => {
            var old = w.SelectedPath ?? throw new InvalidOperationException("Select a folder to edit.");
            var entry = w.PathDraft.Trim();
            if (entry.Length == 0) throw new ArgumentException("Enter a folder path first.");
            ValidateEntry(One(), entry);
            if (w.PathItems.Any(p => !string.Equals(p, old, StringComparison.OrdinalIgnoreCase) && string.Equals(p, entry, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("This folder is already in the list.");
            var index = w.PathItems.IndexOf(old); if (index < 0) throw new InvalidOperationException("Select a folder to edit.");
            w.PathItems[index] = entry; w.SelectedPath = entry;
        });
        PathCommand("RemovePathEntry", () => {
            var entry = w.SelectedPath ?? throw new InvalidOperationException("Select a folder to remove.");
            w.PathItems.Remove(entry); w.SelectedPath = null;
        });
        Add("SavePaths", SavePaths);
        Add("AddPathFolder", async ct => { var a = One(); if (await c.PickFolderAsync("Choose PATH folder") is { } path) { var current = ScoopService.Current(a.Directory); var relative = Path.GetRelativePath(current, path); var entry = relative == "." || (!relative.StartsWith("..") && !Path.IsPathRooted(relative)) ? relative : path; w.PathEntries = (w.PathEntries.TrimEnd() + Environment.NewLine + entry).Trim(); } });
        Add("SyncPaths", async ct => { await Confirm("Reapply saved PATH rules", "Reapply saved per-app PATH rules, reset changed apps, and remove owned paths of uninstalled apps?"); await SyncPaths(Root(), ct); });
        Add("ImportRules", async ct => { if (await c.PickFileAsync("Import previous ScoopManager.ini PATH rules", "INI files|*.ini") is not { } file) return; var ini = SettingsStore.ReadIni(file); if (!ini.TryGetValue("Paths", out var section)) throw new InvalidDataException("No [Paths] section."); await Confirm("Import PATH rules", "Save the imported per-app PATH rules? Use Reapply saved rules to apply them."); var rules = Rules(); foreach (var pair in section) rules[ScoopService.SafeSegment(pair.Key)] = pair.Value.Split('|', StringSplitOptions.RemoveEmptyEntries); WriteRules(rules); Select(); });
    }
    private void Add(string key, Func<CancellationToken, Task> work) => w.Commands[key] = new Command(() => Enqueue(key, work), Error);
    private async void Enqueue(string label, Func<CancellationToken, Task> work)
    {
        if (!running && c.IsOperationRunning) { Error(new InvalidOperationException("Wait for the other DevTools operation to finish.")); return; }
        queue.Enqueue((label, work, Capture()));
        if (running) { c.Log("Scoop", $"Queued {label} ({queue.Count} waiting)"); return; }
        if (!c.TryBeginOperation()) { queue.Clear(); return; }
        running = true;
        w.IsRunning = true;
        try
        {
            while (queue.TryDequeue(out var job))
            {
                c.OperationToken.ThrowIfCancellationRequested();
                active = job.Inputs;
                page.Status = "Running · " + job.Label;
                try { await job.Work(c.OperationToken); page.Status = "Completed · " + job.Label; }
                catch (ActionDeclinedException) { page.Status = "Declined · " + job.Label; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { queue.Clear(); page.Status = "Failed · " + e.Message; c.Log("Scoop", e.ToString()); await c.ShowReportAsync("Scoop operation failed", e.Message); break; }
            }
        }
        catch (OperationCanceledException) { queue.Clear(); page.Status = "Cancelled · queue cleared; refresh to inspect any partial changes"; c.Log("Scoop", page.Status); }
        finally { active = null; running = false; w.IsRunning = false; c.EndOperation(); }
    }
    private void Error(Exception e) { page.Status = e.Message; c.Log("Scoop", e.Message); }
    private async Task Confirm(string title, string text) { if (!await c.ConfirmAsync(title, text)) throw new ActionDeclinedException(); }
    private string Root()
    {
        var root = Path.GetFullPath((active?.Root ?? w.Root).Trim());
        _ = ScoopService.Script(root, []); c.Save("scoop.root", root); return root;
    }
    private async Task Run(string root, IEnumerable<string> args, CancellationToken ct)
    {
        var captured = args.ToArray(); c.Log("Scoop", "scoop " + string.Join(" ", captured));
        var script = ScoopService.Script(root, captured);
        // Preflight before commands which can update or modify Git-backed buckets.
        if (captured.FirstOrDefault() is "install" or "update" or "bucket")
        {
            var blocked = new List<string>();
            var git = new[] { Path.Combine(root, "apps", "git", "current", "cmd", "git.exe"), Path.Combine(root, "apps", "git", "current", "bin", "git.exe") }.FirstOrDefault(File.Exists) ?? "git.exe";
            foreach (var repository in ScoopService.GitRepositories(root))
            {
                ProcessResult result;
                try { result = await c.RunAsync(git, ["-C", repository, "rev-parse", "--is-inside-work-tree"], ct); }
                catch (System.ComponentModel.Win32Exception) { break; } // Scoop will report a missing Git installation.
                if (result.ExitCode != 0 && ScoopService.OwnershipBlocked(result.Output)) blocked.Add(repository);
            }
            if (blocked.Count > 0)
            {
                await Confirm("Trust these Scoop repositories?", "Git reports that these Scoop repositories belong to a different Windows account:\n\n" + string.Join("\n", blocked) + "\n\nIf you recognize these folders, allow Scoop to use them for this command? Trust is limited to this child process; your global Git settings will not change.");
                script = ScoopService.WithGitTrust(script, blocked);
                c.Log("Scoop", "Using command-scoped Git trust for: " + string.Join(", ", blocked));
            }
        }
        await ScoopService.RunScript(script, ct, log);
    }

    private static void Replace<T>(System.Collections.ObjectModel.ObservableCollection<T> items, IEnumerable<T> values) { items.Clear(); foreach (var value in values) items.Add(value); }
    private async Task Refresh(CancellationToken ct)
    {
        var candidate = active?.Root ?? w.Root;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = ScoopService.DetectRoot(c.Get("scoop.root"));
        var status = setup.Probe(candidate); ApplySetup(status);
        if (!w.HasInstallation) { all.Clear(); Filter(); w.UpdatePackages.Clear(); w.UpdateSelection.Clear(); w.Results.Clear(); w.Buckets.Clear(); w.Summary = ""; return; }
        var root = status.Root;
        w.Root = root; c.Save("scoop.root", root); if (active is not null) active = active with { Root = root };
        var result = await Task.Run(() => (Apps: ScoopService.Installed(root, ct), Buckets: ScoopService.Buckets(root)), ct);
        all = result.Apps; Filter(); w.UpdateSelection.Clear(); Replace(w.UpdatePackages, all.Where(a => a.AvailableVersion.Length > 0)); Replace(w.Buckets, result.Buckets); Replace(w.AuthorBuckets, result.Buckets.Select(b => b.Name));
        w.Summary = $"{all.Count} installed · {all.Count(a => a.Status.StartsWith("Update:"))} updates · {all.Count(a => a.Held)} held · {w.Buckets.Count} buckets";
    }
    private Dictionary<string, string> FormValues() => new() { ["version"] = w.AuthorVersion, ["url"] = w.AuthorUrl, ["hash"] = w.AuthorHash, ["description"] = w.AuthorDescription, ["homepage"] = w.AuthorHomepage, ["license"] = w.AuthorLicense, ["architecture"] = w.AuthorArchitecture, ["bin"] = w.AuthorExecutable, ["shortcuts"] = w.AuthorShortcuts, ["env_add_path"] = w.AuthorPaths };
    private static string Scalar(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
    private void LoadManifestForm(string file, string contents)
    {
        var n = (JsonObject)JsonNode.Parse(contents)!; w.AuthorName = Path.GetFileNameWithoutExtension(file);
        w.AuthorVersion = Scalar(n["version"]); w.AuthorDescription = Scalar(n["description"]); w.AuthorHomepage = Scalar(n["homepage"]); w.AuthorLicense = Scalar(n["license"]); w.AuthorExecutable = n["bin"] is JsonArray bins ? string.Join(Environment.NewLine, bins.Select(Scalar).Where(x => x.Length > 0)) : Scalar(n["bin"]);
        var architectures = n["architecture"] as JsonObject; var arch = architectures?.FirstOrDefault(p => p.Value is JsonObject).Key;
        w.AuthorArchitecture = arch is "64bit" or "32bit" or "arm64" ? arch : "Any"; var source = w.AuthorArchitecture == "Any" ? n : architectures![w.AuthorArchitecture] as JsonObject;
        w.AuthorUrl = Scalar(source?["url"]); w.AuthorHash = Scalar(source?["hash"]);
        w.AuthorShortcuts = n["shortcuts"] is JsonArray shortcuts ? string.Join(Environment.NewLine, shortcuts.OfType<JsonArray>().Where(a => a.Count >= 2 && Scalar(a[0]).Length > 0 && Scalar(a[1]).Length > 0).Select(a => Scalar(a[0]) + " | " + Scalar(a[1]))) : "";
        w.AuthorPaths = n["env_add_path"] is JsonArray paths ? string.Join(Environment.NewLine, paths.Select(Scalar).Where(x => x.Length > 0)) : Scalar(n["env_add_path"]); formBaseline = FormValues();
    }
    private static void ApplyLists(JsonObject n, string shortcuts, string paths)
    {
        static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = new JsonArray(); foreach (var line in Lines(shortcuts)) { var parts = line.Split('|', 2, StringSplitOptions.TrimEntries); if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Each shortcut must be executable | name."); if (Path.IsPathRooted(parts[0]) || parts[0].Replace('\\', '/').Split('/').Any(p => p is ".." or "." or "")) throw new ArgumentException("Shortcut executable must be a relative path inside the app."); ScoopService.SafeSegment(parts[1].Replace(' ', '_')); items.Add(new JsonArray(parts[0].Replace('\\', '/'), parts[1])); }
        if (items.Count > 0) n["shortcuts"] = items; else n.Remove("shortcuts");
        var pathItems = new JsonArray(); foreach (var path in Lines(paths)) { var value = path.Replace('\\', '/'); if (Path.IsPathRooted(value) || value.Split('/').Any(p => p == ".." || p == "")) throw new ArgumentException("PATH entries must be relative folders inside the app."); pathItems.Add(value); } if (pathItems.Count > 0) n["env_add_path"] = pathItems; else n.Remove("env_add_path");
    }
    private static void ValidateExecutable(string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || normalized.Split('/').Any(p => p is ".." or "." or "")) throw new ArgumentException("Enter an executable path relative to the app folder.");
    }
    private static void ApplyExecutables(JsonObject n, string text)
    {
        var entries = text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries) ValidateExecutable(entry);
        if (entries.Length == 0) n.Remove("bin");
        else if (entries.Length == 1) n["bin"] = entries[0].Replace('\\', '/');
        else n["bin"] = new JsonArray(entries.Select(x => (JsonNode?)JsonValue.Create(x.Replace('\\', '/'))).ToArray());
    }
    private JsonObject NewManifest() { var node = ScoopBucketAuthoring.Manifest(w.AuthorVersion, w.AuthorUrl, w.AuthorHash, w.AuthorDescription, w.AuthorHomepage, w.AuthorLicense, w.AuthorArchitecture, "", ""); ApplyExecutables(node, w.AuthorExecutable); ApplyLists(node, w.AuthorShortcuts, w.AuthorPaths); return node; }
    private void ApplyForm(JsonObject n)
    {
        var current = FormValues(); bool Changed(string key) => !formBaseline.TryGetValue(key, out var old) || old != current[key];
        foreach (var key in new[] { "version", "description", "homepage", "license" }) if (Changed(key)) { if (key == "version") ScoopService.SafeSegment(current[key].Trim()); if (key == "homepage" && current[key].Trim().Length > 0) ScoopBucketAuthoring.Https(current[key]); if (current[key].Trim().Length == 0) n.Remove(key); else n[key] = current[key].Trim(); }
        if (Changed("bin")) { if (n["bin"] is JsonArray complexBins && complexBins.Any(x => x is not JsonValue)) throw new InvalidOperationException("This manifest has advanced executable entries. Edit them in Advanced JSON to preserve their options."); ApplyExecutables(n, w.AuthorExecutable); }
        if (Changed("architecture")) throw new InvalidOperationException("Change architecture in the JSON editor to preserve other architecture entries.");
        if (Changed("url") || Changed("hash")) { var target = w.AuthorArchitecture == "Any" ? n : n["architecture"]?[w.AuthorArchitecture] as JsonObject ?? throw new InvalidOperationException("Architecture entry changed in JSON; reload the manifest."); if (Changed("url")) { ScoopBucketAuthoring.Https(w.AuthorUrl); target["url"] = w.AuthorUrl.Trim(); } if (Changed("hash")) { if (!System.Text.RegularExpressions.Regex.IsMatch(w.AuthorHash.Trim(), "^[a-fA-F0-9]{64}$")) throw new ArgumentException("Enter a SHA-256 hash (64 hexadecimal characters)."); target["hash"] = w.AuthorHash.Trim().ToLowerInvariant(); } }
        if (Changed("shortcuts") && n["shortcuts"] is JsonArray complexShortcuts && complexShortcuts.Any(x => x is not JsonArray row || row.Count != 2 || row.Any(v => v is not JsonValue))) throw new InvalidOperationException("This manifest has advanced shortcuts. Edit them in Advanced JSON to preserve their options.");
        if (Changed("env_add_path") && n["env_add_path"] is JsonArray complexPaths && complexPaths.Any(x => x is not JsonValue)) throw new InvalidOperationException("This manifest has advanced PATH entries. Edit them in Advanced JSON to preserve their options.");
        if (Changed("shortcuts") || Changed("env_add_path")) { var scratch = new JsonObject(); ApplyLists(scratch, w.AuthorShortcuts, w.AuthorPaths); foreach (var key in new[] { "shortcuts", "env_add_path" }) if (Changed(key)) { if (scratch[key] is { } value) n[key] = value.DeepClone(); else n.Remove(key); } }
    }
    private void ApplySetup(ScoopSetupResult result) { if (w.SetupState == ScoopSetupState.Checking && result.State is ScoopSetupState.Missing or ScoopSetupState.Broken or ScoopSetupState.PathMissing) w.Tab = 5; w.SetupState = result.State; w.SetupMessage = result.Message; if (result.Root.Length > 0) w.Root = result.Root; }
    private void Filter() { var selected = w.Selected?.Name; Replace(w.Installed, all.Where(a => $"{a.Name} {a.Version} {a.Bucket} {a.HeldLabel} {a.Status}".Contains(w.Filter.Trim(), StringComparison.OrdinalIgnoreCase))); w.Selected = w.Installed.FirstOrDefault(a => a.Name == selected); }
    private ScoopPackage One() => (active?.Selected ?? w.Selected) ?? throw new InvalidOperationException("Select an installed app.");
    private void Select()
    {
        w.Versions.Clear();
        if (w.Selected is not { } a) { w.Details = "Select a package to inspect its details."; w.PathEntries = ""; w.Version = ""; return; }
        w.Details = $"{a.Name} · {a.Version}\n{a.Description}\nBucket: {a.Bucket} · {a.Status}\nHomepage: {a.Homepage}\nLicense: {a.License}\nPath: {a.Directory}";
        if (Directory.Exists(a.Directory)) Replace(w.Versions, Directory.GetDirectories(a.Directory).Where(d => Path.GetFileName(d) != "current").Select(Path.GetFileName).OfType<string>().Order());
        w.Version = a.Version;
        w.PathEntries = string.Join(Environment.NewLine, Rules().GetValueOrDefault(a.Name) ?? ScoopService.Strings(ScoopService.Read(a.ManifestPath)["env_add_path"]).ToArray());
    }
    private async Task Details(CancellationToken ct)
    {
        var a = One(); var manifest = ScoopService.Read(a.ManifestPath);
        var size = await Task.Run(() => ScoopService.FolderSize(a.Directory, ct), ct);
        w.Details = $"{a.Name} · {a.Version}\n{a.Description}\nBucket: {a.Bucket} · {a.Status}\nHomepage: {a.Homepage}\nLicense: {a.License}\nPath: {a.Directory}\nSize (all versions): {size / 1048576d:N1} MB\nShims: {manifest["bin"]}\nShortcuts: {manifest["shortcuts"]}\nInstalled versions: {string.Join(", ", w.Versions)}";
        await c.ShowReportAsync("Package details · " + a.Name, w.Details);
    }
    private async Task Download(CancellationToken ct)
    {
        var selected = active?.Search ?? w.SearchSelection.ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select one or more search results.");
        var root = Root();
        if (!string.Equals(Path.GetFullPath(w.Root), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The Scoop root changed. Search again.");
        foreach (var a in selected) { ScoopService.SafeSegment(a.Name); ScoopService.SafeSegment(a.Bucket); }
        var installed = await Task.Run(() => ScoopService.Installed(root, ct), ct);
        var blocked = selected.Where(a => installed.Any(i => string.Equals(i.Name, a.Name, StringComparison.OrdinalIgnoreCase))).Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (blocked.Length > 0) throw new InvalidOperationException("Already installed: " + string.Join(", ", blocked) + ". Remove these packages from the selection.");
        await Run(root, ["download", .. selected.Select(a => a.Identity).Distinct(StringComparer.OrdinalIgnoreCase)], ct);
    }
    private async Task Install(CancellationToken ct)
    {
        var selected = active?.Search ?? w.SearchSelection.ToArray(); if (selected.Length == 0) throw new InvalidOperationException("Select one or more search results.");
        var preview = new List<string>();
        foreach (var a in selected)
        {
            var m = ScoopService.Read(a.ManifestPath);
            preview.Add($"{a.Identity} · {a.Version} · {a.Status}\n{a.Description}\nHomepage: {a.Homepage}\nLicense: {a.License}\nDependencies: {m["depends"]}\nDownload: {m["url"] ?? m["architecture"]}\n");
        }
        await Confirm("Install preview", string.Join("\n", preview) + "\nScoop installs dependencies automatically and reports download sizes when it starts. Install these packages?");
        var root = Root(); await Run(root, ["install", .. selected.Select(a => a.Identity)], ct); await Refresh(ct);
    }
    private static string SuggestCustomName(string text)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9_.+-]+", "-").Trim('-', '.');
        return name.Length > 0 && char.IsAsciiLetterOrDigit(name[0]) ? name : "";
    }
    private async Task AddCustom(CancellationToken ct)
    {
        string F(string key) => (active?.Fields[key] ?? w.Field(key).Value).Trim();
        var root = Root(); var name = ScoopService.SafeSegment(F("name")); var version = ScoopService.SafeSegment(F("version"));
        var source = Path.GetFullPath(F("folder")).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(source) || source == Path.GetPathRoot(source)?.TrimEnd(Path.DirectorySeparatorChar)) throw new ArgumentException("Choose an existing app folder, not a drive root.");
        var exe = ScoopService.Within(source, F("exe"));
        if (!File.Exists(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an executable inside the app folder.");
        var app = Path.Combine(root, "apps", name); var target = Path.Combine(app, version);
        if (Directory.Exists(app) || File.Exists(app)) throw new IOException("An app with this name already exists. Choose a new name.");
        if (source.Equals(root, StringComparison.OrdinalIgnoreCase) || source.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || root.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an app folder outside your Scoop installation.");
        var relativeExe = Path.GetRelativePath(source, exe).Replace('\\', '/');
        var shim = F("shim"); if (shim.Length > 0) ScoopService.SafeSegment(shim);
        var shortcut = F("shortcut"); if (shortcut.Length == 0) shortcut = name; ScoopService.SafeSegment(shortcut.Replace(' ', '_'));
        var method = F("method"); if (method is not ("Copy" or "Move")) throw new ArgumentException("Choose Copy or Move.");
        var move = method == "Move";
        await Task.Run(() => ScoopService.ValidateTree(source, ct), ct);
        foreach (var reserved in new[] { "manifest.json", "install.json", "scoop-manifest.json", "scoop-install.json" }) if (File.Exists(Path.Combine(source, reserved))) throw new IOException("Source contains Scoop metadata: " + reserved + ". Choose the original portable app folder.");
        await Confirm("Add custom app", $"{(move ? "Move" : "Copy")} {source}\nto {target}\nExecutable: {relativeExe}\nCreate shim and Start Menu shortcut?" + (move ? "\nThe original folder will be moved. Keep a backup if needed." : "\nThe original folder will be kept."));
        var manifest = ScoopService.CustomManifest(version, F("description"), relativeExe, shim, shortcut);
        var moved = false; var owned = false;
        try
        {
            Directory.CreateDirectory(app); owned = true;
            if (move && Path.GetPathRoot(source)!.Equals(Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)) { Directory.Move(source, target); moved = true; }
            else await Task.Run(() => ScoopService.CopyTree(source, target, ct), ct);
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(target, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(target, "install.json"), new JsonObject { ["architecture"] = Environment.Is64BitOperatingSystem ? "64bit" : "32bit", ["url"] = Path.Combine(target, "manifest.json").Replace('\\', '/') }.ToJsonString());
            await ScoopService.RunScript("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + ScoopService.Literal(Path.Combine(app, "current")) + " -Target " + ScoopService.Literal(target) + " | Out-Null", ct, log);
        }
        catch
        {
            // Only our newly created paths are touched. Never recursively traverse the junction.
            if (owned)
            {
                var junction = Path.Combine(app, "current"); if (Directory.Exists(junction)) Directory.Delete(junction);
                if (moved && !Directory.Exists(source)) { foreach (var file in new[] { "manifest.json", "install.json" }) File.Delete(Path.Combine(target, file)); Directory.Move(target, source); }
                else if (Directory.Exists(target)) Directory.Delete(target, true);
                if (Directory.Exists(app) && !Directory.EnumerateFileSystemEntries(app).Any()) Directory.Delete(app);
            }
            throw;
        }
        // Once registered, preserve the new app on reset failure so it can be repaired with Reset.
        try { await Run(root, ["reset", name], ct); }
        catch { c.Log("Scoop", "Custom app retained at " + target + ". Use Reset to recreate shims/shortcuts. Original folder: " + source); throw; }
        if (move && !moved)
        {
            // Cross-volume move completes only after successful registration.
            await Task.Run(() => { ScoopService.ValidateTree(source, ct); ct.ThrowIfCancellationRequested(); Directory.Delete(source, true); }, ct);
        }
        await Refresh(ct);
    }
    private Dictionary<string, string[]> Rules() => File.Exists(rulesFile) ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(rulesFile)) ?? new() : new();
    private void WriteRules(Dictionary<string, string[]> rules) { File.WriteAllText(rulesFile + ".tmp", JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true })); File.Move(rulesFile + ".tmp", rulesFile, true); }
    private static string[] Entries(string text) => text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private async Task SavePaths(CancellationToken ct)
    {
        var a = One(); var entries = Entries(active?.Paths ?? w.PathEntries); var root = Root();
        foreach (var entry in entries) ValidateEntry(a, entry);
        await Confirm("Save PATH rules", $"Apply PATH entries to {a.Name}, update its manifest and run Scoop reset?\n" + string.Join("\n", entries));
        var rules = Rules(); var old = rules.GetValueOrDefault(a.Name) ?? [];
        var shared = rules.Where(p => p.Key != a.Name).SelectMany(p => p.Value).Where(Path.IsPathRooted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ChangeAbsolutePaths(entries.Where(Path.IsPathRooted), old.Where(Path.IsPathRooted).Where(e => !entries.Contains(e, StringComparer.OrdinalIgnoreCase) && !shared.Contains(e)));
        rules[a.Name] = entries; WriteRules(rules);
        ApplyRelative(a, entries.Where(e => !Path.IsPathRooted(e)).ToArray()); await Run(root, ["reset", a.Name], ct); await Refresh(ct);
    }
    private static void ValidateEntry(ScoopPackage a, string entry)
    {
        if (entry.Contains(';') || entry.Contains('\0')) throw new ArgumentException("PATH entries cannot contain semicolons.");
        var target = Path.IsPathRooted(entry) ? Path.GetFullPath(entry) : entry == "." ? ScoopService.Current(a.Directory) : ScoopService.Within(ScoopService.Current(a.Directory), Path.Combine(ScoopService.Current(a.Directory), entry));
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
    }
    private bool ApplyRelative(ScoopPackage a, string[] relative)
    {
        if (!File.Exists(a.ManifestPath)) throw new FileNotFoundException("Installed manifest is missing.", a.ManifestPath);
        var manifest = JsonNode.Parse(File.ReadAllText(a.ManifestPath)) as JsonObject ?? throw new InvalidDataException("Installed manifest must be a JSON object.");
        if (ScoopService.Strings(manifest["env_add_path"]).SequenceEqual(relative)) return false;
        File.Copy(a.ManifestPath, a.ManifestPath + ".devtools-backup", true);
        manifest["env_add_path"] = new JsonArray(relative.Select(e => (JsonNode?)JsonValue.Create(e.Replace('\\', '/'))).ToArray());
        File.WriteAllText(a.ManifestPath + ".tmp", manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); File.Move(a.ManifestPath + ".tmp", a.ManifestPath, true); return true;
    }
    private void ChangeAbsolutePaths(IEnumerable<string> add, IEnumerable<string> remove)
    {
        var additions = add.ToArray(); var removals = remove.ToArray();
        if (additions.Length == 0 && removals.Length == 0) return;
        // Track only additions made by this manager; pre-existing user PATH entries are never owned.
        var ownedFile = Path.Combine(c.DataDirectory, "scoop-owned-paths.json");
        var owned = File.Exists(ownedFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ownedFile)) ?? [] : [];
        owned = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
        var current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        static string Normalize(string s) => Environment.ExpandEnvironmentVariables(s).TrimEnd('\\');
        foreach (var entry in removals) if (owned.Remove(entry)) parts.RemoveAll(p => Normalize(p).Equals(Normalize(entry), StringComparison.OrdinalIgnoreCase));
        foreach (var entry in additions) if (!parts.Any(p => Normalize(p).Equals(Normalize(entry), StringComparison.OrdinalIgnoreCase))) { parts.Add(entry); owned.Add(entry); }
        var updated = string.Join(';', parts);
        if (current != updated) { c.Save("scoop.pathBackup", current); Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User); WindowsEnvironment.Broadcast(); }
        File.WriteAllText(ownedFile, JsonSerializer.Serialize(owned));
    }
    private async Task SyncPaths(string root, CancellationToken ct)
    {
        var rules = Rules(); var installed = await Task.Run(() => ScoopService.Installed(root, ct), ct);
        var retained = rules.Where(p => installed.Any(a => a.Name == p.Key)).ToDictionary(p => p.Key, p => p.Value);
        var keptAbsolute = retained.SelectMany(p => p.Value).Where(Path.IsPathRooted).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ChangeAbsolutePaths(keptAbsolute, rules.Where(p => !retained.ContainsKey(p.Key)).SelectMany(p => p.Value).Where(Path.IsPathRooted).Where(e => !keptAbsolute.Contains(e)));
        foreach (var pair in retained)
        {
            ct.ThrowIfCancellationRequested(); var a = installed.Single(a => a.Name == pair.Key);
            foreach (var e in pair.Value) ValidateEntry(a, e);
            if (ApplyRelative(a, pair.Value.Where(e => !Path.IsPathRooted(e)).ToArray())) await Run(root, ["reset", a.Name], ct);
        }
        WriteRules(retained);
    }
    private sealed class Command(Action execute, Action<Exception> error) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { try { execute(); } catch (Exception e) { error(e); } }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
