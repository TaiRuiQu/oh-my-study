using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace OhMyStudy.Setup;

internal sealed record IntegrationRecord(string Root, string LaunchSha256, string UninstallSha256);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IntegrationRecord))]
internal partial class IntegrationJson : JsonSerializerContext;

public sealed class WindowsInstallationIntegration : IInstallationIntegration
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OhMyStudy";
    private static string ShortcutDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Oh My Study");
    private static string LaunchLink => Path.Combine(ShortcutDirectory, "Oh My Study.lnk");
    private static string UninstallLink => Path.Combine(ShortcutDirectory, "卸载 Oh My Study.lnk");

    public IIntegrationTransaction Apply(string root, string version, string bootstrapHash)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ApplyWindows(root, version);
    }

    [SupportedOSPlatform("windows")]
    private static IIntegrationTransaction ApplyWindows(string root, string version)
    {
        string recordPath = Path.Combine(root, "integration.json");
        SafePaths.AssertUnlinked(recordPath); SafePaths.AssertUnlinked(ShortcutDirectory, recursive: true);
        byte[]? priorRecord = File.Exists(recordPath) ? File.ReadAllBytes(recordPath) : null;
        var prior = priorRecord is null ? null : JsonSerializer.Deserialize(priorRecord, IntegrationJson.Default.IntegrationRecord);
        if (prior is not null && !prior.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The Start menu record does not match.");
        Dictionary<string, (object, RegistryValueKind)>? registryBefore;
        using (var existing = Registry.CurrentUser.OpenSubKey(RegistryPath))
        {
            if (existing?.GetValue("InstallLocation") is string location && !location.Equals(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This user already has an installation elsewhere. Uninstall it first.");
            registryBefore = existing?.GetValueNames().ToDictionary(name => name, name => (existing.GetValue(name)!, existing.GetValueKind(name)), StringComparer.Ordinal);
        }
        var added = new List<string>();
        var transaction = new Transaction(() =>
        {
            foreach (string path in added) if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(ShortcutDirectory) && !Directory.EnumerateFileSystemEntries(ShortcutDirectory).Any()) Directory.Delete(ShortcutDirectory);
            if (priorRecord is null) File.Delete(recordPath); else InstallerEngine.AtomicWrite(recordPath, priorRecord);
            RestoreRegistry(registryBefore);
        });
        try
        {
            if (prior is null && Directory.Exists(ShortcutDirectory) && Directory.EnumerateFileSystemEntries(ShortcutDirectory).Any())
                throw new InvalidDataException("The Start menu already contains files with this name. Move them away and retry.");
            if (prior is not null)
            {
                ValidateLink(LaunchLink, prior.LaunchSha256); ValidateLink(UninstallLink, prior.UninstallSha256);
            }
            Directory.CreateDirectory(ShortcutDirectory);
            string bootstrap = Path.Combine(root, InstallerEngine.BootstrapName);
            if (!File.Exists(LaunchLink)) { ShellLinks.Create(LaunchLink, bootstrap, "--launch", root); added.Add(LaunchLink); }
            if (!File.Exists(UninstallLink)) { ShellLinks.Create(UninstallLink, bootstrap, "--uninstall", root); added.Add(UninstallLink); }
            var record = new IntegrationRecord(root, Hash(LaunchLink), Hash(UninstallLink));
            InstallerEngine.AtomicWrite(recordPath, JsonSerializer.SerializeToUtf8Bytes(record, IntegrationJson.Default.IntegrationRecord));
            using var registry = Registry.CurrentUser.CreateSubKey(RegistryPath);
            registry.SetValue("DisplayName", "Oh My Study"); registry.SetValue("DisplayVersion", version);
            registry.SetValue("Publisher", "Oh My Study"); registry.SetValue("InstallLocation", root);
            registry.SetValue("DisplayIcon", bootstrap); registry.SetValue("UninstallString", "\"" + bootstrap + "\" --uninstall");
            registry.SetValue("QuietUninstallString", "\"" + bootstrap + "\" --uninstall --quiet");
            registry.SetValue("NoModify", 1, RegistryValueKind.DWord); registry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            return transaction;
        }
        catch { transaction.Dispose(); throw; }
    }

    public void Remove(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RemoveWindows(root);
    }
    [SupportedOSPlatform("windows")]
    private static void RemoveWindows(string root)
    {
        string recordPath = Path.Combine(root, "integration.json");
        SafePaths.AssertUnlinked(recordPath); SafePaths.AssertUnlinked(ShortcutDirectory, recursive: true);
        if (File.Exists(recordPath))
        {
            var record = JsonSerializer.Deserialize(File.ReadAllBytes(recordPath), IntegrationJson.Default.IntegrationRecord);
            if (record is null || !record.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The Start menu record does not match.");
            foreach (var (path, hash) in new[] { (LaunchLink, record.LaunchSha256), (UninstallLink, record.UninstallSha256) })
                if (File.Exists(path) && Hash(path) == hash) File.Delete(path);
            File.Delete(recordPath);
        }
        if (Directory.Exists(ShortcutDirectory) && !Directory.EnumerateFileSystemEntries(ShortcutDirectory).Any()) Directory.Delete(ShortcutDirectory);
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
            if (key?.GetValue("InstallLocation") is not string location || !location.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
        Registry.CurrentUser.DeleteSubKey(RegistryPath, throwOnMissingSubKey: false);
    }
    [SupportedOSPlatform("windows")]
    private static void RestoreRegistry(Dictionary<string, (object, RegistryValueKind)>? values)
    {
        Registry.CurrentUser.DeleteSubKey(RegistryPath, false);
        if (values is null) return;
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        foreach (var pair in values) key.SetValue(pair.Key, pair.Value.Item1, pair.Value.Item2);
    }
    private static string Hash(string path) { SafePaths.AssertUnlinked(path); using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void ValidateLink(string path, string expected) { if (File.Exists(path) && Hash(path) != expected) throw new InvalidDataException("The Start menu shortcut has changed. Move it away and retry."); }
    private sealed class Transaction(Action rollback) : IIntegrationTransaction
    {
        private bool committed;
        public void Commit() => committed = true;
        public void Dispose() { if (!committed) { committed = true; rollback(); } }
    }
}

/// <summary>Direct ShellLink COM vtables; no dynamic COM, scripting host or PowerShell dependency.</summary>
internal static unsafe partial class ShellLinks
{
    public static void Create(string path, string target, string arguments, string workingDirectory)
    {
        SafePaths.AssertUnlinked(path);
        int initialized = CoInitializeEx(0, 2);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        Guid clsid = new("00021401-0000-0000-C000-000000000046"), iid = new("000214F9-0000-0000-C000-000000000046"), persistIid = new("0000010b-0000-0000-C000-000000000046");
        nint link = 0, persist = 0;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".lnk";
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out link));
            nint* table = *(nint**)link;
            CallString(table[20], link, target); CallString(table[11], link, arguments); CallString(table[9], link, workingDirectory);
            Guid* requested = &persistIid;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)table[0])(link, requested, &persist));
            nint* persistTable = *(nint**)persist;
            fixed (char* filename = temporary)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)persistTable[6])(persist, filename, 1));
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (persist != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)persist)[2])(persist);
            if (link != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)link)[2])(link);
            if (initialized >= 0) CoUninitialize();
        }
    }
    private static void CallString(nint method, nint instance, string value)
    { fixed (char* text = value) Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)method)(instance, text)); }
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}
