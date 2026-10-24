using BigDvToolBox_App.Features.PdfLayOut;
using System.Text;
using System.Xml;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutActualSizePrintTests
{
    private const double Dip = 96d / 25.4;
    private const string Framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private const string Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    [TestMethod]
    public void HardwareMargins_ClipA4WithoutChangingSheetDimensionsOrPositions()
    {
        var paper = new PdfLayOutSize(210 * Dip, 297 * Dip);
        var area = new PdfLayOutRect(3 * Dip, 3 * Dip, 204 * Dip, 291 * Dip);
        var original = PdfLayOutPrintGeometry.CreateVisualPlan(new(210, 297), paper);
        var clipped = PdfLayOutPrintGeometry.CreateVisualPlan(new(210, 297), paper, area);
        Assert.AreEqual(original.Sheet, clipped.Sheet);
        Assert.AreEqual(210d, clipped.Sheet.Width / Dip, 0.0001);
        Assert.AreEqual(297d, clipped.Sheet.Height / Dip, 0.0001);
        Assert.AreEqual(area, clipped.ContentClip);
        Assert.HasCount(4, clipped.UnprintableAreas);
        Assert.IsTrue(clipped.HasHardwareMargins);
    }

    [TestMethod]
    public void AsymmetricAndDifferentPaper_KeepOriginalSizeAndClipToIntersection()
    {
        var paper = new PdfLayOutSize(148 * Dip, 210 * Dip);
        var plan = PdfLayOutPrintGeometry.CreateVisualPlan(new(210, 297), paper, new(2 * Dip, 3 * Dip, 142 * Dip, 202 * Dip));
        Assert.AreEqual(210d, plan.Sheet.Width / Dip, 0.001);
        Assert.AreEqual(297d, plan.Sheet.Height / Dip, 0.001);
        Assert.IsTrue(plan.Sheet.X < 0 && plan.Sheet.Y < 0);
        Assert.AreEqual(plan.PrintableArea, plan.ContentClip);
        Assert.AreEqual(4, plan.UnprintableAreas.Count);
        double printableArea = plan.PrintableArea.Width * plan.PrintableArea.Height;
        double masksArea = plan.UnprintableAreas.Sum(r => r.Width * r.Height);
        Assert.AreEqual(paper.Width * paper.Height, printableArea + masksArea, 0.01);
        var borderless = PdfLayOutPrintGeometry.CreateVisualPlan(new(297, 210), new(297 * Dip, 210 * Dip));
        Assert.IsFalse(borderless.HasHardwareMargins);
        Assert.AreEqual(borderless.Sheet, borderless.ContentClip);
    }

    [TestMethod]
    public void InvalidHardwareAreas_AreRejectedRatherThanFittedOrGuessed()
    {
        foreach (var area in new PdfLayOutRect[] { new(0, 0, 0, 10), new(double.NaN, 0, 100, 100), new(900, 0, 50, 50) })
            Assert.Throws<InvalidOperationException>(() => PdfLayOutPrintGeometry.CreateVisualPlan(new(210, 297), new(794, 1123), area));
        var clipped = PdfLayOutPrintGeometry.CreateVisualPlan(new(210, 297), new(794, 1123), new(-10, -10, 900, 1200));
        Assert.AreEqual(new PdfLayOutRect(0, 0, 794, 1123), clipped.PrintableArea);
    }

    [TestMethod]
    public void None_UsesNamespaceIdentityAndIgnoresInactiveScaleParameters()
    {
        var ticket = Parse("""
            <psf:Feature name="alias:PageScaling"><psf:Option name="alias:None" /></psf:Feature>
            <psf:ParameterInit name="psk:PageScalingScale"><psf:Value>50</psf:Value></psf:ParameterInit>
            """);
        Assert.AreEqual("None", ticket.ScalingStatus);
        ticket.EnsureActualSize();
        Assert.IsNull(ticket.BlockingReason);
        Assert.HasCount(1, ticket.Parameters);
    }

    [TestMethod]
    [DataRow("FitApplicationMediaSizeToPageImageableSize")]
    [DataRow("FitApplicationContentSizeToPageImageableSize")]
    [DataRow("FitApplicationBleedSizeToPageImageableSize")]
    [DataRow("FitApplicationMediaSizeToPageMediaSize")]
    [DataRow("DriverUnknown")]
    public void AutomaticOrUnknownScaling_IsBlockedBeforeSubmission(string option)
    {
        var ticket = Parse($"<psf:Feature name='psk:PageScaling'><psf:Option name='psk:{option}' /></psf:Feature>");
        Assert.IsNotNull(ticket.BlockingReason);
        Assert.Throws<InvalidOperationException>(ticket.EnsureActualSize);
        StringAssert.Contains(ticket.Xml, option);
    }

    [TestMethod]
    [DataRow("100", true)]
    [DataRow("70.7", false)]
    [DataRow("101", false)]
    [DataRow("NaN", false)]
    public void CustomSquare_ResolvesParametersAndAllowsOnly100Percent(string percentage, bool allowed)
    {
        var ticket = Parse($"""
            <psf:Feature name="psk:PageScaling"><psf:Option name="psk:CustomSquare">
                <psf:ScoredProperty name="psk:Scale"><psf:ParameterRef name="psk:PageScalingScale" /></psf:ScoredProperty>
            </psf:Option></psf:Feature>
            <psf:ParameterInit name="alias:PageScalingScale"><psf:Value>{percentage}</psf:Value></psf:ParameterInit>
            """);
        if (allowed) { ticket.EnsureActualSize(); Assert.AreEqual("Custom100Percent", ticket.ScalingStatus); }
        else Assert.Throws<InvalidOperationException>(ticket.EnsureActualSize);
    }

    [TestMethod]
    [DataRow("100", "100", true)]
    [DataRow("100", "50", false)]
    public void AnisotropicScaling_RequiresBothAxesAt100Percent(string x, string y, bool allowed)
    {
        var ticket = Parse($"""
            <psf:Feature name="psk:PageScaling"><psf:Option name="psk:Custom">
                <psf:ScoredProperty name="psk:ScaleWidth"><psf:Value>{x}</psf:Value></psf:ScoredProperty>
                <psf:ScoredProperty name="psk:ScaleHeight"><psf:Value>{y}</psf:Value></psf:ScoredProperty>
            </psf:Option></psf:Feature>
            """);
        if (allowed) ticket.EnsureActualSize();
        else Assert.Throws<InvalidOperationException>(ticket.EnsureActualSize);
    }

    [TestMethod]
    public void PrivateOrOmittedSettings_AreRecordedAsUnspecified_NotMistakenForStandardNone()
    {
        var ticket = Parse("""
            <psf:Feature name="vendor:PageScaling"><psf:Option name="vendor:Reduce" /></psf:Feature>
            <psf:Feature name="psk:PageMediaSize"><psf:Option name="psk:ISOA4" /></psf:Feature>
            """);
        Assert.AreEqual("NotSpecified", ticket.ScalingStatus);
        Assert.IsNull(ticket.BlockingReason);
        Assert.IsTrue(ticket.Features.Any(f => f.Namespace == "urn:vendor" && f.Name == "PageScaling"));
        StringAssert.Contains(ticket.Xml, "Reduce");
        Assert.AreEqual("NotSpecified", Parse(string.Empty).ScalingStatus);
    }

    [TestMethod]
    [DataRow(1, true)]
    [DataRow(2, false)]
    public void NUp_IsBlockedWhenItWouldShrinkTheImposedSheet(int count, bool allowed)
    {
        var ticket = Parse($"""
            <psf:Feature name="psk:JobNUpAllDocumentsContiguously"><psf:Option name="psk:NUp">
                <psf:ScoredProperty name="psk:PagesPerSheet"><psf:Value>{count}</psf:Value></psf:ScoredProperty>
            </psf:Option></psf:Feature>
            """);
        if (allowed) ticket.EnsureActualSize();
        else Assert.Throws<InvalidOperationException>(ticket.EnsureActualSize);
    }

    [TestMethod]
    public void OptionalHardwareArea_UsesMicrons_AndUtf16TicketCanBeRead()
    {
        string content = """
            <psf:Property name="psk:PageImageableSize"><psf:Property name="psk:ImageableArea">
                <psf:Property name="psk:OriginWidth"><psf:Value>3000</psf:Value></psf:Property>
                <psf:Property name="psk:OriginHeight"><psf:Value>3000</psf:Value></psf:Property>
                <psf:Property name="psk:ExtentWidth"><psf:Value>204000</psf:Value></psf:Property>
                <psf:Property name="psk:ExtentHeight"><psf:Value>291000</psf:Value></psf:Property>
            </psf:Property></psf:Property>
            """;
        using var stream = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Xml(content))]);
        var ticket = PdfLayOutPrintTicket.Parse(stream);
        Assert.IsNotNull(ticket.ImageableAreaDip);
        Assert.AreEqual(3d, ticket.ImageableAreaDip.Value.X / Dip, 0.0001);
        Assert.AreEqual(204d, ticket.ImageableAreaDip.Value.Width / Dip, 0.0001);
    }

    [TestMethod]
    public void DtdAndOversizedTickets_AreRejected()
    {
        using var dtd = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY e 'x'>]><a>&e;</a>"));
        Assert.Throws<XmlException>(() => PdfLayOutPrintTicket.Parse(dtd));
        using var large = new MemoryStream(Encoding.UTF8.GetBytes("<a>" + new string('x', PdfLayOutPrintTicket.MaximumBytes + 1) + "</a>"));
        Assert.Throws<XmlException>(() => PdfLayOutPrintTicket.Parse(large));
    }

    private static PdfLayOutPrintTicket Parse(string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml(content)));
        return PdfLayOutPrintTicket.Parse(stream);
    }
    private static string Xml(string content) => $"<psf:PrintTicket xmlns:psf='{Framework}' xmlns:psk='{Keywords}' xmlns:alias='{Keywords}' xmlns:vendor='urn:vendor'>{content}</psf:PrintTicket>";
}
