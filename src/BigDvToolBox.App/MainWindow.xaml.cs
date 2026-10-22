using BigDvToolBox.App.Pages;
using BigDvToolBox_App.Features.ImageProcessing;
using BigDvToolBox_App.Features.PdfLayOut;
using BigDvToolBox_App.Features.PdfOutlineFonts;
using BigDvToolBox_App.Features.RenameByOCR;
using BigDvToolBox_App.Navigation;
using BigDvToolBox_App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

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
        private const double InitialWidthRatio = 0.85;
        private const double InitialHeightRatio = 0.82;

        // 新增工具页面时只需在这里注册一次，左侧导航和标签页会共用这些信息。
        private static readonly IReadOnlyList<ToolPageDefinition> ToolPages =
        [
            new(
                HomePageId,
                "首页",
                Symbol.Home,
                typeof(HomePage),
                "浏览并快速打开工具箱中的工具。",
                IsClosable: false,
                ShowOnHome: false),
            new(
                "unitCover",
                "单位转换",
                Symbol.Switch,
                typeof(UnitConvertorPage),
                "在公制、小数英寸、分数英寸以及常用流量单位之间转换。"),
            new(
                "renameByOCR",
                "OCR 重命名",
                Symbol.Rename,
                typeof(RenameByOcrPage),
                "识别图片中的文字，并根据识别结果批量整理文件名。"),
            new(
                "pdfOutlineFonts",
                "PDF 字体转曲",
                Symbol.Document,
                typeof(PdfOutlineFontsPage),
                "将 PDF 中的字体转换为轮廓，减少跨设备显示差异。"),
            new(
                "pdfLayOut",
                "PDF 拼版",
                Symbol.Page,
                typeof(PdfLayOutPage),
                "设置印张、版式和印刷标记，预览 PDF 拼版布局。"),
            new(
                "imageProcessing",
                "图片处理",
                Symbol.Pictures,
                typeof(ImageProcessingPage),
                "批量调整图片尺寸和输出设置，快速完成常见图像处理。"),
            new(
                "settings",
                "设置",
                Symbol.Setting,
                typeof(SettingsPage),
                "调整工具箱的外观和行为。",
                IsSettings: true,
                ShowOnHome: false),
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
            // SetIcon 需要文件系统路径；发布后不能依赖进程的当前工作目录。
            string iconPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "BigDvToolBoxIcon.ico");
            AppWindow.SetIcon(iconPath);
            SetInitialWindowBounds();

            BuildNavigationItems();
            OpenOrActivateTool(HomePageId);
        }

        private void SetInitialWindowBounds()
        {
            DisplayArea displayArea = DisplayArea.GetFromWindowId(
                AppWindow.Id,
                DisplayAreaFallback.Primary);
            RectInt32 workArea = displayArea.WorkArea;

            int width = (int)Math.Round(workArea.Width * InitialWidthRatio);
            int height = (int)Math.Round(workArea.Height * InitialHeightRatio);
            int x = workArea.X + ((workArea.Width - width) / 2);
            int y = workArea.Y + ((workArea.Height - height) / 2);

            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
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
            object? navigationParameter = definition.Id == HomePageId
                ? new HomePageNavigationContext(
                    ToolPages.Where(page => page.ShowOnHome).ToArray(),
                    OpenOrActivateTool)
                : null;

            if (!frame.Navigate(definition.PageType, navigationParameter))
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
