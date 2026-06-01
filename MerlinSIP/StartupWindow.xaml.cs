using System.Windows;
using System.Windows.Controls;
using MerlinSip.Models;
using MerlinSip.Services;

namespace MerlinSip;

public partial class StartupWindow : Window
{
    private readonly LicenseService _licenseService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private readonly ProvisioningService _provisioningService = new();
    private string _licenseKey = string.Empty;
    private string _licenseStatus = "Licensed";
    private bool _licenseAccepted;

    public AppStartupConfig? Config { get; private set; }

    public StartupWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
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

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        if (!_licenseAccepted)
        {
            await AcceptLicenseStepAsync();
            return;
        }

        await RunWithDisabledContinueAsync(AcceptCredentialsStep);
    }

    private async Task RunWithDisabledContinueAsync(Func<Task> action)
    {
        ContinueButton.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            ContinueButton.IsEnabled = true;
        }
    }

    private async Task AcceptLicenseStepAsync()
    {
        var licenseKey = LicenseKeyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            ErrorText.Text = "Enter a valid license key.";
            return;
        }

        ContinueButton.IsEnabled = false;
        ContinueButton.Content = "Checking...";
        try
        {
            var activation = await _licenseService.ActivateAsync(licenseKey);
            if (!activation.Success)
            {
                ErrorText.Text = activation.Message;
                return;
            }

            _licenseAccepted = true;
            _licenseKey = licenseKey;
            _licenseStatus = _licenseService.Status;
            LicenseStepPanel.Visibility = Visibility.Collapsed;
            CredentialsStepPanel.Visibility = Visibility.Visible;
            SubtitleText.Text = "License accepted. Choose how to authenticate this device.";
        }
        finally
        {
            ContinueButton.Content = "Provision";
            ContinueButton.IsEnabled = true;
        }
    }

    private async Task AcceptCredentialsStep()
    {
        if (AudioInputComboBox.SelectedItem is not MediaDeviceInfo audioInput ||
            AudioOutputComboBox.SelectedItem is not MediaDeviceInfo audioOutput ||
            VideoSourceComboBox.SelectedItem is not MediaDeviceInfo videoSource)
        {
            ErrorText.Text = "Select audio input, audio output, and video source devices.";
            return;
        }

        if (ProvisionCodeRadioButton.IsChecked == true)
        {
            var provisioned = await _provisioningService.ProvisionAsync(
                ProvisioningCodeTextBox.Text,
                _licenseKey,
                _licenseStatus,
                _licenseService.LocalKey ?? string.Empty,
                audioInput,
                audioOutput,
                videoSource);

            if (!provisioned.Success || provisioned.Config is null)
            {
                ErrorText.Text = provisioned.Message;
                return;
            }

            Config = provisioned.Config;
            DialogResult = true;
            return;
        }

        var extension = ExtensionTextBox.Text.Trim();
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(extension))
        {
            ErrorText.Text = "Enter the user / extension.";
            return;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "Enter the login username and password.";
            return;
        }

        Config = new AppStartupConfig(
            AppStartupConfig.FixedSipServer,
            AppStartupConfig.FixedSipPort,
            AppStartupConfig.FixedSipServer,
            extension,
            username,
            password,
            _licenseKey,
            _licenseStatus,
            audioInput,
            audioOutput,
            videoSource,
            LicenseLocalKey: _licenseService.LocalKey ?? string.Empty).WithFixedSipEndpoint();

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void AuthenticationMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ProvisionCodePanel.Visibility = ProvisionCodeRadioButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ManualSipPanel.Visibility = ManualSipRadioButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
