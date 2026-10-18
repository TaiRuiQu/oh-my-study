using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OhMyStudy.Infrastructure.Credentials;

/// <summary>Preserves the legacy generic-credential target and UTF-8 blob format.</summary>
public static partial class WindowsCredentialStore
{
    private const string Prefix = "OhMyStudy/provider/";
    private const uint Generic = 1;
    private const int NotFound = 1168;

    public static string NewReference() => Prefix + Guid.NewGuid().ToString("D");

    public static void ValidateReference(string reference)
    {
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParse(reference[Prefix.Length..], out _))
            throw new ArgumentException("无效的凭据引用", nameof(reference));
    }

    public static unsafe void Save(string reference, ReadOnlySpan<byte> utf8Secret)
    {
        ValidateReference(reference);
        if (utf8Secret.IsEmpty || utf8Secret.Length > 2560 || utf8Secret.Contains((byte)0))
            throw new ArgumentException("密钥为空或超过 Windows 凭据长度限制", nameof(utf8Secret));
        // Validate UTF-8 without ever logging a secret or storing it in diagnostic objects.
        _ = new UTF8Encoding(false, true).GetCharCount(utf8Secret);
        fixed (char* target = reference)
        fixed (char* user = "api-key")
        fixed (byte* blob = utf8Secret)
        {
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = (nint)target,
                UserName = (nint)user,
                CredentialBlobSize = (uint)utf8Secret.Length,
                CredentialBlob = (nint)blob,
                Persist = 2 // CRED_PERSIST_LOCAL_MACHINE: same identity semantics as the old client.
            };
            if (CredWriteW(in credential, 0) == 0) ThrowNativeError();
        }
    }

    /// <summary>The caller owns the returned bytes and must zero them after use.</summary>
    public static unsafe byte[]? Read(string reference)
    {
        ValidateReference(reference);
        if (CredReadW(reference, Generic, 0, out var pointer) == 0)
        {
            if (Marshal.GetLastPInvokeError() == NotFound) return null;
            ThrowNativeError();
        }
        try
        {
            var credential = *(NativeCredential*)pointer;
            if (credential.CredentialBlobSize > 2560)
                throw new InvalidDataException("凭据格式损坏，请重新配置");
            var bytes = new ReadOnlySpan<byte>((void*)credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize)).ToArray();
            try { _ = new UTF8Encoding(false, true).GetCharCount(bytes); }
            catch (DecoderFallbackException)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new InvalidDataException("凭据格式损坏，请重新配置");
            }
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    public static void Delete(string reference)
    {
        ValidateReference(reference);
        if (CredDeleteW(reference, Generic, 0) == 0 && Marshal.GetLastPInvokeError() != NotFound)
            ThrowNativeError();
    }

    private static void ThrowNativeError() => throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows 凭据存储不可用");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    private static partial int CredWriteW(in NativeCredential credential, uint flags);
    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int CredReadW(string target, uint type, uint flags, out nint credential);
    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int CredDeleteW(string target, uint type, uint flags);
    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint credential);
}
