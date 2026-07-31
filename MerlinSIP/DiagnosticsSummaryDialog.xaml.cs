using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MerlinSip.Services;

namespace MerlinSip
{
    public partial class DiagnosticsSummaryDialog : Window
    {
        public DiagnosticsSummaryDialog()
        {
            InitializeComponent();
            Loaded += DiagnosticsSummaryDialog_Loaded;
        }

        private async void DiagnosticsSummaryDialog_Loaded(object sender, RoutedEventArgs e)
        {
            CloseBtn.IsEnabled = false;
            CurrentTestText.Text = "Running environment scan...";
            var logBuilder = new StringBuilder();

            void AppendLog(string message)
            {
                logBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                LogsText.Text = logBuilder.ToString();
                LogsScrollViewer.ScrollToEnd();
            }

            try
            {
                AppendLog("Starting network diagnostics...");

                // 1. Network Interfaces Check
                CurrentTestText.Text = "Checking network interfaces...";
                AppendLog("Checking local network interfaces...");
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                int activeCount = 0;
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        activeCount++;
                        AppendLog($"  Interface: {ni.Name} ({ni.NetworkInterfaceType}) - UP");
                    }
                }
                AppendLog($"Found {activeCount} active network interface(s).");

                // 2. DNS Resolution Check
                CurrentTestText.Text = "Testing DNS resolution...";
                AppendLog("Testing DNS resolution for the configured service domain...");
                try
                {
                    var entry = await Dns.GetHostEntryAsync("chriskendall.media");
                    AppendLog($"  DNS Resolved successfully. Address count: {entry.AddressList.Length}");
                    foreach (var addr in entry.AddressList)
                    {
                        AppendLog($"    -> {addr}");
                    }
                }
                catch (Exception dnsEx)
                {
                    AppendLog($"  DNS Test warning: {dnsEx.Message}");
                }

                // 3. Debug Log Dump
                CurrentTestText.Text = "Collecting recent application log entries...";
                AppendLog("Recent application log entries:");
                var recentLogs = DebugLog.GetRecentLines(15);
                foreach (var line in recentLogs)
                {
                    AppendLog($"  {line}");
                }

                CurrentTestText.Text = "Diagnostics completed";
                CurrentTestText.Foreground = System.Windows.Media.Brushes.Green;
                AppendLog("All diagnostic checks finished successfully.");
            }
            catch (Exception ex)
            {
                AppendLog($"Error executing diagnostics: {ex.Message}");
                CurrentTestText.Text = "Diagnostics failed";
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
