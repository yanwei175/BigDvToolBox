using BigDvToolBox.App.Pages;
using BigDvToolBox_App.Features.RenameByOCR;
using BigDvToolBox_App.Navigation;
using BigDvToolBox_App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BigDvToolBox_App
{
    /// <summary>
    /// 应用 Shell：NavigationView 负责发现工具，TabView 负责承载已打开的工具页面。
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const string HomePageId = "home";

        // 新增工具页面时只需在这里注册一次，左侧导航和标签页会共用这些信息。
        private static readonly IReadOnlyList<ToolPageDefinition> ToolPages =
        [
            new(HomePageId, "Home", Symbol.Home, typeof(HomePage), IsClosable: false),
            new("about", "About", Symbol.Help, typeof(AboutPage)),
            new("sample", "Sample", Symbol.Accept, typeof(MainPage)),
            new("unitCover", "单位转换2", Symbol.Character, typeof(UnitConvertorPage)),
            new("renameByOCR", "Rename By OCR", Symbol.Rename, typeof(RenameByOcrPage)),
            new("settings", "Settings", Symbol.Setting, typeof(SettingsPage), IsSettings: true),
        ];

        private readonly Dictionary<string, ToolPageDefinition> _pageDefinitions =
            ToolPages.ToDictionary(page => page.Id, StringComparer.Ordinal);

        private readonly Dictionary<string, NavigationViewItem> _navigationItems =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, TabViewItem> _openTabs =
            new(StringComparer.Ordinal);

        private bool _isSynchronizingSelection;

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            AppWindow.SetIcon("Assets/WPFGallery.ico");

            BuildNavigationItems();
            OpenOrActivateTool(HomePageId);
        }

        private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
        {
            NavView.IsPaneOpen = !NavView.IsPaneOpen;
        }

        private void BuildNavigationItems()
        {
            foreach (ToolPageDefinition definition in ToolPages.Where(page => !page.IsSettings))
            {
                var navigationItem = new NavigationViewItem
                {
                    Content = definition.Title,
                    Icon = new SymbolIcon(definition.Icon),
                    Tag = definition.Id,
                };

                AutomationProperties.SetAutomationId(navigationItem, $"Nav_{definition.Id}");
                AutomationProperties.SetName(navigationItem, $"打开 {definition.Title}");
                NavView.MenuItems.Add(navigationItem);
                _navigationItems.Add(definition.Id, navigationItem);
            }

            ToolPageDefinition settingsDefinition = ToolPages.Single(page => page.IsSettings);
            if (NavView.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = settingsDefinition.Title;
                settingsItem.Icon = new SymbolIcon(settingsDefinition.Icon);
                settingsItem.Tag = settingsDefinition.Id;
                AutomationProperties.SetAutomationId(settingsItem, $"Nav_{settingsDefinition.Id}");
                AutomationProperties.SetName(settingsItem, $"打开 {settingsDefinition.Title}");
            }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_isSynchronizingSelection)
            {
                return;
            }

            if (args.IsSettingsSelected)
            {
                ToolPageDefinition settingsDefinition = ToolPages.Single(page => page.IsSettings);
                OpenOrActivateTool(settingsDefinition.Id);
            }
            else if (args.SelectedItem is NavigationViewItem { Tag: string pageId })
            {
                OpenOrActivateTool(pageId);
            }
        }

        private void OpenOrActivateTool(string pageId)
        {
            // 单实例策略：页面已打开时只激活对应标签，不创建重复实例。
            if (_openTabs.TryGetValue(pageId, out TabViewItem? existingTab))
            {
                ToolTabView.SelectedItem = existingTab;
                return;
            }

            if (!_pageDefinitions.TryGetValue(pageId, out ToolPageDefinition? definition))
            {
                throw new InvalidOperationException($"Unknown tool page id: {pageId}");
            }

            var frame = new Frame();
            if (!frame.Navigate(definition.PageType))
            {
                throw new InvalidOperationException($"Unable to navigate to {definition.PageType.FullName}.");
            }

            var tab = new TabViewItem
            {
                Content = frame,
                Header = definition.Title,
                IconSource = new SymbolIconSource { Symbol = definition.Icon },
                IsClosable = definition.IsClosable,
                Tag = definition.Id,
            };

            AutomationProperties.SetAutomationId(tab, $"Tab_{definition.Id}");
            AutomationProperties.SetName(tab, $"{definition.Title} 标签页");
            _openTabs.Add(definition.Id, tab);
            ToolTabView.TabItems.Add(tab);
            ToolTabView.SelectedItem = tab;
        }

        private void ToolTabView_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (ToolTabView.SelectedItem is not TabViewItem { Tag: string pageId } ||
                !_pageDefinitions.TryGetValue(pageId, out ToolPageDefinition? definition))
            {
                return;
            }

            // 标签切换时反向同步左侧导航；保护标志避免两个 SelectionChanged 相互触发。
            _isSynchronizingSelection = true;
            try
            {
                NavView.SelectedItem = definition.IsSettings
                    ? NavView.SettingsItem
                    : _navigationItems[definition.Id];
            }
            finally
            {
                _isSynchronizingSelection = false;
            }
        }

        private void ToolTabView_TabCloseRequested(
            TabView sender,
            TabViewTabCloseRequestedEventArgs args)
        {
            if (args.Tab is not TabViewItem { Tag: string pageId } tab ||
                !_pageDefinitions.TryGetValue(pageId, out ToolPageDefinition? definition) ||
                !definition.IsClosable)
            {
                return;
            }

            int closedIndex = sender.TabItems.IndexOf(tab);
            bool wasSelected = ReferenceEquals(sender.SelectedItem, tab);

            _openTabs.Remove(pageId);
            sender.TabItems.Remove(tab);

            if (sender.TabItems.Count == 0)
            {
                OpenOrActivateTool(HomePageId);
                return;
            }

            if (wasSelected)
            {
                // 优先选择关闭位置右侧的标签；关闭末尾标签时选择左侧相邻标签。
                sender.SelectedIndex = Math.Min(closedIndex, sender.TabItems.Count - 1);
            }
        }
    }
}
