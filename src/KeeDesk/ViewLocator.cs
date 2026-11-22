using Avalonia.Controls;
using Avalonia.Controls.Templates;
using KeeDesk.ViewModels;

namespace KeeDesk;

public partial class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;
        return AutoBuild(param) ?? new TextBlock { Text = $"View not found: {param.GetType().Name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
