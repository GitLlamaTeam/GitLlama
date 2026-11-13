using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using GitLlama.Desktop.ViewModels;
using GitLlama.Git;

namespace GitLlama.Desktop.Controls;

public sealed class PullOptionsButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public PullOptionsButton()
    {
        Content = "⌄";
        MinWidth = Width = 20;
        Padding = new Thickness(0);
        Classes.Add("workspace");
        AutomationProperties.SetName(this, "Pull options");
        ToolTip.SetTip(this, "Choose how to pull");
    }

    internal static MenuItem[] Items(MainWindowViewModel vm) => new[]
    {
        Choice(vm, GitPullMode.Merge, "Pull (fast-forward if possible)"),
        Choice(vm, GitPullMode.FastForwardOnly, "Pull (fast-forward only)"),
        Choice(vm, GitPullMode.Rebase, "Pull (rebase)")
    };

    private static MenuItem Choice(MainWindowViewModel vm, GitPullMode mode, string title) => new()
    {
        Header = title,
        ToggleType = MenuItemToggleType.Radio,
        IsChecked = vm.PullMode == mode,
        Command = vm.SelectPullModeCommand,
        CommandParameter = mode
    };

    protected override void OnClick()
    {
        base.OnClick();
        OpenOptions(this);
    }

    internal static bool OpenOptions(Control owner)
    {
        if (owner.DataContext is MainWindowViewModel vm)
        {
            owner.ContextMenu = new ContextMenu { ItemsSource = Items(vm) };
            owner.ContextMenu.Open(owner);
            return true;
        }
        return false;
    }
}
