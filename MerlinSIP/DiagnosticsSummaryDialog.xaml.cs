using System;
using System.Windows;
using System.Windows.Media;
using MerlinSip.Services;

namespace MerlinSip
{
    public partial class DiagnosticsSummaryDialog : Window
    {
        private NetworkEngine? _engine;

        public DiagnosticsSummaryDialog()
        {
            InitializeComponent();
            Loaded += DiagnosticsSummaryDialog_Loaded;
        }

        private async void DiagnosticsSummaryDialog_Loaded(object sender, RoutedEventArgs e)
        {
            CloseBtn.IsEnabled = false;
            CurrentTestText.Text = "Running Environment Scan...";
            LogsText.Text = "";
            _engine = new NetworkEngine();
            
            _engine.OnLog += (msg, isError) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    LogsText.Text += msg + Environment.NewLine;
                    LogsScrollViewer.ScrollToEnd();
                });
            };
            
            _engine.OnProgress += (testName, status, details) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    CurrentTestText.Text = $"Running: {testName} ({status})";
                });
            };
            
            try
            {
                bool flag = await _engine.RunDiagnosticsAsync();
                CurrentTestText.Text = flag ? "Diagnostics Completed - PASS" : "Diagnostics Completed - FAIL/WARN";
                CurrentTestText.Foreground = flag ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.DarkOrange;
            }
            catch (Exception ex)
            {
                LogsText.Text += Environment.NewLine + "Error executing diagnostics: " + ex.Message + Environment.NewLine;
                CurrentTestText.Text = "Diagnostics Failed";
                CurrentTestText.Foreground = System.Windows.Media.Brushes.Red;
            }
            finally
            {
                CloseBtn.IsEnabled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
