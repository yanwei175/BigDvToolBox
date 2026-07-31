using BigDvToolBox_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BigDvToolBox_App.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class UnitConvertorPage : Page
    {
        public UnitConverterViewModel ViewModel { get; } = new();

        public UnitConvertorPage()
        {
            InitializeComponent();
            RegisterLengthEnterHandlers();
        }

        private void RegisterLengthEnterHandlers()
        {
            RegisterEnterHandler(MetricValueNumberBox, MetricInput_KeyUp);
            RegisterEnterHandler(MetricUnitComboBox, MetricInput_KeyUp);
            RegisterEnterHandler(DecimalInchNumberBox, DecimalInchInput_KeyUp);
            RegisterEnterHandler(FractionWholeNumberBox, FractionInput_KeyUp);
            RegisterEnterHandler(FractionNumeratorNumberBox, FractionInput_KeyUp);
            RegisterEnterHandler(FractionDenominatorComboBox, FractionInput_KeyUp);
        }

        private static void RegisterEnterHandler(UIElement element, KeyEventHandler handler) =>
            element.AddHandler(UIElement.KeyUpEvent, handler, handledEventsToo: true);

        private void MetricInput_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                ConvertFromMetricControls();
                e.Handled = true;
            }
        }

        private void MetricInput_LostFocus(object sender, RoutedEventArgs e) =>
            ConvertFromMetricControls();

        private void DecimalInchInput_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                ConvertFromDecimalInchControl();
                e.Handled = true;
            }
        }

        private void DecimalInchInput_LostFocus(object sender, RoutedEventArgs e) =>
            ConvertFromDecimalInchControl();

        private void FractionInput_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                ConvertFromFractionControls();
                e.Handled = true;
            }
        }

        private void FractionInput_LostFocus(object sender, RoutedEventArgs e) =>
            ConvertFromFractionControls();

        private void ConvertFromMetricControls()
        {
            ViewModel.MetricValue = MetricValueNumberBox.Value;
            ViewModel.MetricUnitIndex = MetricUnitComboBox.SelectedIndex;
            ViewModel.ConvertLengthFromMetric();
        }

        private void ConvertFromDecimalInchControl()
        {
            ViewModel.DecimalInchValue = DecimalInchNumberBox.Value;
            ViewModel.ConvertLengthFromDecimalInches();
        }

        private void ConvertFromFractionControls()
        {
            ViewModel.FractionWholeInches = FractionWholeNumberBox.Value;
            ViewModel.FractionNumerator = FractionNumeratorNumberBox.Value;
            ViewModel.FractionDenominatorIndex = FractionDenominatorComboBox.SelectedIndex;
            ViewModel.ConvertLengthFromFractionalInches();
        }
    }
}
