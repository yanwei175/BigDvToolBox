using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Graphics.Imaging;

namespace BigDvToolBox_App.Features.RenameByOCR;

internal enum RenameMode
{
    FixedRegion,
    AnchorText,
}

internal enum OcrSegmentationMode
{
    Auto,
    SingleLine,
    SingleWord,
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

internal sealed record RenderedDocument(
    string SourcePath,
    byte[] PngBytes,
    uint PixelWidth,
    uint PixelHeight,
    int PdfDpi);

public partial class RenamePreviewItem : ObservableObject
{
    public required string OriginalPath { get; init; }
    public string OriginalName => Path.GetFileName(OriginalPath);
    public string OcrText { get; init; } = string.Empty;
    public string TargetPath
    {
        get => string.IsNullOrEmpty(TargetNameError)
            ? Path.Combine(Path.GetDirectoryName(OriginalPath)!, TargetName)
            : string.Empty;
        init => TargetName = Path.GetFileName(value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetPath))]
    [NotifyPropertyChangedFor(nameof(TargetNameError))]
    [NotifyPropertyChangedFor(nameof(IsNoChange))]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    public partial string TargetName { get; set; } = string.Empty;

    public bool CanRename => string.IsNullOrEmpty(Error) &&
        string.IsNullOrEmpty(TargetNameError) && !IsNoChange && CanEditTarget;
    public bool IsNoChange => TargetPath.Equals(OriginalPath, StringComparison.OrdinalIgnoreCase);
    public bool CanEditTarget => Status != "已重命名";
    public string TargetNameError => ValidateTargetName(TargetName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    public partial string Error { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    [NotifyPropertyChangedFor(nameof(CanEditTarget))]
    public partial string Status { get; set; } = "待确认";

    partial void OnTargetNameChanged(string value)
    {
        Error = TargetNameError;
        Status = !string.IsNullOrEmpty(Error) ? "名称无效" : IsNoChange ? "无需修改" : "待确认";
    }

    private static string ValidateTargetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return "请输入目标文件名（含扩展名）。";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith(' ') || name.EndsWith('.') || name.Length > 255)
        {
            return "文件名包含非法字符、过长，或以空格/句点结尾。";
        }

        string deviceName = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL" ||
            (deviceName.Length == 4 &&
             (deviceName.StartsWith("COM", StringComparison.Ordinal) ||
              deviceName.StartsWith("LPT", StringComparison.Ordinal)) &&
             deviceName[3] is >= '1' and <= '9'))
        {
            return "不能使用 Windows 保留的设备名称。";
        }

        return string.Empty;
    }
}

internal sealed record BatchSummary(int Succeeded, int Skipped, int Failed);
