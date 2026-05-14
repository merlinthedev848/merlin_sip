using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MerlinSip.Models;
using MerlinSip.Services;

namespace MerlinSip;

public partial class MainWindow : Window
{
    private readonly ContactStore _contactStore = new();
    private readonly CallHistoryStore _callHistoryStore = new();
    private readonly AppCacheService _cacheService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private readonly SipRegistrationService _sipRegistrationService = new();
    private readonly RingtonePlayer _ringtonePlayer = new();
    private readonly ObservableCollection<ContactEntry> _contacts = [];
    private readonly ObservableCollection<CallHistoryEntry> _callHistory = [];
    private AppStartupConfig _config;
    private bool _dndEnabled;
    private DateTimeOffset? _activeCallStartedAt;

    public MainWindow(AppStartupConfig config)
    {
        _config = config;
        InitializeComponent();
        ApplyStartupConfig();
        LoadDeviceSelectors();
        ContactsListView.ItemsSource = _contacts;
        RecentCallsListView.ItemsSource = _callHistory;
        CallHistoryListView.ItemsSource = _callHistory;
        _sipRegistrationService.IncomingCall += SipRegistrationService_IncomingCall;
        _sipRegistrationService.CallProgress += SipRegistrationService_CallProgress;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadContactsAsync();
        await LoadCallHistoryAsync();
        await RegisterSipAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _ringtonePlayer.Dispose();
        _sipRegistrationService.Dispose();
    }

    private void SipRegistrationService_IncomingCall(object? sender, IncomingCallEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var contact = _contactStore.FindByNumber(_contacts, e.CallerNumber);
            var callerName = contact?.Name ?? e.CallerNumber;
            DestinationTextBox.Text = e.CallerNumber;
            CallerLookupText.Text = contact is null ? "Unknown caller" : $"{contact.Name}  {contact.Company}".Trim();
            CallStateText.Text = "Ringing";
            NoticeText.Text = $"Incoming call from {callerName}.";
            FooterStatusText.Text = "Incoming SIP INVITE received. Ringing response sent.";
            _ringtonePlayer.Start(_config.AudioOutput);
            _ = AddCallHistory("Inbound", callerName, e.CallerNumber, "Ringing", "Incoming SIP INVITE received.");
        });
    }

    private void SipRegistrationService_CallProgress(object? sender, CallProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Connected)
            {
                CallStateText.Text = "In call";
                NoticeText.Text = "Call connected.";
                FooterStatusText.Text = "Call connected. Audio session is active.";
                return;
            }

            if (e.Code is 180 or 183)
            {
                CallStateText.Text = "Ringing";
                NoticeText.Text = e.Message;
                FooterStatusText.Text = $"{e.Code} {e.Reason}".Trim();
                return;
            }

            if (e.Code == 100)
            {
                CallStateText.Text = "Trying";
                FooterStatusText.Text = "Call setup in progress.";
                return;
            }

            if (e.Code >= 300)
            {
                CallStateText.Text = "Ready";
                NoticeText.Text = e.Message;
                FooterStatusText.Text = e.Message;
            }
        });
    }

    private void ApplyStartupConfig()
    {
        ServerTextBox.Text = _config.Server;
        PortTextBox.Text = _config.Port.ToString();
        DomainTextBox.Text = _config.Domain;
        ExtensionTextBox.Text = _config.Extension;
        UsernameTextBox.Text = _config.Username;
        PasswordBox.Password = _config.Password;
        LicenseStatusText.Text = _config.LicenseStatus;
    }

    private void LoadDeviceSelectors()
    {
        AudioInputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioInputs();
        AudioOutputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioOutputs();
        VideoSourceComboBox.ItemsSource = _deviceDiscoveryService.GetVideoSources();
        SelectDevice(AudioInputComboBox, _config.AudioInput);
        SelectDevice(AudioOutputComboBox, _config.AudioOutput);
        SelectDevice(VideoSourceComboBox, _config.VideoSource);
    }

    private static void SelectDevice(ComboBox comboBox, MediaDeviceInfo selected)
    {
        foreach (var item in comboBox.Items.OfType<MediaDeviceInfo>())
        {
            if (item.Id == selected.Id || item.Name == selected.Name)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private async Task RegisterSipAsync()
    {
        SetConnectionState("Connecting...", "#FFF1D6", "#8A4F08");
        NoticeText.Text = "Registering your SIP account...";
        var result = await _sipRegistrationService.RegisterAsync(_config);

        if (result.Connected)
        {
            SetConnectionState("Connected", "#DFF8EE", "#106247");
            NoticeText.Text = "Ready to make and receive calls.";
        }
        else
        {
            SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
            NoticeText.Text = result.Message;
        }

        FooterStatusText.Text = result.Message;
    }

    private void SetConnectionState(string text, string background, string foreground)
    {
        ConnectionStatusText.Text = text;
        ConnectionPill.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        ConnectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
    }

    private async Task LoadContactsAsync()
    {
        _contacts.Clear();
        foreach (var contact in await _contactStore.LoadAsync())
        {
            _contacts.Add(contact);
        }
    }

    private async Task LoadCallHistoryAsync()
    {
        _callHistory.Clear();
        foreach (var call in await _callHistoryStore.LoadAsync())
        {
            _callHistory.Add(call);
        }
    }

    private void DestinationTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var destination = DestinationTextBox.Text.Trim();
        DestinationPreviewText.Text = string.IsNullOrWhiteSpace(destination) ? "Enter number" : destination;

        var contact = _contactStore.FindByNumber(_contacts, destination);
        CallerLookupText.Text = contact is null
            ? "No matching contact"
            : $"{contact.Name}  {contact.Company}".Trim();
    }

    private void DialpadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            DestinationTextBox.Text += button.Content?.ToString();
            DestinationTextBox.CaretIndex = DestinationTextBox.Text.Length;
        }
    }

    private async void DialButton_Click(object sender, RoutedEventArgs e)
    {
        var destination = DestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            NoticeText.Text = "Enter a number first.";
            return;
        }

        var contact = _contactStore.FindByNumber(_contacts, destination);
        var name = contact?.Name ?? destination;
        _ringtonePlayer.Stop();
        CallStateText.Text = "Calling";
        NoticeText.Text = $"Calling {name}.";
        _activeCallStartedAt = DateTimeOffset.Now;
        var result = await _sipRegistrationService.InviteAsync(destination);
        FooterStatusText.Text = result.Message;
        await AddCallHistory("Outbound", name, destination, result.Signalled ? "Signalled" : "Failed", result.Message);
        if (!result.Signalled)
        {
            CallStateText.Text = "Ready";
            NoticeText.Text = result.Message;
        }
    }

    private async void HangupButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await _sipRegistrationService.EndCallAsync();
        _ringtonePlayer.Stop();
        CallStateText.Text = "Ready";
        NoticeText.Text = "Call ended.";
        FooterStatusText.Text = result.Message;
        if (_activeCallStartedAt is not null && !string.IsNullOrWhiteSpace(DestinationTextBox.Text))
        {
            var number = DestinationTextBox.Text.Trim();
            var contact = _contactStore.FindByNumber(_contacts, number);
            await AddCallHistory("Outbound", contact?.Name ?? number, number, result.Signalled ? "Ended" : "Cleared", result.Message, _activeCallStartedAt.Value);
            _activeCallStartedAt = null;
        }
    }

    private void PlaceholderCallControl_Click(object sender, RoutedEventArgs e)
    {
        var action = sender is Button button ? button.Content?.ToString() : "Call control";
        NoticeText.Text = $"{action} requested.";
        FooterStatusText.Text = $"{action} will bind to the live SIP session.";
    }

    private void DndButton_Click(object sender, RoutedEventArgs e)
    {
        _dndEnabled = !_dndEnabled;
        if (_dndEnabled)
        {
            _ringtonePlayer.Stop();
        }
        CallStateText.Text = _dndEnabled ? "DND" : "Ready";
        NoticeText.Text = _dndEnabled ? "Do not disturb enabled." : "Do not disturb disabled.";
        FooterStatusText.Text = NoticeText.Text;
    }

    private void ClearDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        DestinationTextBox.Text = string.Empty;
        DestinationTextBox.Focus();
    }

    private void DestinationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialButton_Click(sender, e);
        }
    }

    private void ContactsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        UseSelectedContact();
    }

    private void UseSelectedContactButton_Click(object sender, RoutedEventArgs e)
    {
        UseSelectedContact();
    }

    private void UseSelectedContact()
    {
        if (ContactsListView.SelectedItem is ContactEntry contact)
        {
            DestinationTextBox.Text = contact.Number;
            NoticeText.Text = $"Ready to call {contact.Name}.";
        }
    }

    private void RecentCallsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecentCallsListView.SelectedItem is CallHistoryEntry call)
        {
            DestinationTextBox.Text = call.Number;
            NoticeText.Text = $"Ready to redial {call.Name}.";
        }
    }

    private void CallHistoryListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CallHistoryListView.SelectedItem is CallHistoryEntry call)
        {
            DestinationTextBox.Text = call.Number;
            MainTabs.SelectedIndex = 0;
            NoticeText.Text = $"Ready to redial {call.Name}.";
        }
    }

    private async void SaveContactButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ContactNameTextBox.Text.Trim();
        var number = ContactNumberTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(number))
        {
            FooterStatusText.Text = "Name and number are required.";
            return;
        }

        var existing = _contacts.FirstOrDefault(contact => contact.Number == number);
        if (existing is not null)
        {
            _contacts.Remove(existing);
        }

        _contacts.Add(new ContactEntry
        {
            Name = name,
            Number = number,
            Company = ContactCompanyTextBox.Text.Trim(),
            Notes = ContactNotesTextBox.Text.Trim()
        });

        await _contactStore.SaveAsync(_contacts);
        ContactNameTextBox.Text = "";
        ContactNumberTextBox.Text = "";
        ContactCompanyTextBox.Text = "";
        ContactNotesTextBox.Text = "";
        FooterStatusText.Text = "Contact saved.";
    }

    private async void DeleteContactButton_Click(object sender, RoutedEventArgs e)
    {
        if (ContactsListView.SelectedItem is not ContactEntry contact)
        {
            FooterStatusText.Text = "Select a contact first.";
            return;
        }

        _contacts.Remove(contact);
        await _contactStore.SaveAsync(_contacts);
        FooterStatusText.Text = "Contact deleted.";
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        var port = int.TryParse(PortTextBox.Text.Trim(), out var parsedPort) ? parsedPort : 5060;
        var audioInput = AudioInputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioInput;
        var audioOutput = AudioOutputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioOutput;
        var videoSource = VideoSourceComboBox.SelectedItem as MediaDeviceInfo ?? _config.VideoSource;
        _config = _config with
        {
            Server = ServerTextBox.Text.Trim(),
            Port = port,
            Domain = DomainTextBox.Text.Trim(),
            Extension = ExtensionTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            AudioInput = audioInput,
            AudioOutput = audioOutput,
            VideoSource = videoSource
        };

        await _cacheService.SaveSettingsAsync(_config);
        SettingsOverlay.Visibility = Visibility.Collapsed;
        await RegisterSipAsync();
    }

    private void ShowDialButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 0;
    }

    private void ShowContactsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 0;
        ContactsListView.Focus();
    }

    private void ShowCallsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _callHistory.Clear();
        await _callHistoryStore.SaveAsync(_callHistory);
        FooterStatusText.Text = "Call history cleared.";
    }

    private async void ResetCacheButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Clear saved SIP settings, contacts, and call history? Merlin SIP will close so setup can run again next time.",
            "Cache reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _cacheService.Reset();
        _contacts.Clear();
        _callHistory.Clear();
        await Task.Delay(50);
        Application.Current.Shutdown();
    }

    private async Task AddCallHistory(string direction, string name, string number, string result, string detail, DateTimeOffset? startedAt = null)
    {
        var start = startedAt ?? DateTimeOffset.Now;
        var end = DateTimeOffset.Now;
        var duration = end > start ? (end - start).ToString(@"mm\:ss") : "";
        var entry = new CallHistoryEntry
        {
            Direction = direction,
            Name = name,
            Number = number,
            StartedAt = start.ToString("yyyy-MM-dd HH:mm:ss"),
            EndedAt = end.ToString("yyyy-MM-dd HH:mm:ss"),
            Duration = duration,
            Result = result,
            Detail = detail
        };

        _callHistory.Insert(0, entry);
        await _callHistoryStore.SaveAsync(_callHistory);
    }
}
