using BigDvToolBox_App.ViewModels;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class UnitConverterViewModelTests
{
    [TestMethod]
    public void ConvertLengthFromMetric_UpdatesDecimalAndFractionalInches()
    {
        var viewModel = new UnitConverterViewModel
        {
            MetricUnitIndex = 0,
            MetricValue = 25.4,
        };

        viewModel.ConvertLengthFromMetric();

        Assert.AreEqual(1, viewModel.DecimalInchValue, 0.000001);
        Assert.AreEqual(1, viewModel.FractionWholeInches, 0.000001);
        Assert.AreEqual(0, viewModel.FractionNumerator, 0.000001);
        Assert.AreEqual("已从毫米转换", viewModel.LengthStatus);
    }

    [TestMethod]
    public void ConvertLengthFromDecimalInches_UpdatesMetricAndFractionalInches()
    {
        var viewModel = new UnitConverterViewModel
        {
            MetricUnitIndex = 0,
            FractionDenominatorIndex = 3,
            DecimalInchValue = 1.5,
        };

        viewModel.ConvertLengthFromDecimalInches();

        Assert.AreEqual(38.1, viewModel.MetricValue, 0.000001);
        Assert.AreEqual(1, viewModel.FractionWholeInches, 0.000001);
        Assert.AreEqual(8, viewModel.FractionNumerator, 0.000001);
        Assert.AreEqual("已从小数英寸转换", viewModel.LengthStatus);
    }

    [TestMethod]
    public void ConvertLengthFromFractionalInches_CarriesAFullFractionIntoWholeInches()
    {
        var viewModel = new UnitConverterViewModel
        {
            MetricUnitIndex = 0,
            FractionDenominatorIndex = 3,
            FractionWholeInches = 1,
            FractionNumerator = 16,
        };

        viewModel.ConvertLengthFromFractionalInches();

        Assert.AreEqual(50.8, viewModel.MetricValue, 0.000001);
        Assert.AreEqual(2, viewModel.DecimalInchValue, 0.000001);
        Assert.AreEqual(2, viewModel.FractionWholeInches, 0.000001);
        Assert.AreEqual(0, viewModel.FractionNumerator, 0.000001);
    }

    [TestMethod]
    public void FlowConversion_UpdatesInBothDirections()
    {
        var viewModel = new UnitConverterViewModel
        {
            FlowSourceUnitIndex = 0,
            FlowInputValue = 3.785411784,
        };

        Assert.AreEqual("1 US GPM", viewModel.FlowResult);
        Assert.AreEqual("美制加仑/分钟 (US GPM)", viewModel.FlowTargetUnit);

        viewModel.FlowSourceUnitIndex = 1;
        viewModel.FlowInputValue = 1;

        Assert.AreEqual("3.785412 L/min", viewModel.FlowResult);
        Assert.AreEqual("升/分钟 (L/min)", viewModel.FlowTargetUnit);
    }

    [TestMethod]
    public void InvalidInputs_ShowValidationMessages()
    {
        var viewModel = new UnitConverterViewModel
        {
            MetricValue = double.NaN,
        };

        viewModel.ConvertLengthFromMetric();

        Assert.AreEqual("请输入有效的非负公制数值", viewModel.LengthStatus);

        viewModel.FlowInputValue = double.NaN;

        Assert.AreEqual("请输入有效的非负数值", viewModel.FlowResult);
        Assert.AreEqual(string.Empty, viewModel.FlowTargetUnit);
    }

    [TestMethod]
    public void InvalidDecimalAndFractionInputs_ShowSourceSpecificMessages()
    {
        var viewModel = new UnitConverterViewModel
        {
            DecimalInchValue = -1,
        };

        viewModel.ConvertLengthFromDecimalInches();

        Assert.AreEqual("请输入有效的非负小数英寸", viewModel.LengthStatus);

        viewModel.FractionWholeInches = double.PositiveInfinity;
        viewModel.FractionNumerator = 1;
        viewModel.ConvertLengthFromFractionalInches();

        Assert.AreEqual("请输入有效的非负英寸分数", viewModel.LengthStatus);
    }
}
