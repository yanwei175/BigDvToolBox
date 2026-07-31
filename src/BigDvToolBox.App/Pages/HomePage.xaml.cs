using BigDvToolBox_App.Navigation;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BigDvToolBox.App.Pages
{
    public sealed partial class HomePage : Page
    {
        private Action<string>? _openTool;

        internal ObservableCollection<ToolPageDefinition> Tools { get; } = [];

        public HomePage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is not HomePageNavigationContext context)
            {
                return;
            }

            _openTool = context.OpenTool;
            Tools.Clear();
            foreach (ToolPageDefinition tool in context.Tools)
            {
                Tools.Add(tool);
            }
        }

        private void ToolCards_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ToolPageDefinition tool)
            {
                _openTool?.Invoke(tool.Id);
            }
        }
    }
}
