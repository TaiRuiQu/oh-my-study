using System.Net;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OhMyStudy.Infrastructure.Import;

internal static class PhoneCaptureProtocolChecks
{
    public static async Task<int> RunStandaloneAsync()
    {
        int failures = 0;
        await RunAsync(async (name, test) =>
        {
            try { await test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        });
        return failures == 0 ? 0 : 1;
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("phone_script_assets_and_reordered_pages_preserve_originals", async () =>
        {
            byte[] script = "export const capture = true;"u8.ToArray();
            await using var server = new PhoneCaptureServer((_, _) => Task.FromResult(Analysis()), (bytes, _, _) => Task.FromResult(bytes), _ => { }, scripts: new Dictionary<string, byte[]> { ["/capture.js"] = script });
            await server.StartAsync(IPAddress.Loopback, "Module resource regression");
            using var client = Client(server.Url);
            using var asset = await client.GetAsync("/capture.js");
            Require(asset.IsSuccessStatusCode && asset.Content.Headers.ContentType?.MediaType == "text/javascript");
            Require((await asset.Content.ReadAsByteArrayAsync()).SequenceEqual(script));
            Require(asset.Headers.GetValues("Content-Security-Policy").Single().Contains("script-src 'self'; worker-src 'self'", StringComparison.Ordinal));
            Require((await client.PostAsync("/reorder", Selection("missing"))).StatusCode == HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var first = await UploadAsync(client, "first.pdf", "%PDF-first"u8.ToArray());
            var second = await UploadAsync(client, "second.pdf", "%PDF-second"u8.ToArray());
            var third = await UploadAsync(client, "third.pdf", "%PDF-third"u8.ToArray());
            foreach (string[] ids in new[] { new[] { first.Id }, new[] { first.Id, first.Id, third.Id }, new[] { first.Id, second.Id, "missing" } })
            {
                Require((await client.PostAsync("/reorder", Selection(ids))).StatusCode == HttpStatusCode.BadRequest);
                var unchanged = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
                Require(unchanged.Files.Select(file => file.Id).SequenceEqual(new[] { first.Id, second.Id, third.Id }));
            }
            Require((await client.PostAsync("/reorder", Selection(third.Id, first.Id, second.Id))).IsSuccessStatusCode);
            var status = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
            Require(status.Files.Select(file => file.Id).SequenceEqual(new[] { third.Id, first.Id, second.Id }));
            Require((await client.PostAsync("/accept-batch", Selection(first.Id, third.Id))).IsSuccessStatusCode);
            Require((await client.PostAsync("/reorder", Selection(second.Id, third.Id))).StatusCode == HttpStatusCode.BadRequest);
            var sources = await server.TakeFilesAsync();
            Require(sources.Select(source => source.Name).SequenceEqual(new[] { "third.pdf", "first.pdf" }));
            Require(sources[0].Bytes.AsSpan().SequenceEqual("%PDF-third"u8) && sources[1].Bytes.AsSpan().SequenceEqual("%PDF-first"u8));
        });
        await check("phone_preview_upload_skips_corner_detection_and_docres_needs_no_corners", async () =>
        {
            await using var server = new PhoneCaptureServer((_, _) => throw new InvalidOperationException("Automatic corner analysis must be opt-in"),
                (bytes, _, _) => Task.FromResult(bytes), _ => { }, preview: (_, _) => Task.FromResult(new PhonePhotoAnalysis([4, 5], [[0, 0], [1, 0], [1, 1], [0, 1]], false)));
            await server.StartAsync(IPAddress.Loopback, "Optional processing regression");
            using var client = Client(server.Url);
            client.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var info = await UploadAsync(client, "original.jpg");
            Require(!info.Detected && info.Rotation == 0);
            var correction = new PhotoCorrection([], "original", Backend: "docres", Tasks: []);
            var response = await client.PostAsync("/render/" + info.Id, new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(correction, PhoneCaptureJsonContext.Default.PhotoCorrection)));
            Require(response.IsSuccessStatusCode);
        });
        await check("phone_standard_auto_crop_accepts_empty_corners_and_rejects_partial_manual_corners", async () =>
        {
            PhotoCorrection? received = null;
            await using var server = new PhoneCaptureServer((_, _) => Task.FromResult(Analysis()), (bytes, correction, _) =>
            {
                received = correction;
                return Task.FromResult(bytes);
            }, _ => { });
            await server.StartAsync(IPAddress.Loopback, "Automatic crop regression");
            using var client = Client(server.Url);
            client.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var info = await UploadAsync(client, "original.jpg");
            var correction = new PhotoCorrection([], "original", Processing: new(Crop: true, AutoOrientation: true, Shadows: true, Deblur: true, Appearance: true));
            using var response = await client.PostAsync("/render/" + info.Id, new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(correction, PhoneCaptureJsonContext.Default.PhotoCorrection)));
            Require(response.IsSuccessStatusCode && received?.Corners.Length == 0 && received.Processing == correction.Processing);
            foreach (double[][] corners in new double[][][] { [[0, 0]], [[0, 0], [1, 0], [1, 1]], [[0], [1, 0], [1, 1], [0, 1]] })
            {
                received = null;
                using var invalid = await client.PostAsync("/render/" + info.Id, new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(correction with { Corners = corners }, PhoneCaptureJsonContext.Default.PhotoCorrection)));
                Require(invalid.StatusCode == HttpStatusCode.BadRequest && received is null);
            }
        });
        await check("phone_batch_confirmation_is_atomic_and_preserves_order", async () =>
        {
            await using var server = CreateServer();
            await server.StartAsync(IPAddress.Loopback, "Phone capture batch regression");
            using var client = Client(server.Url);
            Require((await client.PostAsync("/accept-batch", Selection("missing"))).StatusCode == HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var first = await UploadAsync(client, "first.jpg");
            var pdf = await UploadAsync(client, "second.pdf", "%PDF-synthetic"u8.ToArray());
            var last = await UploadAsync(client, "third.jpg");
            Require(server.UploadedCount == 3 && server.Accepted.Count == 0);
            await RenderAsync(client, first);
            Require((await client.GetByteArrayAsync("/result/" + first.Id)).SequenceEqual(new byte[] { 8, 9 }));
            foreach (string[] ids in new[] { Array.Empty<string>(), new[] { first.Id, last.Id }, new[] { pdf.Id, "missing" }, new[] { pdf.Id, pdf.Id }, new string[] { null! } })
            {
                Require((await client.PostAsync("/accept-batch", Selection(ids))).StatusCode == HttpStatusCode.BadRequest);
                Require(server.Accepted.Count == 0);
            }
            Require((await client.PostAsync("/accept-batch", Selection(pdf.Id, first.Id))).IsSuccessStatusCode);
            Require(server.Accepted.Select(file => file.Name).SequenceEqual(new[] { "first.jpg", "second.pdf" }));
            Require((await client.PostAsync("/accept-batch", Selection(pdf.Id, first.Id))).IsSuccessStatusCode);
            Require(server.Accepted.Count == 2);
            var sources = await server.TakeFilesAsync();
            Require(sources.Count == 2 && sources[0].Bytes.SequenceEqual(new byte[] { 1, 2, 3 }) && sources[0].ProcessedImagePng!.SequenceEqual(new byte[] { 8, 9 }));
            Require(sources[1].ProcessedImagePng is null && sources[1].Bytes.AsSpan().StartsWith("%PDF-"u8));
            var status = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
            Require(status.Files[0].Processed && status.Files[0].Ready && !status.Files[2].Ready);
            Require(status.Files[0].Correction is { Enhancement: "color" } && status.Files[0].Correction!.Corners.Length == 4);
            Require((await client.PostAsync("/remove/" + last.Id, null)).IsSuccessStatusCode && server.UploadedCount == 2);
        });
        await check("phone_batch_processing_failure_keeps_other_previews_and_originals", async () =>
        {
            int attempts = 0;
            await using var server = new PhoneCaptureServer((_, _) => Task.FromResult(Analysis()), (_, _, _) =>
            {
                if (++attempts == 2) throw new PhotoRestorationException("Synthetic restoration failure", new InvalidDataException("Synthetic page"));
                return Task.FromResult(new byte[] { 8, 9 });
            }, _ => { });
            await server.StartAsync(IPAddress.Loopback, "Phone capture failure regression");
            using var client = Client(server.Url);
            client.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var first = await UploadAsync(client, "first.jpg");
            var last = await UploadAsync(client, "last.jpg");
            await RenderAsync(client, first);
            Require((await client.PostAsync("/render/" + last.Id, Correction(last))).StatusCode == HttpStatusCode.UnprocessableEntity);
            Require((await client.GetAsync("/image/" + last.Id)).IsSuccessStatusCode && server.Accepted.Count == 0);
            Require((await client.PostAsync("/accept-batch", Selection(first.Id, last.Id))).StatusCode == HttpStatusCode.BadRequest && server.Accepted.Count == 0);
            await RenderAsync(client, last);
            Require((await client.PostAsync("/accept-batch", Selection(first.Id, last.Id))).IsSuccessStatusCode && server.Accepted.Count == 2);
        });
        await check("phone_emudhra_certificate_signs_only_private_lan_addresses", async () =>
        {
            foreach (string address in new[] { "127.0.0.1", "192.168.1.20", "10.1.2.3", "172.20.1.2" })
            {
                using var certificate = new PhoneCaptureCertificate(IPAddress.Parse(address));
                Require(certificate.Certificate.HasPrivateKey && certificate.Certificate.Issuer == certificate.Chain[0].Subject);
                var names = certificate.Certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
                Require(names.EnumerateIPAddresses().Single().Equals(IPAddress.Parse(address)));
                var encoded = new AsnReader(certificate.Certificate.RawData, AsnEncodingRules.DER).ReadSequence();
                var signed = encoded.ReadEncodedValue();
                encoded.ReadSequence();
                var signature = encoded.ReadBitString(out _);
                using var key = certificate.Chain[0].GetECDsaPublicKey()!;
                Require(key.VerifyData(signed.Span, signature, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence));
            }
            foreach (string address in new[] { "8.8.8.8", "172.32.0.1", "192.0.2.1" })
            {
                try { using var certificate = new PhoneCaptureCertificate(IPAddress.Parse(address)); throw new InvalidOperationException("Public address accepted"); }
                catch (ArgumentException) { }
            }
            using var tls = new PhoneCaptureCertificate(IPAddress.Loopback);
            await using var server = new PhoneCaptureServer((_, _) => Task.FromResult(Analysis()), (_, _, _) => Task.FromResult(new byte[] { 8, 9 }), error => Console.Error.WriteLine(error));
            await server.StartAsync(IPAddress.Loopback, "Phone capture TLS regression", certificate: tls.Certificate, chain: tls.Chain);
            Require(server.Url.StartsWith("https://", StringComparison.Ordinal) && server.HttpUrl.StartsWith("http://", StringComparison.Ordinal));
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == tls.Certificate.Thumbprint };
            using var secure = new HttpClient(handler) { BaseAddress = new Uri(server.Url.Split('#')[0]) };
            Require(await secure.GetStringAsync("/") == "Phone capture TLS regression");
            secure.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var pdf = await UploadAsync(secure, "tls.pdf", "%PDF-synthetic"u8.ToArray());
            using var fallback = Client(server.HttpUrl);
            fallback.DefaultRequestHeaders.Add("X-Capture-Token", server.HttpUrl.Split('#')[1]);
            Require((await fallback.PostAsync("/accept-batch", Selection(pdf.Id))).IsSuccessStatusCode && server.Accepted.Count == 1);
        });
    }

    private static PhonePhotoAnalysis Analysis() => new([4, 5], [[0, 0], [1, 0], [1, 1], [0, 1]], true);
    private static PhoneCaptureServer CreateServer() => new((_, _) => Task.FromResult(Analysis()), (_, _, _) => Task.FromResult(new byte[] { 8, 9 }), _ => { });
    private static HttpClient Client(string url) => new() { BaseAddress = new Uri(url.Split('#')[0]), Timeout = TimeSpan.FromSeconds(10) };
    private static ByteArrayContent Selection(params string[] ids) => new(JsonSerializer.SerializeToUtf8Bytes(new PhoneCaptureSelection(ids), PhoneCaptureJsonContext.Default.PhoneCaptureSelection));
    private static ByteArrayContent Correction(PhonePhotoInfo info) => new(JsonSerializer.SerializeToUtf8Bytes(new PhotoCorrection(info.Corners!, "color", info.Rotation), PhoneCaptureJsonContext.Default.PhotoCorrection));
    private static async Task RenderAsync(HttpClient client, PhonePhotoInfo info) => Require((await client.PostAsync("/render/" + info.Id, Correction(info))).IsSuccessStatusCode);
    private static async Task<PhonePhotoInfo> UploadAsync(HttpClient client, string name, byte[]? bytes = null)
    {
        var response = await client.PostAsync("/upload?name=" + name, new ByteArrayContent(bytes ?? [1, 2, 3]));
        Require(response.IsSuccessStatusCode);
        return JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(), PhoneCaptureJsonContext.Default.PhonePhotoInfo)!;
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Phone capture protocol regression failed"); }
}
