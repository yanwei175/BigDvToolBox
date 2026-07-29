using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;

namespace BigDvToolBox_App.ViewModels;

/// <summary>
/// 单位转换页面的状态与换算逻辑。
/// </summary>
public partial class UnitConverterViewModel : ObservableObject
{
    private const double MillimetersPerInch = 25.4;
    private const double LitersPerUsGallon = 3.785411784;

    private static readonly int[] FractionDenominators = [2, 4, 8, 16, 32, 64];

    [ObservableProperty]
    public partial double MetricValue { get; set; } = 25.4;

    /// <summary>0 表示毫米，1 表示厘米。</summary>
    [ObservableProperty]
    public partial int MetricUnitIndex { get; set; }

    [ObservableProperty]
    public partial double DecimalInchValue { get; set; } = 1;

    [ObservableProperty]
    public partial double FractionWholeInches { get; set; } = 1;

    [ObservableProperty]
    public partial double FractionNumerator { get; set; }

    [ObservableProperty]
    public partial int FractionDenominatorIndex { get; set; } = 3;

    [ObservableProperty]
    public partial string LengthStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double FlowInputValue { get; set; } = 1;

    /// <summary>0 表示 L/min，1 表示 US GPM。</summary>
    [ObservableProperty]
    public partial int FlowSourceUnitIndex { get; set; }

    [ObservableProperty]
    public partial string FlowResult { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FlowTargetUnit { get; private set; } = string.Empty;

    public UnitConverterViewModel()
    {
        ConvertLengthFromMetric();
        UpdateFlowResult();
    }

    partial void OnFlowInputValueChanged(double value) => UpdateFlowResult();

    partial void OnFlowSourceUnitIndexChanged(int value) => UpdateFlowResult();

    /// <summary>以当前 mm/cm 输入为源，更新两种英寸表示。</summary>
    public void ConvertLengthFromMetric()
    {
        if (!IsValidInput(MetricValue))
        {
            LengthStatus = "请输入有效的非负公制数值";
            return;
        }

        double millimeters = MetricUnitIndex == 0 ? MetricValue : MetricValue * 10;
        SetLengthFromInches(millimeters / MillimetersPerInch);
        LengthStatus = MetricUnitIndex == 0 ? "已从毫米转换" : "已从厘米转换";
    }

    /// <summary>以当前小数英寸输入为源，更新公制和分数英寸。</summary>
    public void ConvertLengthFromDecimalInches()
    {
        if (!IsValidInput(DecimalInchValue))
        {
            LengthStatus = "请输入有效的非负小数英寸";
            return;
        }

        SetLengthFromInches(DecimalInchValue);
        LengthStatus = "已从小数英寸转换";
    }

    /// <summary>以当前整数、分子和分母为源，更新公制和小数英寸。</summary>
    public void ConvertLengthFromFractionalInches()
    {
        if (!IsValidInput(FractionWholeInches) || !IsValidInput(FractionNumerator))
        {
            LengthStatus = "请输入有效的非负英寸分数";
            return;
        }

        int denominator = GetSelectedDenominator();
        double wholeInches = Math.Floor(FractionWholeInches);
        double numerator = Math.Round(FractionNumerator, MidpointRounding.AwayFromZero);
        double inches = wholeInches + (numerator / denominator);

        SetLengthFromInches(inches);
        LengthStatus = "已从分数英寸转换";
    }

    private void SetLengthFromInches(double inches)
    {
        double millimeters = inches * MillimetersPerInch;
        MetricValue = MetricUnitIndex == 0 ? millimeters : millimeters / 10;
        DecimalInchValue = inches;

        int denominator = GetSelectedDenominator();
        int wholeInches = (int)Math.Floor(inches);
        int numerator = (int)Math.Round(
            (inches - wholeInches) * denominator,
            MidpointRounding.AwayFromZero);

        if (numerator == denominator)
        {
            wholeInches++;
            numerator = 0;
        }

        FractionWholeInches = wholeInches;
        FractionNumerator = numerator;
    }

    private void UpdateFlowResult()
    {
        if (!IsValidInput(FlowInputValue))
        {
            FlowTargetUnit = string.Empty;
            FlowResult = "请输入有效的非负数值";
            return;
        }

        if (FlowSourceUnitIndex == 0)
        {
            double gallonsPerMinute = FlowInputValue / LitersPerUsGallon;
            FlowTargetUnit = "美制加仑/分钟 (US GPM)";
            FlowResult = $"{FormatNumber(gallonsPerMinute)} US GPM";
        }
        else
        {
            double litersPerMinute = FlowInputValue * LitersPerUsGallon;
            FlowTargetUnit = "升/分钟 (L/min)";
            FlowResult = $"{FormatNumber(litersPerMinute)} L/min";
        }
    }

    private static bool IsValidInput(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    private static string FormatNumber(double value) =>
        value.ToString("0.######", CultureInfo.CurrentCulture);

    private int GetSelectedDenominator() =>
        FractionDenominators[
            Math.Clamp(FractionDenominatorIndex, 0, FractionDenominators.Length - 1)];
}
