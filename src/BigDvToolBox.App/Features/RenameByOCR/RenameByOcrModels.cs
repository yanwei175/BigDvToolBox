using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Graphics.Imaging;

namespace BigDvToolBox_App.Features.RenameByOCR;

internal enum RenameMode
{
    FixedRegion,
    AnchorText,
}

internal readonly record struct NormalizedOcrRegion(double X, double Y, double Width, double Height)
{
    public bool IsValid => Width > 0 && Height > 0;

    public BitmapBounds ToPixelBounds(uint pixelWidth, uint pixelHeight)
    {
        double left = Math.Clamp(X, 0, 1);
        double top = Math.Clamp(Y, 0, 1);
        double right = Math.Clamp(X + Width, 0, 1);
        double bottom = Math.Clamp(Y + Height, 0, 1);

        uint x = (uint)Math.Floor(left * pixelWidth);
        uint y = (uint)Math.Floor(top * pixelHeight);
        uint width = (uint)Math.Max(1, Math.Ceiling(right * pixelWidth) - x);
        uint height = (uint)Math.Max(1, Math.Ceiling(bottom * pixelHeight) - y);

        width = Math.Min(width, pixelWidth - Math.Min(x, pixelWidth - 1));
        height = Math.Min(height, pixelHeight - Math.Min(y, pixelHeight - 1));
        return new BitmapBounds { X = x, Y = y, Width = width, Height = height };
    }
}

internal sealed record RenderedDocument(byte[] PngBytes, uint PixelWidth, uint PixelHeight);

public partial class RenamePreviewItem : ObservableObject
{
    public required string OriginalPath { get; init; }
    public string OriginalName => Path.GetFileName(OriginalPath);
    public string OcrText { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string TargetName => string.IsNullOrEmpty(TargetPath) ? string.Empty : Path.GetFileName(TargetPath);
    public bool CanRename => string.IsNullOrEmpty(Error) && !IsNoChange;
    public bool IsNoChange { get; init; }

    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = "待确认";
}

internal sealed record BatchSummary(int Succeeded, int Skipped, int Failed);
