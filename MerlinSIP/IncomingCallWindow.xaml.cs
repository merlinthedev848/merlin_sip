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
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        DeclineRequested?.Invoke(this, EventArgs.Empty);
    }
}
