using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RynthCore.LootEditor;

/// <summary>Simple Yes/No confirmation dialog.</summary>
public class ConfirmDialog : Window
{
    public ConfirmDialog(string message)
    {
        Title           = "Confirm";
        Width           = 360;
        Height          = 150;
        CanResize       = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background      = new SolidColorBrush(Color.Parse("#171F29"));

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 16 };

        panel.Children.Add(new TextBlock
        {
            Text         = message,
            Foreground   = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
        };

        var yes = new Button { Content = "Yes", Width = 72 };
        var no  = new Button { Content = "No",  Width = 72 };
        yes.Click += (_, _) => Close(true);
        no.Click  += (_, _) => Close(false);

        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);

        Content = panel;
    }
}
