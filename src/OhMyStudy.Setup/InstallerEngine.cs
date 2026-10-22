using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyStudy.Setup;

public enum InstallPhase { Staged, BeforeSwitch, AfterSwitch, Integrated }
public sealed record InstallOptions(string Source, string Root, bool Integrate = true, Action<InstallPhase>? Checkpoint = null,
    Action? BootstrapCopyCheckpoint = null);
public sealed record InstallResult(string Root, string? ActiveVersion, bool Upgraded, bool DataPreserved = true);
public sealed record PackageFile(string Path, long Bytes, string Sha256);
public sealed record PackageManifest(int SchemaVersion, string Architecture, string EntryPoint, List<PackageFile> Files, string? Version = null);
public sealed record InstallMarker(string Product, string BootstrapSha256, bool Integrated, List<string> Versions);
public sealed record ActiveInstallation(string Version, string EntryPoint);
internal sealed record UninstallVersion(string Version, byte[] ManifestBytes);
internal sealed record UninstallJournal(string Product, byte[] MarkerBytes, List<UninstallVersion> Versions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(InstallMarker))]
[JsonSerializable(typeof(ActiveInstallation))]
[JsonSerializable(typeof(InstallResult))]
[JsonSerializable(typeof(UninstallJournal))]
internal partial class SetupJson : JsonSerializerContext;

/// <summary>Per-user immutable version directories, verified manifests, and one atomic active pointer.</summary>
public sealed class InstallerEngine
{
    public const string Product = "OhMyStudy.PerUser.1";
    public const string BootstrapName = "OhMyStudy.Setup.exe";
    public const string ManifestName = "package-manifest.json";
    private const string MarkerName = "installation.json";
    private const string ActiveName = "active.json";
    private const string UninstallName = "uninstall.json";
    private readonly IInstallationIntegration integration;
    public InstallerEngine(IInstallationIntegration? integration = null) => this.integration = integration ?? new WindowsInstallationIntegration();
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OhMyStudy");

    public InstallResult Install(InstallOptions options)
    {
        string source = SafePaths.Root(options.Source), root = SafePaths.Root(options.Root);
        if (SafePaths.Within(source, root) || SafePaths.Within(root, source)) throw new InvalidDataException("The installation location must not overlap the installation source folder.");
        var package = ReadPackage(source);
        if (!package.Manifest.Files.Any(file => file.Path.Equals(BootstrapName, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Installation files are incomplete. Extract them again.");
        VerifyFiles(source, package.Manifest);
        SafePaths.AssertUnlinked(root);
        bool exists = Directory.Exists(root);
        if (exists && !File.Exists(Path.Combine(root, MarkerName)) && Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".install.lock"))
            throw new InvalidDataException("The installation location contains other files. Select an empty folder.");
        Directory.CreateDirectory(root);
        using var installLock = Lock(root);
        if (File.Exists(Path.Combine(root, UninstallName))) throw new InvalidDataException("The previous uninstall is incomplete. Run it again before installing.");
        string markerPath = Path.Combine(root, MarkerName), activePath = Path.Combine(root, ActiveName);
        byte[]? oldMarker = ReadOptional(markerPath), oldActive = ReadOptional(activePath);
        InstallMarker? marker = oldMarker is null ? null : DeserializeMarker(oldMarker);
        if (marker is not null) ValidateMarker(root, marker);
        if (marker is not null && marker.Integrated != options.Integrate) throw new InvalidDataException("Use the original installation mode to upgrade.");
        string version = package.Hash;
        string versions = SafePaths.Child(root, "versions");
        SafePaths.AssertUnlinked(versions);
        Directory.CreateDirectory(versions);
        string final = SafePaths.Child(versions, version);
        string staging = SafePaths.Child(versions, ".staging-" + Guid.NewGuid().ToString("N"));
        var created = new List<string>();
        bool installedVersion = false, installedBootstrap = false;
        IDisposable? integrationTransaction = null;
        try
        {
            if (!Directory.Exists(final))
            {
                Directory.CreateDirectory(staging);
                foreach (var file in package.Manifest.Files)
                {
                    string input = SafePaths.Child(source, file.Path), target = SafePaths.Child(staging, file.Path);
                    SafePaths.AssertUnlinked(input);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var from = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { created.Add(target); from.CopyTo(to); to.Flush(flushToDisk: true); }
                    VerifyFile(target, file);
                }
                string manifestTarget = Path.Combine(staging, ManifestName);
                File.WriteAllBytes(manifestTarget, package.Bytes); created.Add(manifestTarget);
                options.Checkpoint?.Invoke(InstallPhase.Staged);
                SafePaths.AssertUnlinked(staging);
                RetryFileOperation(() => Directory.Move(staging, final));
                installedVersion = true;
            }
            else VerifyInstalledVersion(final, version);

            var bootstrap = package.Manifest.Files.Single(file => file.Path.Equals(BootstrapName, StringComparison.OrdinalIgnoreCase));
            if (marker is null) marker = new(Product, bootstrap.Sha256.ToLowerInvariant(), options.Integrate, []);
            string bootstrapPath = SafePaths.Child(root, BootstrapName);
            if (!File.Exists(bootstrapPath))
            {
                CopyNew(Path.Combine(final, BootstrapName), bootstrapPath, options.BootstrapCopyCheckpoint);
                installedBootstrap = true;
                marker = marker with { BootstrapSha256 = bootstrap.Sha256.ToLowerInvariant() };
            }
            else VerifyFile(bootstrapPath, new(BootstrapName, new FileInfo(bootstrapPath).Length, marker.BootstrapSha256));
            var tracked = marker.Versions.Append(version).Distinct(StringComparer.Ordinal).ToList();
            AtomicWrite(markerPath, JsonSerializer.SerializeToUtf8Bytes(marker with { Versions = tracked }, SetupJson.Default.InstallMarker));
            options.Checkpoint?.Invoke(InstallPhase.BeforeSwitch);
            AtomicWrite(activePath, JsonSerializer.SerializeToUtf8Bytes(new ActiveInstallation(version, "OhMyStudy.App.exe"), SetupJson.Default.ActiveInstallation));
            options.Checkpoint?.Invoke(InstallPhase.AfterSwitch);
            if (options.Integrate) integrationTransaction = integration.Apply(root, package.Manifest.Version ?? "1.0", marker.BootstrapSha256);
            options.Checkpoint?.Invoke(InstallPhase.Integrated);
            if (integrationTransaction is IIntegrationTransaction transaction) transaction.Commit();
            return new(root, version, oldActive is not null);
        }
        catch
        {
            Restore(activePath, oldActive); Restore(markerPath, oldMarker);
            if (installedVersion) DeleteVersion(final, version);
            else RemoveCreated(staging, created);
            if (installedBootstrap) File.Delete(Path.Combine(root, BootstrapName));
            if (Directory.Exists(versions) && !Directory.EnumerateFileSystemEntries(versions).Any()) Directory.Delete(versions);
            throw;
        }
        finally { integrationTransaction?.Dispose(); }
    }

    public InstallResult Uninstall(string installRoot, Action<int>? afterFileDeleted = null)
    {
        string root = SafePaths.Root(installRoot);
        SafePaths.AssertUnlinked(root);
        using var installLock = Lock(root);
        string journalPath = SafePaths.Child(root, UninstallName), markerPath = SafePaths.Child(root, MarkerName);
        SafePaths.AssertUnlinked(journalPath);
        UninstallJournal journal;
        if (File.Exists(journalPath))
        {
            if (new FileInfo(journalPath).Length > 67_108_864) throw new InvalidDataException("Invalid uninstall record.");
            journal = JsonSerializer.Deserialize(File.ReadAllBytes(journalPath), SetupJson.Default.UninstallJournal) ?? throw new InvalidDataException("Invalid uninstall record.");
            if (journal.Product != Product || journal.MarkerBytes is null || journal.Versions is null || journal.Versions.Count > 1000)
                throw new InvalidDataException("Invalid uninstall record.");
            if (File.Exists(markerPath) && !File.ReadAllBytes(markerPath).AsSpan().SequenceEqual(journal.MarkerBytes))
                throw new InvalidDataException("The installation record has changed. Uninstall stopped.");
        }
        else
        {
            var ownership = ReadMarker(root); ValidateMarker(root, ownership);
            var versions = new List<UninstallVersion>();
            foreach (string version in ownership.Versions)
            {
                string directory = SafePaths.Child(root, "versions/" + version);
                if (Directory.Exists(directory)) versions.Add(new(version, ReadPackage(directory).Bytes));
            }
            journal = new(Product, File.ReadAllBytes(markerPath), versions);
        }
        var marker = DeserializeMarker(journal.MarkerBytes); ValidateMarker(root, marker);
        string bootstrapPath = SafePaths.Child(root, BootstrapName);
        if (File.Exists(bootstrapPath)) VerifyFile(bootstrapPath, new(BootstrapName, new FileInfo(bootstrapPath).Length, marker.BootstrapSha256));
        var tracked = new List<(string Directory, string Version, List<string> Files)>();
        foreach (var recorded in journal.Versions)
        {
            string version = recorded.Version;
            if (!IsHash(version) || !marker.Versions.Contains(version, StringComparer.Ordinal)) throw new InvalidDataException("Invalid uninstall record.");
            string directory = SafePaths.Child(root, "versions/" + version);
            var package = ParsePackage(directory, recorded.ManifestBytes);
            if (package.Hash != version) throw new InvalidDataException("The uninstall manifest has changed. Operation stopped.");
            foreach (var file in package.Manifest.Files)
            {
                string path = SafePaths.Child(directory, file.Path);
                if (File.Exists(path)) VerifyFile(path, file);
            }
            string manifestPath = Path.Combine(directory, ManifestName);
            if (File.Exists(manifestPath)) VerifyFile(manifestPath, new(ManifestName, recorded.ManifestBytes.Length, version));
            tracked.Add((directory, version, package.Manifest.Files.Select(file => SafePaths.Child(directory, file.Path)).Append(manifestPath).ToList()));
        }
        // Check every known product file before the first deletion. Never delete an unlisted user file.
        foreach (var file in tracked.SelectMany(item => item.Files).Append(Path.Combine(root, BootstrapName)))
        {
            SafePaths.AssertUnlinked(file);
            if (File.Exists(file)) using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        }
        if (!File.Exists(journalPath))
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, SetupJson.Default.UninstallJournal);
            if (bytes.Length > 67_108_864) throw new InvalidDataException("The uninstall record exceeds the limit.");
            AtomicWrite(journalPath, bytes);
        }
        if (marker.Integrated) integration.Remove(root);
        int deleted = 0;
        void DeleteTracked(string path)
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            afterFileDeleted?.Invoke(++deleted);
        }
        foreach (var version in tracked)
        {
            foreach (string file in version.Files) DeleteTracked(file);
            if (Directory.Exists(version.Directory)) RemoveEmptyDirectories(version.Directory);
        }
        DeleteTracked(Path.Combine(root, BootstrapName));
        DeleteTracked(Path.Combine(root, ActiveName));
        DeleteTracked(markerPath);
        string versionsRoot = Path.Combine(root, "versions");
        if (Directory.Exists(versionsRoot) && !Directory.EnumerateFileSystemEntries(versionsRoot).Any()) Directory.Delete(versionsRoot);
        File.Delete(journalPath);
        return new(root, null, false);
    }

    public string ResolveLaunch(string installRoot)
    {
        string root = SafePaths.Root(installRoot); SafePaths.AssertUnlinked(root);
        if (File.Exists(Path.Combine(root, UninstallName))) throw new InvalidDataException("Uninstall is incomplete. Run it again.");
        var marker = ReadMarker(root); ValidateMarker(root, marker);
        string activePath = SafePaths.Child(root, ActiveName); SafePaths.AssertUnlinked(activePath);
        var active = JsonSerializer.Deserialize(File.ReadAllBytes(activePath), SetupJson.Default.ActiveInstallation) ?? throw new InvalidDataException("Reinstall the app.");
        if (!IsHash(active.Version) || !marker.Versions.Contains(active.Version, StringComparer.Ordinal) || active.EntryPoint != "OhMyStudy.App.exe") throw new InvalidDataException("Reinstall the app.");
        string version = SafePaths.Child(root, "versions/" + active.Version);
        var package = ReadPackage(version);
        if (package.Hash != active.Version) throw new InvalidDataException("Installation files have changed. Reinstall the app.");
        string executable = SafePaths.Child(version, active.EntryPoint);
        VerifyFile(executable, package.Manifest.Files.Single(file => file.Path == active.EntryPoint));
        return executable;
    }

    private static FileStream Lock(string root)
    {
        string path = SafePaths.Child(root, ".install.lock"); SafePaths.AssertUnlinked(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static InstallMarker ReadMarker(string root)
    {
        string path = SafePaths.Child(root, MarkerName); SafePaths.AssertUnlinked(path);
        return DeserializeMarker(File.ReadAllBytes(path));
    }
    private static InstallMarker DeserializeMarker(byte[] bytes)
    {
        if (bytes.Length > 1_048_576) throw new InvalidDataException("Invalid installation record.");
        return JsonSerializer.Deserialize(bytes, SetupJson.Default.InstallMarker) ?? throw new InvalidDataException("Invalid installation record.");
    }
    private static void ValidateMarker(string root, InstallMarker marker)
    {
        if (marker.Product != Product || !IsHash(marker.BootstrapSha256) || marker.Versions is null || marker.Versions.Count > 1000 || marker.Versions.Any(version => !IsHash(version)))
            throw new InvalidDataException("This folder is not an Oh My Study installation.");
        SafePaths.AssertUnlinked(root, recursive: true);
    }
    private static PackageManifest VerifyInstalledVersion(string directory, string version)
    {
        var package = ReadPackage(directory);
        if (package.Hash != version) throw new InvalidDataException("The installation record has changed. Operation stopped.");
        VerifyFiles(directory, package.Manifest);
        return package.Manifest;
    }
    private static (PackageManifest Manifest, byte[] Bytes, string Hash) ReadPackage(string source)
    {
        SafePaths.AssertUnlinked(source, recursive: true);
        string path = SafePaths.Child(source, ManifestName);
        long length = new FileInfo(path).Length;
        if (length is <= 0 or > 16_777_216) throw new InvalidDataException("Invalid installation manifest.");
        byte[] bytes = File.ReadAllBytes(path);
        return ParsePackage(source, bytes);
    }
    private static (PackageManifest Manifest, byte[] Bytes, string Hash) ParsePackage(string source, byte[] bytes)
    {
        if (bytes.Length is <= 0 or > 16_777_216) throw new InvalidDataException("Invalid installation manifest.");
        var manifest = JsonSerializer.Deserialize(bytes, SetupJson.Default.PackageManifest) ?? throw new InvalidDataException("Invalid installation manifest.");
        if (manifest.SchemaVersion != 1 || manifest.Architecture != "win-x64" || manifest.EntryPoint != "OhMyStudy.App.exe" || manifest.Files is null || manifest.Files.Count is 0 or > 50_000)
            throw new InvalidDataException("Unsupported installation manifest.");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            _ = SafePaths.Child(source, file.Path);
            if (!paths.Add(file.Path) || file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || !IsHash(file.Sha256) || file.Bytes is < 0 or > 2_147_483_648 || (total += file.Bytes) > 8_589_934_592)
                throw new InvalidDataException("Invalid installation manifest.");
        }
        if (!paths.Contains(manifest.EntryPoint)) throw new InvalidDataException("Application files are missing.");
        return (manifest, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    private static void VerifyFiles(string source, PackageManifest manifest)
    {
        foreach (var file in manifest.Files) VerifyFile(SafePaths.Child(source, file.Path), file);
    }
    private static void VerifyFile(string path, PackageFile file)
    {
        SafePaths.AssertUnlinked(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != file.Bytes || !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installation file verification failed. Extract or download the files again.");
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));
    private static byte[]? ReadOptional(string path) { SafePaths.AssertUnlinked(path); return File.Exists(path) ? File.ReadAllBytes(path) : null; }
    private static void Restore(string path, byte[]? content) { if (content is null) File.Delete(path); else AtomicWrite(path, content); }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        SafePaths.AssertUnlinked(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            RetryFileOperation(() => File.Move(temporary, path, overwrite: true));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void CopyNew(string source, string target, Action? checkpoint = null)
    {
        SafePaths.AssertUnlinked(target);
        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? to = null;
        bool created = false;
        try
        {
            to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            byte[] first = new byte[(int)Math.Min(4096, Math.Max(1, from.Length / 2))];
            int count = from.Read(first);
            to.Write(first.AsSpan(0, count)); to.Flush(true);
            checkpoint?.Invoke();
            from.CopyTo(to); to.Flush(true);
        }
        catch
        {
            to?.Dispose();
            // Only our successful CreateNew establishes ownership of a partial file.
            if (created) File.Delete(target);
            throw;
        }
        finally { to?.Dispose(); }
    }
    private static void RetryFileOperation(Action operation)
    {
        // Antivirus/indexing can briefly hold freshly written executables; never weaken sharing or ownership checks.
        for (int attempt = 0; ; attempt++)
            try { operation(); return; }
            catch (IOException error) when (attempt < 5 && (error.HResult & 0xffff) is 5 or 32 or 33) { Thread.Sleep(100 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(100 * (attempt + 1)); }
    }
    private static void DeleteVersion(string directory, string version)
    {
        var package = VerifyInstalledVersion(directory, version);
        foreach (var file in package.Files) File.Delete(SafePaths.Child(directory, file.Path));
        File.Delete(Path.Combine(directory, ManifestName));
        RemoveEmptyDirectories(directory);
    }
    private static void RemoveCreated(string directory, IEnumerable<string> created)
    {
        if (!Directory.Exists(directory)) return;
        SafePaths.AssertUnlinked(directory, recursive: true);
        foreach (string file in created) if (File.Exists(file)) File.Delete(file);
        RemoveEmptyDirectories(directory);
    }
    private static void RemoveEmptyDirectories(string directory)
    {
        SafePaths.AssertUnlinked(directory, recursive: true);
        foreach (string child in Directory.EnumerateDirectories(directory)) RemoveEmptyDirectories(child);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, recursive: false);
    }
}

public static class SafePaths
{
    public static string Root(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Use a fully qualified folder path.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A drive root cannot be used.");
        return root;
    }
    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string Child(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 1024 || relative.Contains('\\') || relative.Contains(':') || Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid installation file path.");
        foreach (string segment in relative.Split('/'))
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsDeviceName(segment)) throw new InvalidDataException("Invalid installation file path.");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(full, root) || full.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installation file path is outside the folder.");
        return full;
    }
    private static bool IsDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '0' and <= '9';
    }
    public static void AssertUnlinked(string path, bool recursive = false)
    {
        string full = Path.GetFullPath(path);
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The installation location contains links. Use a regular folder."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (recursive && Directory.Exists(full))
            foreach (string child in Directory.EnumerateFileSystemEntries(full))
            {
                AssertUnlinked(child);
                if (Directory.Exists(child)) AssertUnlinked(child, recursive: true);
            }
    }
}

public interface IIntegrationTransaction : IDisposable { void Commit(); }
public interface IInstallationIntegration
{
    IIntegrationTransaction Apply(string root, string version, string bootstrapHash);
    void Remove(string root);
}
