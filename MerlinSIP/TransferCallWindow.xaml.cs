using System.Windows;
using System.Windows.Input;

namespace MerlinSip;

public partial class TransferCallWindow : Window
{
    public string TransferTarget => TransferTargetTextBox.Text.Trim();

    public TransferCallWindow(string currentTarget = "")
    {
        InitializeComponent();
        TransferTargetTextBox.Text = currentTarget;
        TransferTargetTextBox.CaretIndex = TransferTargetTextBox.Text.Length;
        Loaded += (_, _) => TransferTargetTextBox.Focus();
    }

    private void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = !string.IsNullOrWhiteSpace(TransferTarget);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TransferTargetTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TransferButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelButton_Click(sender, e);
        }
    }
}
