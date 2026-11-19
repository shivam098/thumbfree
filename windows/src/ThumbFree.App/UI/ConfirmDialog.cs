using System.Windows;
using System.Windows.Controls;

namespace ThumbFree.App.UI;

/// <summary>A small question window in ThumbFree's look, with clear buttons ("Delete" / "Cancel") instead of Yes / No.</summary>
internal sealed class ConfirmDialog : Window
{
    private ConfirmDialog(Confirmation question)
    {
        Title = "ThumbFree";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Theme.Card;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        Icon = Theme.AppIcon();
        Content = View(question, answer => DialogResult = answer);
    }

    /// <summary>Shows the question over <paramref name="owner"/> and returns true when the person confirms.</summary>
    public static bool Ask(Window? owner, Confirmation question)
    {
        var dialog = new ConfirmDialog(question);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>The dialog's content; offscreen renders draw it without a window.</summary>
    internal static FrameworkElement View(Confirmation question, Action<bool> answer)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Width = 440 };
        panel.Children.Add(Theme.Title(question.Title, 18));
        var message = Theme.Label(question.Message, 13);
        message.Margin = new Thickness(0, 4, 0, 20);
        message.LineHeight = 19;
        panel.Children.Add(message);

        var confirm = Theme.Button(question.Confirm, (_, _) => answer(true), primary: true, danger: question.Danger);
        var cancel = Theme.Button(question.Cancel, (_, _) => answer(false));
        cancel.Margin = new Thickness(0);
        cancel.IsCancel = true;

        // Enter confirms only what can't do harm; a deletion needs a click.
        confirm.IsDefault = !question.Danger;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        return new Border { Background = Theme.Card, Child = panel };
    }
}
