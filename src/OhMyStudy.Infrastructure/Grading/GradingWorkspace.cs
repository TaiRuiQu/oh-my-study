using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Grading;

internal sealed class GradingWorkspace(string jobId, LibraryRepository library, IGradingImageReader images)
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "OhMyStudy", "tasks", jobId);

    public void Prepare() => RuntimePaths.CheckedDirectory(DirectoryPath, create: true);

    public async Task<string> WriteImageAsync(byte[] bytes, CancellationToken cancellationToken, string mime = "image/png")
    {
        Prepare();
        var directory = RuntimePaths.CheckedDirectory(Path.Combine(DirectoryPath, "pages"), create: true);
        string extension = mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => throw new InvalidDataException("Unsupported page image format.") };
        string name = Convert.ToHexStringLower(SHA256.HashData(bytes)) + extension;
        string path = Path.Combine(directory, name);
        if (File.Exists(path)) RuntimePaths.CheckedFile(path);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return "pages/" + name;
    }

    public static IReadOnlyList<string> OutputPaths(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("outputs", out var outputs)) return [];
        if (outputs.ValueKind != JsonValueKind.Array || outputs.GetArrayLength() > 8)
            throw new DomainValidationException("outputs must contain at most 8 relative image paths.");
        return outputs.EnumerateArray().Select(value => value.GetString() ?? throw new JsonException()).ToList();
    }

    public string OutputPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new DomainValidationException("Output paths must be relative to the task working directory.");
        string path = Path.GetFullPath(Path.Combine(DirectoryPath, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DomainValidationException("Output paths must stay inside the task working directory.");
        return path;
    }

    public async Task<JsonArray> ReadOutputsAsync(IReadOnlyList<string> outputs, List<Image> attachments, Func<Func<CancellationToken, Task<Asset>>, Task<Asset>> commit, CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        foreach (string relative in outputs)
        {
            try
            {
                string path = RuntimePaths.CheckedFile(OutputPath(relative));
                long size = new FileInfo(path).Length;
                if (size is < 1 or > LibraryLimits.MaximumAssetBytes) throw new InvalidDataException("The output image is empty or exceeds the image size limit.");
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var preview = await images.ReadAsync(bytes, null, 0, cancellationToken).ConfigureAwait(false);
                var asset = await commit(token => library.SaveGradingImageAsync(jobId, bytes, token)).ConfigureAwait(false);
                result.Add((JsonNode)new JsonObject
                {
                    ["path"] = relative,
                    ["asset_id"] = asset.Id,
                    ["width"] = asset.Width,
                    ["height"] = asset.Height,
                    ["image_index"] = attachments.Count
                });
                attachments.Add(preview.Image);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or DomainValidationException or COMException)
            {
                result.Add((JsonNode)new JsonObject { ["path"] = relative, ["error"] = error.Message });
            }
        }
        return result;
    }
}
