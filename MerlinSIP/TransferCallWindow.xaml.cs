using System.Windows;
using System.Windows.Input;

namespace MerlinSip;

public partial class TransferCallWindow : Window
{
    private string _transferTarget = "";
    public string TransferTarget => _transferTarget;
    public bool AssistedTransfer => AssistedTransferRadio.IsChecked == true;

    public TransferCallWindow(string currentTarget = "", System.Collections.Generic.IEnumerable<Models.ContactEntry>? favorites = null)
    {
        InitializeComponent();
        TransferTargetTextBox.Text = currentTarget;
        TransferTargetTextBox.CaretIndex = TransferTargetTextBox.Text.Length;
        Loaded += (_, _) => TransferTargetTextBox.Focus();

        var favList = favorites?.ToList() ?? [];
        if (favList.Count > 0)
        {
            FavoritesListView.ItemsSource = favList;
            FavoritesPanel.Visibility = Visibility.Visible;
            Height = 498;
        }
        else
        {
            FavoritesPanel.Visibility = Visibility.Collapsed;
            Height = 318;
        }
    }

    private void FavoritesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesListView.SelectedItem is Models.ContactEntry contact)
        {
            TransferTargetTextBox.Text = contact.Number;
            TransferButton_Click(this, new RoutedEventArgs());
        }
    }

    private void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        _transferTarget = TransferTargetTextBox.Text.Trim();
        DialogResult = !string.IsNullOrWhiteSpace(_transferTarget);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TransferTargetTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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
