using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Setup;

string output = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "output/migration/setup-aot-tests.json");
string testRoot = Path.Combine(Path.GetDirectoryName(output)!, "setup-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var results = new JsonArray();
int failures = 0;
void Test(string name, Action action)
{
    try { action(); results.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = true }); }
    catch (Exception error) { failures++; results.Add((JsonNode)new JsonObject { ["name"] = name, ["passed"] = false, ["error"] = error.ToString() }); }
}
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void Rejected(Action action) { try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { return; } throw new Exception("unsafe operation accepted"); }
string Package(string name, string body)
{
    string directory = Path.Combine(testRoot, name); Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "OhMyStudy.App.exe"), "synthetic native app " + body);
    File.WriteAllText(Path.Combine(directory, "OhMyStudy.Setup.exe"), "synthetic bootstrap " + body);
    Directory.CreateDirectory(Path.Combine(directory, "native", "字体"));
    File.WriteAllText(Path.Combine(directory, "native", "字体", "许可.txt"), body);
    var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(path => new PackageFile(Path.GetRelativePath(directory, path).Replace('\\', '/'), new FileInfo(path).Length, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))).ToList();
    File.WriteAllBytes(Path.Combine(directory, InstallerEngine.ManifestName), JsonSerializer.SerializeToUtf8Bytes(new PackageManifest(1, "win-x64", "OhMyStudy.App.exe", files, body), SetupJson.Default.PackageManifest));
    return directory;
}
var integration = new FakeIntegration();
var engine = new InstallerEngine(integration);
string package1 = Package("包 一", "1.0"), package2 = Package("包 二", "2.0"), install = Path.Combine(testRoot, "安装位置");
InstallResult? first = null;
Test("fresh offline install and manifest validation", () =>
{
    first = engine.Install(new(package1, install, false));
    Require(first.ActiveVersion is { Length: 64 } && !first.Upgraded, "fresh result");
    Require(File.ReadAllText(engine.ResolveLaunch(install)).EndsWith("1.0", StringComparison.Ordinal), "launch current version");
});
Test("idempotent install reuses verified immutable version", () =>
{
    var again = engine.Install(new(package1, install, false)); Require(again.ActiveVersion == first!.ActiveVersion && again.Upgraded, "same package");
});
string data = Path.Combine(install, "用户资料"); Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "keep.sqlite3"), "USER_PRIVATE");
foreach (var phase in Enum.GetValues<InstallPhase>())
    Test("upgrade rollback at " + phase, () =>
    {
        string active = File.ReadAllText(Path.Combine(install, "active.json"));
        string marker = File.ReadAllText(Path.Combine(install, "installation.json"));
        Rejected(() => engine.Install(new(package2, install, false, current => { if (current == phase) throw new IOException("synthetic failure"); })));
        Require(File.ReadAllText(Path.Combine(install, "active.json")) == active, "active pointer changed after failure");
        Require(File.ReadAllText(Path.Combine(install, "installation.json")) == marker, "ownership changed after failure");
        Require(File.ReadAllText(engine.ResolveLaunch(install)).EndsWith("1.0", StringComparison.Ordinal), "previous executable unavailable");
        Require(Directory.GetDirectories(Path.Combine(install, "versions")).Length == 1, "failed version left as installed");
    });
Test("upgrade keeps previous version and user data", () =>
{
    var upgraded = engine.Install(new(package2, install, false));
    Require(upgraded.Upgraded && upgraded.ActiveVersion != first!.ActiveVersion, "upgrade pointer");
    Require(Directory.Exists(Path.Combine(install, "versions", first!.ActiveVersion!)), "previous version lost");
    Require(File.ReadAllText(engine.ResolveLaunch(install)).EndsWith("2.0", StringComparison.Ordinal), "new launch");
    Require(File.ReadAllText(Path.Combine(data, "keep.sqlite3")) == "USER_PRIVATE", "user data changed");
});
Test("failed first install cleans only its created files", () =>
{
    string target = Path.Combine(testRoot, "first-failure");
    Rejected(() => engine.Install(new(package1, target, false, phase => { if (phase == InstallPhase.AfterSwitch) throw new IOException(); })));
    Require(!File.Exists(Path.Combine(target, "active.json")) && !File.Exists(Path.Combine(target, InstallerEngine.BootstrapName)), "partial first install committed");
    engine.Install(new(package1, target, false)); engine.Uninstall(target);
});
Test("partial bootstrap copy failure cleans its file and permits install retry", () =>
{
    string target = Path.Combine(testRoot, "partial-bootstrap");
    bool injected = false;
    Rejected(() => engine.Install(new(package1, target, false, BootstrapCopyCheckpoint: () =>
    {
        string bootstrap = Path.Combine(target, InstallerEngine.BootstrapName);
        long length = new FileInfo(bootstrap).Length;
        Require(length > 0 && length < new FileInfo(Path.Combine(package1, InstallerEngine.BootstrapName)).Length, "fault was not injected during a partial copy");
        injected = true;
        throw new IOException("synthetic disk-full during bootstrap copy");
    })));
    Require(injected && !File.Exists(Path.Combine(target, InstallerEngine.BootstrapName)), "half bootstrap remained");
    Require(!Directory.Exists(Path.Combine(target, "versions")), "failed version remained");
    engine.Install(new(package1, target, false));
    Require(File.ReadAllText(engine.ResolveLaunch(target)).EndsWith("1.0", StringComparison.Ordinal), "retry failed");
    engine.Uninstall(target);
});
Test("CreateNew failure never removes a preexisting target", () =>
{
    string target = Path.Combine(testRoot, "existing-bootstrap.exe"); File.WriteAllText(target, "EXISTING_PRIVATE");
    Rejected(() => InstallerEngine.CopyNew(Path.Combine(package1, InstallerEngine.BootstrapName), target));
    Require(File.ReadAllText(target) == "EXISTING_PRIVATE", "preexisting file removed");
});
Test("tampered payload rejected before changing active pointer", () =>
{
    string bad = Package("tampered", "bad"); File.AppendAllText(Path.Combine(bad, "OhMyStudy.App.exe"), "tamper");
    string before = File.ReadAllText(Path.Combine(install, "active.json"));
    Rejected(() => engine.Install(new(bad, install, false)));
    Require(File.ReadAllText(Path.Combine(install, "active.json")) == before, "tamper changed installed state");
});
foreach (string relative in new[] { "../outside", "/rooted", "C:/absolute", "native\\evil", "native/file:stream", "native/CON", "native/file.", "native/file ", "native//file", "native/../file" })
    Test("path rejection: " + relative, () => Rejected(() => SafePaths.Child(testRoot, relative)));
Test("case-duplicate manifest paths rejected", () =>
{
    string bad = Package("duplicate", "duplicate"); string manifestPath = Path.Combine(bad, InstallerEngine.ManifestName);
    var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SetupJson.Default.PackageManifest)!;
    manifest.Files.Add(manifest.Files[0] with { Path = manifest.Files[0].Path.ToUpperInvariant() });
    File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, SetupJson.Default.PackageManifest));
    Rejected(() => engine.Install(new(bad, Path.Combine(testRoot, "duplicate-root"), false)));
});
Test("nonempty unowned destination untouched", () =>
{
    string target = Path.Combine(testRoot, "unowned"); Directory.CreateDirectory(target); string keep = Path.Combine(target, "keep.txt"); File.WriteAllText(keep, "KEEP");
    Rejected(() => engine.Install(new(package1, target, false))); Require(File.ReadAllText(keep) == "KEEP", "unowned data changed");
    Rejected(() => engine.Uninstall(target));
});
Test("overlapping source and destination rejected", () => Rejected(() => engine.Install(new(package1, Path.Combine(package1, "inside"), false))));
Test("concurrent writer rejected without modifying active state", () =>
{
    using var locked = new FileStream(Path.Combine(install, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Rejected(() => engine.Install(new(package1, install, false)));
});
Test("real Windows directory junction rejected without touching target", () =>
{
    string target = Path.Combine(testRoot, "junction-target"), link = Path.Combine(testRoot, "junction"); Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "keep.txt"), "TARGET_KEEP");
    Junction.Create(link, target);
    Rejected(() => engine.Install(new(package1, link, false)));
    Require(File.ReadAllText(Path.Combine(target, "keep.txt")) == "TARGET_KEEP", "junction target changed");
    Directory.Delete(link, false);
});
Test("integrated transaction rolls back external changes", () =>
{
    string target = Path.Combine(testRoot, "integrated-failure");
    Rejected(() => engine.Install(new(package1, target, true, phase => { if (phase == InstallPhase.Integrated) throw new IOException(); })));
    Require(!integration.Installed, "integration remained after rollback");
    engine.Install(new(package1, target, true)); Require(integration.Installed, "integration missing");
    engine.Uninstall(target); Require(!integration.Installed, "integration not removed");
});
Test("native ShellLink serialization to isolated folder", () =>
{
    string link = Path.Combine(testRoot, "isolated.lnk"); ShellLinks.Create(link, Path.Combine(install, InstallerEngine.BootstrapName), "--launch", install);
    byte[] bytes = File.ReadAllBytes(link); Require(bytes.Length > 76 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x4c, "invalid native shortcut");
});
Test("uninstall preflight refuses locked file before any product deletion", () =>
{
    string app = engine.ResolveLaunch(install); using var locked = new FileStream(app, FileMode.Open, FileAccess.Read, FileShare.None);
    Rejected(() => engine.Uninstall(install)); Require(File.Exists(Path.Combine(install, "active.json")), "active removed while app locked");
});
Test("uninstall retains user data and unlisted files inside version", () =>
{
    string version = Path.GetDirectoryName(engine.ResolveLaunch(install))!; string added = Path.Combine(version, "my-notes.txt"); File.WriteAllText(added, "NOT_PRODUCT_DATA");
    engine.Uninstall(install);
    Require(File.ReadAllText(Path.Combine(data, "keep.sqlite3")) == "USER_PRIVATE", "user data deleted");
    Require(File.ReadAllText(added) == "NOT_PRODUCT_DATA", "unlisted file deleted");
    Require(!File.Exists(Path.Combine(install, "active.json")) && !File.Exists(Path.Combine(install, InstallerEngine.BootstrapName)), "app still installed");
});
for (int failureAt = 1; failureAt <= 7; failureAt++)
{
    int checkpoint = failureAt;
    Test("uninstall resumes after deletion " + checkpoint, () =>
    {
        string target = Path.Combine(testRoot, "uninstall-retry-" + checkpoint);
        engine.Install(new(package1, target, false));
        string version = Path.GetDirectoryName(engine.ResolveLaunch(target))!;
        string keepRoot = Path.Combine(target, "user-data.sqlite3"), keepVersion = Path.Combine(version, "my-notes.txt");
        File.WriteAllText(keepRoot, "ROOT_PRIVATE"); File.WriteAllText(keepVersion, "VERSION_PRIVATE");
        bool injected = false;
        Rejected(() => engine.Uninstall(target, deleted =>
        {
            if (deleted != checkpoint) return;
            injected = true;
            throw new IOException("synthetic mid-uninstall file deletion failure");
        }));
        Require(injected && File.Exists(Path.Combine(target, "uninstall.json")), "resume journal missing after fault");
        Rejected(() => engine.ResolveLaunch(target));
        engine.Uninstall(target);
        Require(File.ReadAllText(keepRoot) == "ROOT_PRIVATE" && File.ReadAllText(keepVersion) == "VERSION_PRIVATE", "retry removed unlisted user data");
        Require(!File.Exists(Path.Combine(target, "uninstall.json")) && !File.Exists(Path.Combine(target, "installation.json")), "uninstall did not complete");
        Require(!File.Exists(Path.Combine(target, InstallerEngine.BootstrapName)), "bootstrap remained after retry");
    });
}
Test("uninstall retry validates still-present payload before continuing", () =>
{
    string target = Path.Combine(testRoot, "uninstall-retry-tamper"); engine.Install(new(package1, target, false));
    string version = Path.GetDirectoryName(engine.ResolveLaunch(target))!;
    Rejected(() => engine.Uninstall(target, deleted => { if (deleted == 1) throw new IOException(); }));
    string remaining = Path.Combine(version, "native", "字体", "许可.txt");
    byte[] original = File.ReadAllBytes(remaining); File.WriteAllText(remaining, "MODIFIED_PRIVATE");
    Rejected(() => engine.Uninstall(target));
    Require(File.ReadAllText(remaining) == "MODIFIED_PRIVATE", "modified remaining file deleted");
    File.WriteAllBytes(remaining, original); engine.Uninstall(target);
});
Test("uninstall resumes across multiple versions after an old manifest was removed", () =>
{
    string target = Path.Combine(testRoot, "uninstall-two-versions");
    engine.Install(new(package1, target, false)); engine.Install(new(package2, target, false));
    string version = Path.GetDirectoryName(engine.ResolveLaunch(target))!;
    string keep = Path.Combine(version, "extra-private.txt"); File.WriteAllText(keep, "KEEP_ACROSS_VERSIONS");
    Rejected(() => engine.Uninstall(target, deleted => { if (deleted == 4) throw new IOException(); }));
    engine.Uninstall(target);
    Require(File.ReadAllText(keep) == "KEEP_ACROSS_VERSIONS" && !File.Exists(Path.Combine(target, "uninstall.json")), "multi-version retry failed");
});
if (args.ElementAtOrDefault(1) is { } setupPath)
{
    Test("actual AOT setup CLI install and self-uninstall helper", () =>
    {
        string source = Package("actual-cli-package", "cli"), target = Path.Combine(testRoot, "actual-cli-install");
        File.Copy(Path.GetFullPath(setupPath), Path.Combine(source, InstallerEngine.BootstrapName), true);
        string manifestPath = Path.Combine(source, InstallerEngine.ManifestName);
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), SetupJson.Default.PackageManifest)!;
        var files = manifest.Files.Select(file => file.Path == InstallerEngine.BootstrapName
            ? file with { Bytes = new FileInfo(Path.Combine(source, file.Path)).Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, file.Path)))) } : file).ToList();
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with { Files = files }, SetupJson.Default.PackageManifest));
        string installReport = Path.Combine(testRoot, "actual-install.json"), uninstallReport = Path.Combine(testRoot, "actual-uninstall.json");
        Run(Path.GetFullPath(setupPath), ["--install", "--source", source, "--root", target, "--no-shortcuts", "--quiet", "--report", installReport]);
        using (var report = JsonDocument.Parse(File.ReadAllBytes(installReport))) Require(report.RootElement.GetProperty("success").GetBoolean() && report.RootElement.GetProperty("nativeAot").GetBoolean(), "CLI installation failed or not native AOT");
        string keep = Path.Combine(target, "user-data.sqlite3"); File.WriteAllText(keep, "CLI_PRIVATE");
        Run(Path.Combine(target, InstallerEngine.BootstrapName), ["--uninstall", "--quiet", "--report", uninstallReport]);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(uninstallReport) && timer.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(50);
        using (var report = JsonDocument.Parse(File.ReadAllBytes(uninstallReport))) Require(report.RootElement.GetProperty("success").GetBoolean(), "self uninstall helper failed");
        Require(File.ReadAllText(keep) == "CLI_PRIVATE" && !File.Exists(Path.Combine(target, InstallerEngine.BootstrapName)), "CLI uninstall data/ownership failure");
    });
}
var report = new JsonObject { ["native_aot"] = !RuntimeFeature.IsDynamicCodeSupported, ["passed"] = results.Count - failures, ["failed"] = failures, ["test_root"] = testRoot, ["checks"] = results };
Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllText(output, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Setup tests: {results.Count - failures} passed, {failures} failed; native_aot={!RuntimeFeature.IsDynamicCodeSupported}; {output}");
return failures == 0 ? 0 : 1;

static void Run(string executable, string[] arguments)
{
    var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("failed to start setup");
    if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); throw new IOException("setup timeout"); }
    Require(process.ExitCode == 0, "setup returned " + process.ExitCode);
}

internal sealed class FakeIntegration : IInstallationIntegration
{
    public bool Installed;
    public IIntegrationTransaction Apply(string root, string version, string hash) { bool before = Installed; Installed = true; return new Transaction(this, before); }
    public void Remove(string root) => Installed = false;
    private sealed class Transaction(FakeIntegration owner, bool before) : IIntegrationTransaction
    { private bool committed; public void Commit() => committed = true; public void Dispose() { if (!committed) owner.Installed = before; } }
}

internal static partial class Junction
{
    // FSCTL_SET_REPARSE_POINT creates a real mount-point junction without invoking a shell or requiring symbolic-link privilege.
    public static void Create(string link, string target)
    {
        Directory.CreateDirectory(link);
        string substitute = @"\??\" + Path.GetFullPath(target), print = Path.GetFullPath(target);
        byte[] names = Encoding.Unicode.GetBytes(substitute + '\0' + print + '\0');
        byte[] buffer = new byte[16 + names.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(8 + names.Length)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)(substitute.Length * 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)((substitute.Length + 1) * 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)(print.Length * 2)));
        names.CopyTo(buffer, 16);
        using var handle = CreateFile(link, 0x40000000, 0, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, (uint)buffer.Length, 0, 0, out _, 0)) throw new IOException("junction creation failed: " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16, SetLastError = true)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
