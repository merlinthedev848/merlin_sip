using System.Windows;
using System.Windows.Controls;
using MerlinSip.Models;
using MerlinSip.Services;

namespace MerlinSip;

public partial class StartupWindow : Window
{
    private readonly LicenseService _licenseService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private string _licenseKey = string.Empty;
    private string _licenseStatus = "Trial mode";
    private bool _licenseAccepted;

    public AppStartupConfig? Config { get; private set; }

    public StartupWindow()
    {
        InitializeComponent();
        Loaded += StartupWindow_Loaded;
    }

    private void StartupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadMediaDevices();
    }

    private void LoadMediaDevices()
    {
        AudioInputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioInputs();
        AudioOutputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioOutputs();
        VideoSourceComboBox.ItemsSource = _deviceDiscoveryService.GetVideoSources();

        AudioInputComboBox.SelectedIndex = AudioInputComboBox.Items.Count > 0 ? 0 : -1;
        AudioOutputComboBox.SelectedIndex = AudioOutputComboBox.Items.Count > 0 ? 0 : -1;
        VideoSourceComboBox.SelectedIndex = VideoSourceComboBox.Items.Count > 0 ? 0 : -1;
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        if (!_licenseAccepted)
        {
            AcceptLicenseStep();
            return;
        }

        AcceptCredentialsStep();
    }

    private void AcceptLicenseStep()
    {
        var licenseKey = LicenseKeyTextBox.Text.Trim();

        if (!_licenseService.Activate(licenseKey))
        {
            ErrorText.Text = $"Enter a license key. For testing, use {LicenseService.PlaceholderKey}.";
            return;
        }

        _licenseAccepted = true;
        _licenseKey = licenseKey;
        _licenseStatus = _licenseService.Status;
        LicenseStepPanel.Visibility = Visibility.Collapsed;
        CredentialsStepPanel.Visibility = Visibility.Visible;
        SubtitleText.Text = "License accepted. Now enter your SIP account details and devices.";
        ContinueButton.Content = "Open Merlin SIP";
    }

    private void AcceptCredentialsStep()
    {
        var server = ServerTextBox.Text.Trim();
        var domain = DomainTextBox.Text.Trim();
        var extension = ExtensionTextBox.Text.Trim();
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordBox.Password;
        var portIsValid = int.TryParse(PortTextBox.Text.Trim(), out var port) && port is > 0 and <= 65535;

        if (string.IsNullOrWhiteSpace(server))
        {
            ErrorText.Text = "Enter the SIP server.";
            return;
        }

        if (!portIsValid)
        {
            ErrorText.Text = "Enter a valid SIP port.";
            return;
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            ErrorText.Text = "Enter the extension.";
            return;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "Enter the login username and password.";
            return;
        }

        if (AudioInputComboBox.SelectedItem is not MediaDeviceInfo audioInput ||
            AudioOutputComboBox.SelectedItem is not MediaDeviceInfo audioOutput ||
            VideoSourceComboBox.SelectedItem is not MediaDeviceInfo videoSource)
        {
            ErrorText.Text = "Select audio input, audio output, and video source devices.";
            return;
        }

        Config = new AppStartupConfig(
            server,
            port,
            domain,
            extension,
            username,
            password,
            _licenseKey,
            _licenseStatus,
            audioInput,
            audioOutput,
            videoSource);

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

}
