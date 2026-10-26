using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using DevTools.Contracts;

namespace DevTools.Modules.Windows;

public sealed record KnownFolderDefinition(string Id, string Name, Guid Guid, string Symbol);
public interface IFolderLocationBackend
{
    string Read(KnownFolderDefinition folder, bool defaultPath = false);
    void Set(KnownFolderDefinition folder, string path);
}
public sealed class WindowsFolderLocationBackend : IFolderLocationBackend
{
    public string Read(KnownFolderDefinition folder, bool defaultPath = false)
    {
        var id = folder.Guid;
        var result = SHGetKnownFolderPath(ref id, 0x4000u | (defaultPath ? 0x400u : 0u), 0, out var pointer);
        if (result < 0)
        {
            if (pointer != 0) Marshal.FreeCoTaskMem(pointer);
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.Name);
            if (defaultPath) return fallback;
            var name = folder.Id switch { "desktop" => "Desktop", "documents" => "Personal", "downloads" => "{" + folder.Guid.ToString().ToUpperInvariant() + "}", "pictures" => "My Pictures", "music" => "My Music", "videos" => "My Video", _ => "" };
            var saved = ModuleTools.ReadRegistry(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", name)?.ToString();
            return string.IsNullOrWhiteSpace(saved) ? fallback : Environment.ExpandEnvironmentVariables(saved);
        }
        try { return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Windows returned an empty folder path."); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    public void Set(KnownFolderDefinition folder, string path) { var id = folder.Guid; Marshal.ThrowExceptionForHR(SHSetKnownFolderPath(ref id, 0, 0, path)); }
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHSetKnownFolderPath(ref Guid id, uint flags, nint token, string path);
}
public sealed class FolderLocationService(IFolderLocationBackend backend)
{
    public static readonly KnownFolderDefinition[] Definitions = [
        new("desktop", "Desktop", new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), "\uE7F4"),
        new("documents", "Documents", new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), "\uE8A5"),
        new("downloads", "Downloads", new("374DE290-123F-4565-9164-39C4925E467B"), "\uE896"),
        new("pictures", "Pictures", new("33E28130-4E1E-4676-835A-98395C3BC3BB"), "\uE91B"),
        new("music", "Music", new("4BD8D571-6D19-48D3-BE97-422220080E43"), "\uE8D6"),
        new("videos", "Videos", new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), "\uE714")];
    public List<FolderItem> Read() => Definitions.Select(d => { var current = backend.Read(d); return new FolderItem(d.Id, d.Name, current, backend.Read(d, true), d.Symbol, CloudManaged: IsCloudPath(current)); }).ToList();
    public static bool IsCloudPath(string path) => new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable).Where(p => !string.IsNullOrWhiteSpace(p)).Any(p => Same(path, p!) || Within(p!, path));
    public static bool Same(string first, string second) => Path.GetFullPath(first).TrimEnd('\\').Equals(Path.GetFullPath(second).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static bool Within(string parent, string child) => Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public static string Validate(FolderItem item, string destination, IEnumerable<FolderItem> others, bool move)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Choose a full local folder path.");
        var target = Path.GetFullPath(destination).TrimEnd('\\'); var source = Path.GetFullPath(item.CurrentPath).TrimEnd('\\');
        if (target.StartsWith("\\\\") || source.StartsWith("\\\\")) throw new ArgumentException("Use a local drive for folder changes.");
        if (Same(source, target) || Within(source, target) || Within(target, source)) throw new ArgumentException("Source and destination must be separate folders, with neither inside the other.");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Same(source, Path.GetPathRoot(source)!) || Same(target, Path.GetPathRoot(target)!) || Same(source, profile) || Same(target, profile)) throw new ArgumentException("Choose a specific folder, not a drive root or the whole user profile.");
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var protectedPath = Environment.GetFolderPath(special); if (protectedPath.Length == 0) continue;
            if (Same(source, protectedPath) || Within(protectedPath, source) || Same(target, protectedPath) || Within(protectedPath, target)) throw new ArgumentException("Windows, application and application-data folders cannot be relocated here.");
        }
        foreach (var other in others.Where(f => f.Id != item.Id && !f.IsCustom))
        {
            if (Same(other.CurrentPath, target) || Within(other.CurrentPath, target) || Within(target, other.CurrentPath)) throw new ArgumentException("The destination overlaps the " + other.Name + " folder.");
            if (item.IsCustom && (Same(source, other.CurrentPath) || Within(source, other.CurrentPath))) throw new ArgumentException("Use the Windows entry for " + other.Name + "; a custom entry cannot move a Windows folder or its parent.");
        }
        CheckAncestors(source); CheckAncestors(target);
        if (item.CloudManaged) throw new InvalidOperationException("This folder is managed by OneDrive. Stop its folder backup in OneDrive before changing its location here.");
        if (File.Exists(target)) throw new IOException("The destination is a file.");
        if (move && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("For file moves, choose an empty destination to avoid merging or overwriting files.");
        if (move && !Directory.Exists(source)) throw new DirectoryNotFoundException("The source folder does not exist: " + source);
        return target;
    }
    private static void CheckAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Choose folders without junctions or symbolic links in their path.");
    }
    private sealed record FileSnapshot(string Relative, long Length, long Modified);
    private static List<FileSnapshot> Inventory(string source, CancellationToken ct)
    {
        var result = new List<FileSnapshot>();
        void Scan(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("File moves cannot include linked or cloud-placeholder items: " + entry.FullName);
                if (entry is DirectoryInfo folder) Scan(folder.FullName);
                else if (entry is FileInfo file) result.Add(new(Path.GetRelativePath(source, file.FullName), file.Length, file.LastWriteTimeUtc.Ticks));
            }
        }
        Scan(source); return result;
    }
    public async Task Change(FolderItem item, string target, bool move, string dataDirectory, Action<string> saveCustomLocation, CancellationToken ct, IProgress<string> log)
    {
        target = Validate(item, target, Read(), move);
        Directory.CreateDirectory(dataDirectory);
        var journal = Path.Combine(dataDirectory, "folder-change-" + Guid.NewGuid().ToString("N") + ".json");
        var stage = "Prepared"; var copied = new List<string>(); var removed = new List<string>();
        void Record() { File.WriteAllText(journal + ".tmp", JsonSerializer.Serialize(new { item.Id, item.Name, Source = item.CurrentPath, Target = target, MoveFiles = move, Stage = stage, CopiedFiles = copied, RemovedOriginalFiles = removed }, new JsonSerializerOptions { WriteIndented = true })); File.Move(journal + ".tmp", journal, true); }
        Record();
        try
        {
            ct.ThrowIfCancellationRequested();
            var files = move ? await Task.Run(() => Inventory(item.CurrentPath, ct), ct) : [];
            if (move && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("The destination changed and is no longer empty.");
            Directory.CreateDirectory(target);
            var hashes = new Dictionary<string, byte[]>();
            if (move)
            {
                stage = "Copying; original files retained"; Record();
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var sourceFile = Path.Combine(item.CurrentPath, file.Relative); var destination = Path.Combine(target, file.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) await input.CopyToAsync(output, ct);
                    File.SetLastWriteTimeUtc(destination, new DateTime(file.Modified, DateTimeKind.Utc));
                    await using var original = File.OpenRead(sourceFile); var sourceHash = await SHA256.HashDataAsync(original, ct);
                    await using var duplicate = File.OpenRead(destination); var destinationHash = await SHA256.HashDataAsync(duplicate, ct);
                    if (!sourceHash.SequenceEqual(destinationHash)) throw new IOException("File verification failed: " + file.Relative);
                    hashes[file.Relative] = sourceHash; copied.Add(file.Relative); Record();
                    log.Report($"Verified {copied.Count}/{files.Count}: {file.Relative}");
                }
                var latest = Inventory(item.CurrentPath, ct);
                if (!files.OrderBy(f => f.Relative).SequenceEqual(latest.OrderBy(f => f.Relative))) throw new IOException("The source changed during copying. Originals were retained; review the journal and copied data.");
                // Preserve empty directories too. Inventory has already rejected links.
                foreach (var directory in Directory.EnumerateDirectories(item.CurrentPath, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) { ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(item.CurrentPath, directory))); }
            }
            ct.ThrowIfCancellationRequested(); stage = "Copied; updating location"; Record();
            if (item.IsCustom) saveCustomLocation(target);
            else
            {
                var definition = Definitions.Single(d => d.Id == item.Id);
                if (!Same(backend.Read(definition), item.CurrentPath)) throw new IOException("Windows folder location changed externally. Refresh before trying again.");
                backend.Set(definition, target);
                if (!Same(backend.Read(definition), target)) throw new IOException("Windows did not confirm the new location. Original files were retained.");
            }
            stage = "Location updated; removing verified original files"; Record();
            if (move)
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var originalPath = Path.Combine(item.CurrentPath, file.Relative); var destination = Path.Combine(target, file.Relative);
                    using var handle = CreateFile(originalPath, 0x80010000, 0, 0, 3, 0x08000000, 0);
                    if (handle.IsInvalid) throw new IOException("Cannot exclusively access original file: " + originalPath, new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                    await using (var original = new FileStream(handle, FileAccess.Read))
                    await using (var duplicate = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    {
                        if (!(await SHA256.HashDataAsync(original, ct)).SequenceEqual(hashes[file.Relative]) || !(await SHA256.HashDataAsync(duplicate, ct)).SequenceEqual(hashes[file.Relative])) throw new IOException("A file changed after verification. Remaining originals were retained.");
                        ct.ThrowIfCancellationRequested();
                        var disposition = new FileDisposition { Delete = true };
                        if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) throw new IOException("Could not remove the verified original file.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                    }
                    removed.Add(file.Relative); Record();
                }
                // Leave original directories in place; never recursively delete a user's folder.
            }
            stage = "Completed"; Record(); log.Report("Folder change completed. Journal: " + journal);
        }
        catch { log.Report("Folder change stopped at: " + stage + ". Original and destination locations are recorded in " + journal); throw; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref FileDisposition information, uint size);
}
