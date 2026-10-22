using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OhMyStudy.Infrastructure.Execution;

public sealed partial class AppContainerProfile : IDisposable
{
    private readonly string _name;
    private readonly List<(string Path, FileSystemAccessRule Rule, nint Descriptor, nint Label)> _grants = [];
    private nint _sid;
    internal nint Sid => _sid != 0 ? _sid : throw new ObjectDisposedException(nameof(AppContainerProfile));
    public bool Networking { get; }

    private AppContainerProfile(string name, nint sid, bool networking)
    {
        _name = name;
        _sid = sid;
        Networking = networking;
    }

    public static AppContainerProfile Create(Guid id, bool networking = false)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new PlatformNotSupportedException("此系统不支持 AppContainer 运行。");
        if (id == Guid.Empty) throw new ArgumentException("无效的沙箱标识。", nameof(id));
        var name = "OhMyStudy.Execution." + id.ToString("N");
        int result = Native.CreateAppContainerProfile(name, name, name, 0, 0, out var sid);
        if (result < 0) throw new Win32Exception(result & 0xffff);
        return new(name, sid, networking);
    }

    public void GrantDirectory(string directory, bool writable = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var sid = new SecurityIdentifier(Sid);
        var path = RuntimePaths.CheckedDirectory(directory);
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("AppContainer 需要本地目录。");
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl(AccessControlSections.Access);
        if (!writable && security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule =>
            rule.IdentityReference.Value == "S-1-15-2-1" && rule.AccessControlType == AccessControlType.Allow
            && (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute
            && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0)) return;
        var rule = new FileSystemAccessRule(sid, writable ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        nint descriptor = 0, label = 0;
        if (writable)
        {
            uint result = Native.GetNamedSecurityInfoW(path, 1, 0x10, out _, out _, out _, out label, out descriptor);
            if (result != 0) throw new Win32Exception((int)result);
        }
        _grants.Add((path, rule, descriptor, label));
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        if (writable) SetLowIntegrity(path);
    }

    private static void SetLowIntegrity(string path)
    {
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (!Native.GetSecurityDescriptorSacl(descriptor, out _, out var label, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            uint result = Native.SetNamedSecurityInfoW(path, 1, 0x10, 0, 0, 0, label);
            if (result != 0) throw new Win32Exception((int)result);
        }
        finally { Native.LocalFree(descriptor); }
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (_sid == 0) return;
        while (_grants.Count > 0)
        {
            var grant = _grants[^1];
            var info = new DirectoryInfo(RuntimePaths.CheckedDirectory(grant.Path));
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.RemoveAccessRuleSpecific(grant.Rule);
            info.SetAccessControl(security);
            if (grant.Descriptor != 0)
            {
                uint result = Native.SetNamedSecurityInfoW(grant.Path, 1, 0x10, 0, 0, 0, grant.Label);
                if (result != 0) throw new Win32Exception((int)result);
                Native.LocalFree(grant.Descriptor);
            }
            _grants.RemoveAt(_grants.Count - 1);
        }
        int deleted = Native.DeleteAppContainerProfile(_name);
        if (deleted < 0) throw new Win32Exception(deleted & 0xffff);
        Native.FreeSid(_sid);
        _sid = 0;
    }

    private static partial class Native
    {
        [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint count, out nint sid);
        [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int DeleteAppContainerProfile(string name);
        [LibraryImport("advapi32.dll")] internal static partial nint FreeSid(nint sid);
        [LibraryImport("kernel32.dll")] internal static partial nint LocalFree(nint memory);
        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint GetNamedSecurityInfoW(string name, uint type, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial uint SetNamedSecurityInfoW(string name, uint type, uint information, nint owner, nint group, nint dacl, nint sacl);
        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetSecurityDescriptorSacl(nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    }
}
