using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KeePassModern.Core.Kdbx;

namespace KeePassModern.ViewModels;

public sealed partial class GroupItemViewModel : ViewModelBase
{
    public KdbxGroup Model { get; }
    public ObservableCollection<GroupItemViewModel> Children { get; } = new();

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _entryCount;
    [ObservableProperty] private bool _isExpanded;

    public GroupItemViewModel(KdbxGroup group, Guid recycleBinUuid)
    {
        Model = group;
        _name = group.Name;
        _isExpanded = group.IsExpanded;
        _entryCount = CountEntries(group);

        foreach (var sub in group.Groups)
        {
            if (recycleBinUuid != Guid.Empty && sub.Uuid == recycleBinUuid) continue;
            Children.Add(new GroupItemViewModel(sub, recycleBinUuid));
        }
    }

    partial void OnIsExpandedChanged(bool value) => Model.IsExpanded = value;

    private static int CountEntries(KdbxGroup g)
        => g.Entries.Count + g.Groups.Sum(CountEntries);
}
