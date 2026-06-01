using System.Windows;
using System.Windows.Input;

namespace MerlinSip;

public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow(string title, string message, string confirmText = "Confirm", string cancelText = "Cancel")
    {
        InitializeComponent();
        Title = title;
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    public static bool Confirm(Window owner, string title, string message, string confirmText = "Confirm", string cancelText = "Cancel")
    {
        var dialog = new ConfirmDialogWindow(title, message, confirmText, cancelText)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
