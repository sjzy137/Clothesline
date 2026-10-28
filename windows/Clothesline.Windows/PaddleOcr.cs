using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace Clothesline.Windows;

/// PP-OCRv6 text extraction, used as a higher quality alternative to the Windows recogniser.
///
/// The runtime is pure managed: it interprets the ONNX graphs itself, so the app keeps its
/// self-contained single-file publish instead of shipping ONNX Runtime, Paddle Inference or OpenCV
/// native binaries.
///
/// The tiny tier ships inside the installer and is unpacked from the referenced package. Small and
/// medium are downloaded from ModelScope, and every model that reaches disk is checked against the
/// hash PaddlePaddle publishes, because a truncated download that merely exists would otherwise
/// fail deep inside inference with a message nobody could act on.
internal static class PaddleOcr
{
    public const string Tiny = "tiny";
    public const string Small = "small";
    public const string Medium = "medium";
    public static readonly string[] Tiers = [Tiny, Small, Medium];

    private static readonly Dictionary<string, ModelFile> Models = new()
    {
        ["tiny/det"] = new(ModelUrl("tiny_det"), "193bab7a04fca699a6c82e6abb5b81bdb28177f0abd4062552b04908dafb19f8", 1780590),
        ["tiny/rec"] = new(ModelUrl("tiny_rec"), "9ef676d6ed3c88256a2d92c640c44f25b0c40947e111b14b8be8f594091563e6", 4462639),
        ["small/det"] = new(ModelUrl("small_det"), "d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e", 9880512),
        ["small/rec"] = new(ModelUrl("small_rec"), "5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634", 21159378),
        ["medium/det"] = new(ModelUrl("medium_det"), "eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1", 62032837),
        ["medium/rec"] = new(ModelUrl("medium_rec"), "9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba", 76554979),
    };

    /// The tiny tier recognises 6,904 labels while the other two share a list of 18,708, so each
    /// tier has to be paired with its own dictionary. Pairing a model with the wrong one indexes
    /// every character against the wrong class and returns plausible looking nonsense.
    private static readonly Dictionary<string, string> Dictionaries = new()
    {
        [Tiny] = "pack://application:,,,/Assets/ppocr-v6-tiny-dict.txt",
        [Small] = "pack://application:,,,/Assets/ppocr-v6-full-dict.txt",
        [Medium] = "pack://application:,,,/Assets/ppocr-v6-full-dict.txt",
    };

    private sealed record ModelFile(string Url, string Sha256, long Size);

    private static string ModelUrl(string model) =>
        "https://www.modelscope.cn/models/PaddlePaddle/PP-OCRv6_" + model + "_onnx/resolve/master/inference.onnx";

    private static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clothesline", "models");
    private static string TierDirectory(string tier) => Path.Combine(Root, "v6-" + tier);
    private static string ModelPath(string tier, string file) => Path.Combine(TierDirectory(tier), file);
    private static string ClassificationPath => Path.Combine(Root, "cls.onnx");

    public static string DisplayName(string tier) => tier switch
    {
        Tiny => "PP-OCRv6 Tiny",
        Small => "PP-OCRv6 Small",
        Medium => "PP-OCRv6 Medium",
        _ => tier
    };

    /// Bytes that have to cross the network. Tiny is zero because it ships with the app and is only
    /// unpacked, which is why it can be offered without a connection.
    public static long DownloadBytes(string tier) => tier == Tiny ? 0 : Models[tier + "/det"].Size + Models[tier + "/rec"].Size;
    public static long InstalledBytes(string tier) => Models[tier + "/det"].Size + Models[tier + "/rec"].Size;

    public static bool IsReady(string tier) =>
        File.Exists(Path.Combine(TierDirectory(tier), "ready")) &&
        File.Exists(ModelPath(tier, "det.onnx")) && File.Exists(ModelPath(tier, "rec.onnx")) &&
        File.Exists(ModelPath(tier, "dict.txt"));

    /// Materialises a tier on disk. Progress runs 0..1 across both models.
    public static async Task EnsureAsync(string tier, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (IsReady(tier)) { progress?.Report(1); return; }
        Directory.CreateDirectory(TierDirectory(tier));
        var tracker = new Tracker(Models[tier + "/det"].Size + Models[tier + "/rec"].Size, progress);
        try
        {
            foreach (var kind in new[] { "det", "rec" })
            {
                var model = Models[tier + "/" + kind];
                if (tier == Tiny) await UnpackAsync(kind, model, tracker, cancel);
                else await DownloadAsync(model, ModelPath(tier, kind + ".onnx"), tracker, cancel);
            }
            await UnpackClassificationAsync();
            File.WriteAllBytes(ModelPath(tier, "dict.txt"), ReadResource(Dictionaries[tier]));
            File.WriteAllText(Path.Combine(TierDirectory(tier), "ready"), tier);
            progress?.Report(1);
        }
        catch
        {
            // A half written directory would pass the readiness check and then fail inside
            // inference, so an attempt that did not finish leaves nothing behind.
            Delete(tier);
            throw;
        }
    }

    public static void Delete(string tier)
    {
        try { if (Directory.Exists(TierDirectory(tier))) Directory.Delete(TierDirectory(tier), true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// Async methods cannot take ref parameters, so the running byte count lives in an object.
    private sealed class Tracker(long total, IProgress<double>? progress)
    {
        private long done;
        public void Report(int count)
        { Interlocked.Add(ref done, count); progress?.Report(Math.Min(0.99, (double)done / Math.Max(1, total))); }
    }

    /// The tiny tier comes from the referenced package rather than the network, so it costs no data
    /// and works on a machine with no connection at all.
    private static async Task UnpackAsync(string kind, ModelFile model, Tracker tracker, CancellationToken cancel)
    {
        const string prefix = "Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny.";
        using var source = typeof(ChineseV6TinyModels).Assembly.GetManifestResourceStream(prefix + kind + ".onnx")
                           ?? throw new InvalidOperationException("The bundled " + kind + " model is missing.");
        await CopyVerifiedAsync(source, ModelPath(Tiny, kind + ".onnx"), model, tracker, cancel);
    }

    private static async Task DownloadAsync(ModelFile model, string target, Tracker tracker, CancellationToken cancel)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Clothesline/0.1");
        using var response = await client.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        await CopyVerifiedAsync(stream, target, model, tracker, cancel);
    }

    /// Writes straight to a temporary file rather than buffering a 76 MB model in memory, and hashes
    /// while copying so verifying it costs no second pass over the file.
    private static async Task CopyVerifiedAsync(Stream source, string target, ModelFile model,
        Tracker tracker, CancellationToken cancel)
    {
        var scratch = target + ".part";
        try
        {
            using var hash = SHA256.Create();
            await using (var output = File.Create(scratch))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    tracker.Report(read);
                }
                hash.TransformFinalBlock([], 0, 0);
            }
            var actual = Convert.ToHexString(hash.Hash!).ToLowerInvariant();
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The model does not match the hash published by PaddlePaddle.\nExpected " + model.Sha256 + "\nReceived " + actual);
            if (new FileInfo(scratch).Length != model.Size)
                throw new InvalidDataException("The model has the wrong size.");
            File.Move(scratch, target, true);
        }
        finally { if (File.Exists(scratch)) try { File.Delete(scratch); } catch (IOException) { } }
    }

    /// One orientation model is shared by every tier, so it is kept beside the tier directories.
    private static async Task UnpackClassificationAsync()
    {
        if (File.Exists(ClassificationPath)) return;
        const string name = "Sdcb.SimdPaddleOCR.Models.TextLineOrientation.cls.onnx";
        using var source = Assembly.Load("Sdcb.SimdPaddleOCR.Models.TextLineOrientation").GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("The bundled orientation model is missing.");
        await using var output = File.Create(ClassificationPath);
        await source.CopyToAsync(output);
    }

    private static byte[] ReadResource(string uri)
    {
        var info = System.Windows.Application.GetResourceStream(new Uri(uri))
                   ?? throw new InvalidOperationException("The bundled label list is missing: " + uri);
        using var memory = new MemoryStream();
        info.Stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ---- Recognition ---------------------------------------------------------

    private static readonly object Gate = new();
    private static PaddleOcrAll? engine;
    private static string? engineTier;

    public static async Task<string> RecognizeAsync(string path, string tier, CancellationToken cancel = default)
    {
        await EnsureAsync(tier, null, cancel);
        // Decoding and inference both keep away from the dispatcher: the clothesline is a top of
        // screen overlay and freezing it mid interaction is immediately visible.
        var raster = await Task.Run(() => Decode(path), cancel);
        return await Task.Run(() =>
        {
            lock (Gate)
            {
                if (engine is null || engineTier != tier)
                {
                    engine?.Dispose();
                    engine = PaddleOcrAll.Load(ModelPath(tier, "det.onnx"), ClassificationPath,
                        ModelPath(tier, "rec.onnx"), ModelPath(tier, "dict.txt"));
                    engineTier = tier;
                }
                return engine.Run(raster.Bytes, raster.Width, raster.Height, raster.Stride, ImagePixelFormat.Bgra32).Text.Trim();
            }
        }, cancel);
    }

    /// Drops the cached engine, so switching tiers or deleting a model releases its memory rather
    /// than keeping a hundred megabytes alive for a tier nobody is using.
    public static void Reset()
    { lock (Gate) { engine?.Dispose(); engine = null; engineTier = null; } }

    private readonly record struct Raster(byte[] Bytes, int Width, int Height, int Stride);

    /// Decodes to tightly packed BGRA32, the layout the recogniser reads directly.
    private static Raster Decode(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        // OnLoad releases the file as soon as decoding ends, so the watcher and the user are still
        // free to rename or delete the image afterwards.
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();

        // Phone photos and scans are far larger than any screenshot. Scaling down bounds memory and
        // inference time while leaving text well above the size the recogniser needs.
        var source = (BitmapSource)image;
        var scale = Math.Min(1d, Math.Sqrt(16_000_000d / Math.Max(1d, (double)image.PixelWidth * image.PixelHeight)));
        if (scale < 1d) source = new TransformedBitmap(source, new ScaleTransform(scale, scale));

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var bytes = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(bytes, stride, 0);
        return new Raster(bytes, converted.PixelWidth, converted.PixelHeight, stride);
    }
}
