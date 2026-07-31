using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BigDvToolBox_App.Features.ImageProcessing;

internal interface IImageResizeService
{
    Task<ImageResizeResult> ResizeAsync(
        string inputPath,
        string outputPath,
        ImageResizeOptions options,
        CancellationToken cancellationToken);
}

internal sealed class ImageResizeService : IImageResizeService
{
    private const string OrientationProperty = "System.Photo.Orientation";

    public async Task<ImageResizeResult> ResizeAsync(
        string inputPath,
        string outputPath,
        ImageResizeOptions options,
        CancellationToken cancellationToken)
    {
        if (!ImageProcessingFileRules.IsSupportedImageFile(inputPath))
        {
            throw new InvalidDataException("仅支持 JPG、JPEG 和 PNG 图片。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        DecodedImage decoded = await DecodeAsync(
            inputPath,
            options.LongEdge,
            cancellationToken);

        bool encodeAsJpeg = options.OutputFormat == ImageOutputFormat.Jpeg ||
            (options.OutputFormat == ImageOutputFormat.Original &&
             ImageProcessingFileRules.IsJpeg(inputPath));
        if (encodeAsJpeg)
        {
            await Task.Run(
                () => CompositeTransparencyOnWhite(decoded.Pixels),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath) ??
            throw new InvalidOperationException("无法确定输出目录。");
        Directory.CreateDirectory(outputDirectory);

        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(temporaryPath))
            {
            }

            StorageFile temporaryFile =
                await StorageFile.GetFileFromPathAsync(temporaryPath);
            using IRandomAccessStream output =
                await temporaryFile.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await CreateEncoderAsync(
                encodeAsJpeg,
                output,
                options.JpegQuality);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                decoded.Width,
                decoded.Height,
                96,
                96,
                decoded.Pixels);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();

            output.Dispose();
            File.Move(
                temporaryPath,
                fullOutputPath,
                overwrite: options.ConflictPolicy ==
                    ImageConflictPolicy.Overwrite);
            return new ImageResizeResult(
                decoded.Width,
                decoded.Height,
                fullOutputPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<DecodedImage> DecodeAsync(
        string inputPath,
        int targetLongEdge,
        CancellationToken cancellationToken)
    {
        StorageFile inputFile = await StorageFile.GetFileFromPathAsync(inputPath);
        using IRandomAccessStream input =
            await inputFile.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        ushort orientation = await ReadOrientationAsync(decoder);
        ImageDimensions rawDimensions = ImageProcessingFileRules.CalculateDimensions(
            decoder.PixelWidth,
            decoder.PixelHeight,
            targetLongEdge);

        var transform = new BitmapTransform
        {
            ScaledWidth = rawDimensions.Width,
            ScaledHeight = rawDimensions.Height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        ApplyOrientation(transform, orientation);

        bool swapsDimensions =
            transform.Rotation is BitmapRotation.Clockwise90Degrees or
            BitmapRotation.Clockwise270Degrees;
        uint outputWidth = swapsDimensions
            ? rawDimensions.Height
            : rawDimensions.Width;
        uint outputHeight = swapsDimensions
            ? rawDimensions.Width
            : rawDimensions.Height;

        cancellationToken.ThrowIfCancellationRequested();
        PixelDataProvider provider = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        cancellationToken.ThrowIfCancellationRequested();
        return new DecodedImage(
            provider.DetachPixelData(),
            outputWidth,
            outputHeight);
    }

    private static async Task<ushort> ReadOrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            BitmapPropertySet properties =
                await decoder.BitmapProperties.GetPropertiesAsync(
                    [OrientationProperty]);
            if (properties.TryGetValue(
                    OrientationProperty,
                    out BitmapTypedValue? orientation) &&
                orientation.Value is not null)
            {
                return Convert.ToUInt16(orientation.Value);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidCastException or
            NotSupportedException)
        {
            // Missing or unsupported EXIF data means the image is already top-left oriented.
        }

        return 1;
    }

    private static void ApplyOrientation(
        BitmapTransform transform,
        ushort orientation)
    {
        switch (orientation)
        {
            case 2:
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case 3:
                transform.Rotation = BitmapRotation.Clockwise180Degrees;
                break;
            case 4:
                transform.Flip = BitmapFlip.Vertical;
                break;
            case 5:
                transform.Rotation = BitmapRotation.Clockwise90Degrees;
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case 6:
                transform.Rotation = BitmapRotation.Clockwise90Degrees;
                break;
            case 7:
                transform.Rotation = BitmapRotation.Clockwise270Degrees;
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case 8:
                transform.Rotation = BitmapRotation.Clockwise270Degrees;
                break;
        }
    }

    private static async Task<BitmapEncoder> CreateEncoderAsync(
        bool encodeAsJpeg,
        IRandomAccessStream output,
        int jpegQuality)
    {
        if (!encodeAsJpeg)
        {
            return await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId,
                output);
        }

        var properties = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(
                Math.Clamp(jpegQuality, 1, 100) / 100f,
                PropertyType.Single),
        };
        return await BitmapEncoder.CreateAsync(
            BitmapEncoder.JpegEncoderId,
            output,
            properties);
    }

    private static void CompositeTransparencyOnWhite(byte[] pixels)
    {
        for (int index = 0; index <= pixels.Length - 4; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha == 255)
            {
                continue;
            }

            int inverseAlpha = 255 - alpha;
            pixels[index] = CompositeChannel(pixels[index], alpha, inverseAlpha);
            pixels[index + 1] =
                CompositeChannel(pixels[index + 1], alpha, inverseAlpha);
            pixels[index + 2] =
                CompositeChannel(pixels[index + 2], alpha, inverseAlpha);
            pixels[index + 3] = 255;
        }
    }

    private static byte CompositeChannel(
        byte channel,
        int alpha,
        int inverseAlpha) =>
        (byte)(((channel * alpha) + (255 * inverseAlpha) + 127) / 255);

    private readonly record struct DecodedImage(
        byte[] Pixels,
        uint Width,
        uint Height);
}
