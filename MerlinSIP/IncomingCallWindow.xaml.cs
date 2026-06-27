using System.Windows;

namespace MerlinSip;

public partial class IncomingCallWindow : Window
{
    public event EventHandler? AnswerRequested;
    public event EventHandler? DeclineRequested;

    public IncomingCallWindow(string callerName, string callerNumber)
    {
        InitializeComponent();
        CallerNameText.Text = string.IsNullOrWhiteSpace(callerName) ? "Unknown caller" : callerName;
        CallerNumberText.Text = callerNumber;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 22;
        Top = workArea.Bottom - ActualHeight - 22;
        Activate();
    }

    private void AnswerButton_Click(object sender, RoutedEventArgs e)
    {
        AnswerRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        DeclineRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            AnswerButton_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            DeclineButton_Click(this, new RoutedEventArgs());
        }
    }
}
