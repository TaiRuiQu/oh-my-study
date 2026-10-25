using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyStudy.Documents;

public sealed partial class TypstDocumentService
{
    private async Task<string> MeasureAnswerSpaceAsync(string sourcePath, double columnWidthMillimetres, int columns,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var packages = Directory.CreateDirectory(Path.Combine(directory, "empty-packages")).FullName;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = directory
        };
        foreach (string argument in new[] { "query", "--root", directory, "--package-path", packages,
            "--package-cache-path", packages, "--field", "value", sourcePath, "<exam-layout>" })
            start.ArgumentList.Add(argument);
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("TYPST_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Typst layout measurement.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var errors = ReadLimitedAsync(process.StandardError, timeout.Token);
        var output = ReadLimitedAsync(process.StandardOutput, timeout.Token, 8_388_608);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string error = await errors;
            string json = await output;
            if (process.ExitCode != 0) throw new InvalidDataException("Native layout measurement failed: " + error);
            var marks = JsonSerializer.Deserialize(json, ExamLayoutJsonContext.Default.ListExamLayoutMark)
                ?? throw new InvalidDataException("Typst returned no layout measurements.");
            var result = new StringBuilder("#let exam-extra = (");
            double pitch = (columnWidthMillimetres + 12) * 72 / 25.4;
            foreach (var region in marks.GroupBy(mark => (mark.Page, Column: Math.Clamp((int)Math.Floor((mark.X - 17 * 72 / 25.4 + 0.1) / pitch), 0, columns - 1))))
            {
                var blanks = region.Where(mark => mark.Kind == "blank").ToArray();
                if (blanks.Length == 0) continue;
                var last = region.OrderBy(mark => mark.Y).Last();
                if (last.Kind == "start") continue;
                double extra = Math.Max(0, (280 * 72 / 25.4 - last.Y - 1) / blanks.Length);
                foreach (var blank in blanks)
                    result.Append('"').Append(blank.Id).Append("\": ").Append(extra.ToString("0.###", CultureInfo.InvariantCulture)).Append("pt,");
            }
            return result.Append(")\n").ToString();
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await Task.WhenAll(errors, output); } catch (OperationCanceledException) { }
        }
    }
}

internal sealed record ExamLayoutMark(string Kind, int Id, int Page, double X, double Y);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<ExamLayoutMark>))]
internal partial class ExamLayoutJsonContext : JsonSerializerContext;
