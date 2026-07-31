using BigDvToolBox_App.Features.ImageProcessing;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BigDvToolBox.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ImageProcessingTests
{
    [TestMethod]
    [DataRow(4000u, 3000u, 1500, 1500u, 1125u)]
    [DataRow(3000u, 4000u, 1500, 1125u, 1500u)]
    [DataRow(2000u, 2000u, 1500, 1500u, 1500u)]
    [DataRow(800u, 600u, 1500, 800u, 600u)]
    public void CalculateDimensions_PreservesAspectRatioAndNeverUpscales(
        uint sourceWidth,
        uint sourceHeight,
        int targetLongEdge,
        uint expectedWidth,
        uint expectedHeight)
    {
        ImageDimensions result = ImageProcessingFileRules.CalculateDimensions(
            sourceWidth,
            sourceHeight,
            targetLongEdge);

        Assert.AreEqual(expectedWidth, result.Width);
        Assert.AreEqual(expectedHeight, result.Height);
    }

    [TestMethod]
    public void ResolveOutputPath_UsesSourceDirectoryAndSequentialSuffixes()
    {
        string folder = CreateTempFolder();
        try
        {
            string input = Path.Combine(folder, "photo.JPEG");
            File.WriteAllText(input, "source");
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string first = ImageProcessingFileRules.ResolveOutputPath(
                input,
                null,
                ImageOutputFormat.Original,
                ImageConflictPolicy.AddSuffix,
                reserved);
            File.WriteAllText(first, "first");
            string second = ImageProcessingFileRules.ResolveOutputPath(
                input,
                null,
                ImageOutputFormat.Original,
                ImageConflictPolicy.AddSuffix,
                reserved);

            Assert.AreEqual(Path.Combine(folder, "photo_1.JPEG"), first);
            Assert.AreEqual(Path.Combine(folder, "photo_2.JPEG"), second);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void SettingsStore_SavesEachDefaultIndependently()
    {
        string folder = CreateTempFolder();
        try
        {
            string settingsPath = Path.Combine(folder, "settings.json");
            var store = new JsonImageProcessingSettingsStore(settingsPath);

            store.SaveLongEdge(2048);
            store.SaveOutputDirectory(folder);
            store.SaveConflictPolicy(ImageConflictPolicy.Overwrite);
            ImageProcessingDefaults loaded = store.Load();

            Assert.AreEqual(2048, loaded.LongEdge);
            Assert.AreEqual(Path.GetFullPath(folder), loaded.OutputDirectory);
            Assert.AreEqual(
                ImageConflictPolicy.Overwrite,
                loaded.ConflictPolicy);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ViewModel_AddFiles_FiltersAndDeduplicates()
    {
        string folder = CreateTempFolder();
        try
        {
            string image = Path.Combine(folder, "sample.JPG");
            string text = Path.Combine(folder, "notes.txt");
            File.WriteAllText(image, "image");
            File.WriteAllText(text, "text");
            var viewModel = new ImageProcessingViewModel(
                new FakeResizeService(),
                new MemorySettingsStore());

            int added = viewModel.AddFiles([image, image.ToUpperInvariant(), text]);

            Assert.AreEqual(1, added);
            Assert.HasCount(1, viewModel.Files);
            Assert.AreEqual(ImageStatusSeverity.Warning, viewModel.BatchSeverity);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task BatchProcessing_ContinuesAfterOneFileFails()
    {
        string folder = CreateTempFolder();
        try
        {
            string first = Path.Combine(folder, "first.jpg");
            string second = Path.Combine(folder, "second.png");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");
            var service = new FakeResizeService(second);
            var viewModel = new ImageProcessingViewModel(
                service,
                new MemorySettingsStore());
            viewModel.AddFiles([first, second]);

            ImageBatchSummary summary =
                await viewModel.StartProcessingAsync();

            Assert.AreEqual(1, summary.Succeeded);
            Assert.AreEqual(1, summary.Failed);
            Assert.AreEqual(
                ImageProcessingFileRules.DefaultJpegQuality,
                service.LastOptions.JpegQuality);
            Assert.AreEqual(
                ImageFileState.Completed,
                viewModel.Files[0].State);
            Assert.AreEqual(ImageFileState.Failed, viewModel.Files[1].State);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task NativeResize_ProducesDecodableJpegWithWhiteAlphaBackground()
    {
        string folder = CreateTempFolder();
        try
        {
            string source = Path.Combine(folder, "transparent.png");
            string output = Path.Combine(folder, "transparent.jpg");
            await CreateTransparentPngAsync(source);
            var service = new ImageResizeService();

            ImageResizeResult result = await service.ResizeAsync(
                source,
                output,
                new ImageResizeOptions(
                    1500,
                    90,
                    ImageOutputFormat.Jpeg,
                    ImageConflictPolicy.Overwrite),
                CancellationToken.None);

            Assert.AreEqual(2u, result.Width);
            Assert.AreEqual(1u, result.Height);
            Assert.IsTrue(new FileInfo(output).Length > 0);

            byte[] pixels = await DecodePixelsAsync(output);
            Assert.IsGreaterThan(190, pixels[0]);
            Assert.IsGreaterThan(190, pixels[1]);
            Assert.IsGreaterThan(190, pixels[2]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task CreateTransparentPngAsync(string path)
    {
        using (File.Create(path))
        {
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream =
            await file.OpenAsync(FileAccessMode.ReadWrite);
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId,
            stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            2,
            1,
            96,
            96,
            [
                0, 0, 0, 0,
                0, 0, 255, 255,
            ]);
        await encoder.FlushAsync();
    }

    private static async Task<byte[]> DecodePixelsAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using IRandomAccessStream stream =
            await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        return provider.DetachPixelData();
    }

    private static string CreateTempFolder()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "BigDvToolBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeResizeService(string? failingPath = null)
        : IImageResizeService
    {
        internal ImageResizeOptions LastOptions { get; private set; }

        public Task<ImageResizeResult> ResizeAsync(
            string inputPath,
            string outputPath,
            ImageResizeOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastOptions = options;
            return string.Equals(
                inputPath,
                failingPath,
                StringComparison.OrdinalIgnoreCase)
                ? Task.FromException<ImageResizeResult>(
                    new InvalidOperationException("模拟失败"))
                : Task.FromResult(new ImageResizeResult(100, 100, outputPath));
        }
    }

    private sealed class MemorySettingsStore : IImageProcessingSettingsStore
    {
        private ImageProcessingDefaults _defaults = new();

        public ImageProcessingDefaults Load() => _defaults;

        public void SaveLongEdge(int value) =>
            _defaults = _defaults with { LongEdge = value };

        public void SaveOutputDirectory(string? value) =>
            _defaults = _defaults with { OutputDirectory = value };

        public void SaveConflictPolicy(ImageConflictPolicy value) =>
            _defaults = _defaults with { ConflictPolicy = value };
    }
}
