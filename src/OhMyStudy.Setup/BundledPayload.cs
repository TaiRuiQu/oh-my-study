using System.IO.Compression;
using System.Reflection;

namespace OhMyStudy.Setup;

/// <summary>The downloadable installer carries the same verified portable ZIP as a resource.</summary>
public sealed class BundledPayload : IDisposable
{
    public string DirectoryPath { get; }
    private BundledPayload(string path) => DirectoryPath = path;

    public static BundledPayload? Open()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OhMyStudy.Payload.zip");
        return stream is null ? null : Extract(stream);
    }

    public static BundledPayload Extract(Stream stream)
    {
        var payload = new BundledPayload(Directory.CreateTempSubdirectory("OhMyStudy-Install-").FullName);
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                string relative = entry.FullName.TrimEnd('/');
                string target = SafePaths.Child(payload.DirectoryPath, relative);
                if (!paths.Add(relative) || (entry.ExternalAttributes & 0x400) != 0 ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                    entry.Length > 4L * 1024 * 1024 * 1024 || (total += entry.Length) > 8L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("安装文件损坏，请重新下载。");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                if (output.Length != entry.Length) throw new InvalidDataException("安装文件损坏，请重新下载。");
            }
            if (!File.Exists(Path.Combine(payload.DirectoryPath, InstallerEngine.ManifestName)))
                throw new InvalidDataException("安装文件不完整，请重新下载。");
            return payload;
        }
        catch { payload.Dispose(); throw; }
    }

    public void Dispose()
    {
        // Only this newly created, private extraction folder is owned by the helper.
        try { Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
