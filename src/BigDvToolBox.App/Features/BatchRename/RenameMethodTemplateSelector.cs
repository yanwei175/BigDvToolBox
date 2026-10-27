using BigDvToolBox.Renaming.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BigDvToolBox_App.Features.BatchRename;

public sealed class RenameMethodTemplateSelector : DataTemplateSelector
{
    public DataTemplate ReplaceTemplate { get; set; } = null!;
    public DataTemplate InsertTemplate { get; set; } = null!;
    public DataTemplate DeleteTemplate { get; set; } = null!;
    public DataTemplate RemoveCharactersTemplate { get; set; } = null!;
    public DataTemplate CaseTemplate { get; set; } = null!;
    public DataTemplate NumberingTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item) => item is RenameMethodViewModel model ? model.Method switch
    {
        RenameMethod.Replace => ReplaceTemplate,
        RenameMethod.Insert => InsertTemplate,
        RenameMethod.DeleteRange => DeleteTemplate,
        RenameMethod.RemoveCharacters => RemoveCharactersTemplate,
        RenameMethod.ChangeCase => CaseTemplate,
        _ => NumberingTemplate,
    } : ReplaceTemplate;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
