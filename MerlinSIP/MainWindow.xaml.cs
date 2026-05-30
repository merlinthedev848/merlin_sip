using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly UpdateService _updateService = new();
    private readonly ProvisioningService _provisioningService = new();
    private readonly ObservableCollection<ContactEntry> _contacts = [];
    private readonly ObservableCollection<CallHistoryEntry> _callHistory = [];
    private readonly DispatcherTimer _callTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppStartupConfig _config;
    private DateTimeOffset? _activeCallStartedAt;
    private DateTimeOffset? _activeCallConnectedAt;
    private string _activeCallDirection = "Outbound";
    private bool _dndEnabled;
    private bool _muted;
    private bool _held;
    private bool _registered;
    private bool _callInProgress;
    private bool _callConnected;
    private bool _incomingRinging;
    private ContactEntry? _editingContact;
    private IncomingCallWindow? _incomingCallWindow;

    public MainWindow(AppStartupConfig config)
    {
        _config = config;
        InitializeComponent();
        ApplyStartupConfig();
        ApplyAppVersion();
        LoadDefaultDeviceSelectors();
        DialContactsListView.ItemsSource = _contacts;
        PhonebookContactsListView.ItemsSource = _contacts;
        RecentCallsListView.ItemsSource = _callHistory;
        CallHistoryListView.ItemsSource = _callHistory;
        _sipRegistrationService.IncomingCall += SipRegistrationService_IncomingCall;
        _sipRegistrationService.CallProgress += SipRegistrationService_CallProgress;
        _sipRegistrationService.CallEnded += SipRegistrationService_CallEnded;
        _callTimer.Tick += CallTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        UpdateCallControls();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadContactsAsync();
        await LoadCallHistoryAsync();
        await Dispatcher.InvokeAsync(LoadDeviceSelectors, DispatcherPriority.Background);
        _ = RegisterSipAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        HideIncomingCallSurfaces();
        _ringtonePlayer.Dispose();
        _sipRegistrationService.Dispose();
    }

    private void CallTimer_Tick(object? sender, EventArgs e)
    {
        UpdateCallTimer();
    }

    private void StartCallTimer()
    {
        _activeCallConnectedAt = DateTimeOffset.Now;
        CallTimerPill.Visibility = Visibility.Visible;
        UpdateCallTimer();
        _callTimer.Start();
    }

    private void StopCallTimer()
    {
        _callTimer.Stop();
        _activeCallConnectedAt = null;
        CallTimerText.Text = "00:00";
        CallTimerPill.Visibility = Visibility.Collapsed;
    }

    private void UpdateCallTimer()
    {
        if (_activeCallConnectedAt is null)
        {
            CallTimerText.Text = "00:00";
            return;
        }

        var elapsed = DateTimeOffset.Now - _activeCallConnectedAt.Value;
        CallTimerText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void ShowIncomingCallWindow(string callerName, string callerNumber)
    {
        _incomingCallWindow?.Close();
        _incomingCallWindow = new IncomingCallWindow(callerName, callerNumber);
        _incomingCallWindow.AnswerRequested += IncomingCallWindow_AnswerRequested;
        _incomingCallWindow.DeclineRequested += IncomingCallWindow_DeclineRequested;
        _incomingCallWindow.Closed += (_, _) => _incomingCallWindow = null;
        _incomingCallWindow.Show();
    }

    private void HideIncomingCallSurfaces()
    {
        IncomingCallOverlay.Visibility = Visibility.Collapsed;
        if (_incomingCallWindow is null)
        {
            return;
        }

        var window = _incomingCallWindow;
        _incomingCallWindow = null;
        window.Close();
    }

    private void IncomingCallWindow_AnswerRequested(object? sender, EventArgs e)
    {
        AnswerIncomingCall();
    }

    private void IncomingCallWindow_DeclineRequested(object? sender, EventArgs e)
    {
        DeclineIncomingCall();
    }

    private void SipRegistrationService_IncomingCall(object? sender, IncomingCallEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var contact = _contactStore.FindByNumber(_contacts, e.CallerNumber);
            var callerName = contact?.Name ?? e.CallerNumber;
            DestinationTextBox.Text = e.CallerNumber;
            IncomingCallerNameText.Text = callerName;
            IncomingCallerNumberText.Text = e.CallerNumber;
            IncomingCallOverlay.Visibility = Visibility.Visible;
            CallerLookupText.Text = contact is null ? "Unknown caller" : $"{contact.Name}  {contact.Company}".Trim();
            NoticeText.Text = $"Incoming call from {callerName}.";
            FooterStatusText.Text = "Incoming call received.";
            _activeCallStartedAt = DateTimeOffset.Now;
            _activeCallDirection = "Inbound";
            _incomingRinging = true;
            _callInProgress = true;
            _callConnected = false;
            UpdateCallControls();
            _ringtonePlayer.Start(_config.AudioOutput, _config.Ringtone);
            ShowIncomingCallWindow(callerName, e.CallerNumber);
            _ = AddCallHistory("Inbound", callerName, e.CallerNumber, "Ringing", "Incoming call received.");
        });
    }

    private void SipRegistrationService_CallProgress(object? sender, CallProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Connected)
            {
                HideIncomingCallSurfaces();
                NoticeText.Text = "Call connected.";
                FooterStatusText.Text = "Call connected. Audio session is active.";
                _incomingRinging = false;
                _callInProgress = true;
                _callConnected = true;
                StartCallTimer();
                UpdateCallControls();
                return;
            }

            if (e.Code is 180 or 183)
            {
                NoticeText.Text = e.Message;
                FooterStatusText.Text = e.Message;
                return;
            }

            if (e.Code == 100)
            {
                NoticeText.Text = "Call setup in progress.";
                FooterStatusText.Text = "Call setup in progress.";
                return;
            }

            if (e.Code >= 300)
            {
                HideIncomingCallSurfaces();
                NoticeText.Text = e.Message;
                FooterStatusText.Text = e.Message;
                _incomingRinging = false;
                _callInProgress = false;
                _callConnected = false;
                UpdateCallControls();
            }
        });
    }

    private void SipRegistrationService_CallEnded(object? sender, CallEndedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            HideIncomingCallSurfaces();
            _ringtonePlayer.Stop();
            NoticeText.Text = "Call ended.";
            FooterStatusText.Text = e.Message;
            _activeCallStartedAt = null;
            _incomingRinging = false;
            _callInProgress = false;
            _callConnected = false;
            _muted = false;
            _held = false;
            StopCallTimer();
            ClearDialpadAfterCall();
            MuteButton.Content = "Mute";
            HoldButton.Content = "Hold";
            UpdateCallControls();
        });
    }

    private void ApplyStartupConfig()
    {
        _config = _config.WithFixedSipEndpoint();
        ExtensionTextBox.Text = _config.Extension;
        UsernameTextBox.Text = _config.Username;
        PasswordBox.Password = _config.Password;
        LicenseStatusText.Text = "Licensed";
    }

    private void LoadDeviceSelectors()
    {
        AudioInputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioInputs();
        AudioOutputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioOutputs();
        VideoSourceComboBox.ItemsSource = _deviceDiscoveryService.GetVideoSources();
        RingtoneComboBox.ItemsSource = RingtonePlayer.Choices;
        SelectDevice(AudioInputComboBox, _config.AudioInput);
        SelectDevice(AudioOutputComboBox, _config.AudioOutput);
        SelectDevice(VideoSourceComboBox, _config.VideoSource);
        SelectRingtone(_config.Ringtone);
    }

    private void LoadDefaultDeviceSelectors()
    {
        AudioInputComboBox.ItemsSource = new[] { _config.AudioInput };
        AudioOutputComboBox.ItemsSource = new[] { _config.AudioOutput };
        VideoSourceComboBox.ItemsSource = new[] { _config.VideoSource };
        RingtoneComboBox.ItemsSource = RingtonePlayer.Choices;
        AudioInputComboBox.SelectedIndex = 0;
        AudioOutputComboBox.SelectedIndex = 0;
        VideoSourceComboBox.SelectedIndex = 0;
        SelectRingtone(_config.Ringtone);
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

    private void SelectRingtone(string ringtone)
    {
        RingtoneComboBox.SelectedItem = RingtonePlayer.Choices.FirstOrDefault(choice => choice.Id == ringtone)
            ?? RingtonePlayer.Choices.First(choice => choice.Id == AppStartupConfig.DefaultRingtone);
    }

    private async Task RegisterSipAsync()
    {
        SetConnectionState("Connecting...", "#FFF1D6", "#8A4F08");
        ServerStatusText.Text = "Checking account connection.";
        await RefreshConnectionDiagnosticsAsync();
        SipRegistrationResult result;
        try
        {
            result = await _sipRegistrationService.RegisterAsync(_config);
        }
        catch (Exception error)
        {
            DebugLog.Write($"REGISTER unhandled error={error.Message}");
            result = new SipRegistrationResult(false, $"Unable to connect: {error.Message}");
        }

        if (result.Connected)
        {
            _registered = true;
            SetConnectionState("Connected", "#DFF8EE", "#106247");
            ServerStatusText.Text = ToCustomerConnectionMessage(result.Message);
            FooterStatusText.Text = "Ready.";
        }
        else
        {
            _registered = false;
            SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
            ServerStatusText.Text = ToCustomerConnectionMessage(result.Message);
            FooterStatusText.Text = "Connection status is available in Settings.";
        }

        await RefreshConnectionDiagnosticsAsync();
        UpdateCallControls();
    }

    private void SetConnectionState(string text, string background, string foreground)
    {
        ConnectionStatusText.Text = text;
        ConnectionPill.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        ConnectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;

        var mainText = text.Equals("Connected", StringComparison.OrdinalIgnoreCase)
            ? "Connected"
            : text.Equals("Connecting...", StringComparison.OrdinalIgnoreCase)
                ? "Checking"
                : "Not connected";
        MainConnectionStatusText.Text = mainText;
        MainConnectionPill.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        MainConnectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
    }

    private void ApplyAppVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionText.Text = version is null
            ? "Unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        UpdateStatusText.Text = $"Version {AppVersionText.Text}";
        ProductIdText.Text = LicenseService.ProductId;
    }

    private async Task RefreshConnectionDiagnosticsAsync()
    {
        PingStatusText.Text = "Checking...";
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(AppStartupConfig.FixedSipServer, 2500);
            PingStatusText.Text = reply.Status == IPStatus.Success
                ? $"{reply.RoundtripTime} ms"
                : reply.Status.ToString();
        }
        catch (Exception error)
        {
            DebugLog.Write($"PING failed error={error.Message}");
            PingStatusText.Text = "Unavailable";
        }
    }

    private static string ToCustomerConnectionMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Connection unavailable.";
        }

        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
        {
            return "Unable to reach the service.";
        }

        if (message.Contains("Timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection timed out.";
        }

        return message
            .Replace("SIP server returned 0 ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("SIP registration failed: ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("SIP server returned ", "", StringComparison.OrdinalIgnoreCase);
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
        UpdateCallControls();

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

    private static string NormalizeDialDestination(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "";
        }

        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var character in trimmed)
        {
            if (char.IsDigit(character) || character is '*' or '#')
            {
                builder.Append(character);
            }
        }

        return builder.Length > 0 ? builder.ToString() : trimmed;
    }

    private async void DialButton_Click(object sender, RoutedEventArgs e)
    {
        var destination = NormalizeDialDestination(DestinationTextBox.Text);
        if (string.IsNullOrWhiteSpace(destination))
        {
            NoticeText.Text = "Enter a number first.";
            return;
        }

        DestinationTextBox.Text = destination;

        var contact = _contactStore.FindByNumber(_contacts, destination);
        var name = contact?.Name ?? destination;
        _ringtonePlayer.Stop();
        NoticeText.Text = $"Calling {name}.";
        _activeCallStartedAt = DateTimeOffset.Now;
        _activeCallDirection = "Outbound";
        _callInProgress = true;
        _callConnected = false;
        UpdateCallControls();
        var result = await _sipRegistrationService.InviteAsync(destination);
        FooterStatusText.Text = result.Message;
        await AddCallHistory("Outbound", name, destination, result.Signalled ? "Signalled" : "Failed", result.Message);
        if (!result.Signalled)
        {
            NoticeText.Text = result.Message;
            _callInProgress = false;
            _callConnected = false;
            UpdateCallControls();
        }
    }

    private async void HangupButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await _sipRegistrationService.EndCallAsync();
        _ringtonePlayer.Stop();
        HideIncomingCallSurfaces();
        _muted = false;
        _held = false;
        _sipRegistrationService.SetMuted(false);
        _sipRegistrationService.SetHeld(false);
        MuteButton.Content = "Mute";
        HoldButton.Content = "Hold";
        _incomingRinging = false;
        _callInProgress = false;
        _callConnected = false;
        UpdateCallControls();
        NoticeText.Text = "Call ended.";
        FooterStatusText.Text = result.Message;
        if (_activeCallStartedAt is not null && !string.IsNullOrWhiteSpace(DestinationTextBox.Text))
        {
            var number = DestinationTextBox.Text.Trim();
            var contact = _contactStore.FindByNumber(_contacts, number);
            await AddCallHistory(_activeCallDirection, contact?.Name ?? number, number, result.Signalled ? "Ended" : "Cleared", result.Message, _activeCallStartedAt.Value);
            _activeCallStartedAt = null;
        }
        StopCallTimer();
        ClearDialpadAfterCall();
    }

    private void AnswerIncomingCallButton_Click(object sender, RoutedEventArgs e)
    {
        AnswerIncomingCall();
    }

    private void DeclineIncomingCallButton_Click(object sender, RoutedEventArgs e)
    {
        DeclineIncomingCall();
    }

    private async void AnswerIncomingCall()
    {
        _ringtonePlayer.Stop();
        HideIncomingCallSurfaces();
        MainTabs.SelectedItem = PhoneTab;
        WindowState = WindowState.Normal;
        Activate();

        var result = await _sipRegistrationService.AnswerIncomingCallAsync();
        FooterStatusText.Text = result.Message;
        NoticeText.Text = result.Signalled ? "Call answered." : result.Message;
        _incomingRinging = false;
        _callInProgress = result.Signalled;
        _callConnected = result.Signalled;
        if (result.Signalled)
        {
            StartCallTimer();
        }
        UpdateCallControls();
    }

    private async void DeclineIncomingCall()
    {
        var result = await _sipRegistrationService.EndCallAsync();
        _ringtonePlayer.Stop();
        HideIncomingCallSurfaces();
        FooterStatusText.Text = result.Message;
        NoticeText.Text = "Incoming call declined.";
        _incomingRinging = false;
        _callInProgress = false;
        _callConnected = false;
        StopCallTimer();
        ClearDialpadAfterCall();
        UpdateCallControls();
    }

    private void ClearDialpadAfterCall()
    {
        DestinationTextBox.Text = string.Empty;
        DestinationPreviewText.Text = "Enter number";
        CallerLookupText.Text = "No contact selected";
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
        if (sender is ListBox listBox && listBox.SelectedItem is ContactEntry contact)
        {
            CallContact(contact);
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_sipRegistrationService.CanControlAudio)
        {
            FooterStatusText.Text = "Connect a call before muting.";
            return;
        }

        _muted = !_muted;
        _sipRegistrationService.SetMuted(_muted);
        MuteButton.Content = _muted ? "Unmute" : "Mute";
        FooterStatusText.Text = _muted ? "Microphone muted." : "Microphone unmuted.";
    }

    private void HoldButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_sipRegistrationService.CanControlAudio)
        {
            FooterStatusText.Text = "Connect a call before using hold.";
            return;
        }

        _held = !_held;
        _sipRegistrationService.SetHeld(_held);
        HoldButton.Content = _held ? "Resume" : "Hold";
        FooterStatusText.Text = _held ? "Call audio paused." : "Call audio resumed.";
    }

    private async void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        var transferWindow = new TransferCallWindow(DestinationTextBox.Text)
        {
            Owner = this
        };

        if (transferWindow.ShowDialog() != true)
        {
            return;
        }

        var target = NormalizeDialDestination(transferWindow.TransferTarget);
        if (string.IsNullOrWhiteSpace(target))
        {
            FooterStatusText.Text = "Enter a number to transfer to.";
            return;
        }

        var result = await _sipRegistrationService.TransferAsync(target);
        FooterStatusText.Text = result.Message;
    }

    private async void DndButton_Click(object sender, RoutedEventArgs e)
    {
        _dndEnabled = !_dndEnabled;
        _sipRegistrationService.SetRejectIncomingCalls(_dndEnabled);
        if (_dndEnabled)
        {
            _ringtonePlayer.Stop();
            var rejectedCurrentCall = false;
            if (_sipRegistrationService.HasPendingIncomingCall)
            {
                var result = await _sipRegistrationService.EndCallAsync();
                HideIncomingCallSurfaces();
                FooterStatusText.Text = result.Message;
                rejectedCurrentCall = true;
                _incomingRinging = false;
                _callInProgress = false;
                _callConnected = false;
                UpdateCallControls();
            }

            if (rejectedCurrentCall)
            {
                DndButton.Content = "DND on";
                return;
            }
        }

        DndButton.Content = _dndEnabled ? "DND on" : "DND";
        UpdateCallControls();
        if (!_dndEnabled || !_sipRegistrationService.HasPendingIncomingCall)
        {
            FooterStatusText.Text = _dndEnabled ? "Do not disturb is on." : "Do not disturb is off.";
        }
    }

    private void UseSelectedContactButton_Click(object sender, RoutedEventArgs e)
    {
        var contact = MainTabs.SelectedItem == ContactsTab
            ? PhonebookContactsListView.SelectedItem as ContactEntry
            : DialContactsListView.SelectedItem as ContactEntry;

        if (contact is not null)
        {
            UseSelectedContact(contact);
        }
    }

    private void UseSelectedContact(ContactEntry contact)
    {
        DestinationTextBox.Text = contact.Number;
        MainTabs.SelectedItem = PhoneTab;
        NoticeText.Text = $"Ready to call {contact.Name}.";
    }

    private void CallContact(ContactEntry contact)
    {
        UseSelectedContact(contact);
        DialButton_Click(this, new RoutedEventArgs());
    }

    private void PhonebookContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhonebookContactsListView.SelectedItem is not ContactEntry contact)
        {
            return;
        }

        _editingContact = contact;
        ContactNameTextBox.Text = contact.Name;
        ContactNumberTextBox.Text = contact.Number;
        ContactCompanyTextBox.Text = contact.Company;
        ContactNotesTextBox.Text = contact.Notes;
        FooterStatusText.Text = $"Editing {contact.Name}.";
    }

    private void RecentCallsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecentCallsListView.SelectedItem is CallHistoryEntry call)
        {
            RedialCall(call);
        }
    }

    private void CallHistoryListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CallHistoryListView.SelectedItem is CallHistoryEntry call)
        {
            RedialCall(call);
        }
    }

    private void RedialCall(CallHistoryEntry call)
    {
        DestinationTextBox.Text = call.Number;
        MainTabs.SelectedItem = PhoneTab;
        NoticeText.Text = $"Calling {call.Name}.";
        DialButton_Click(this, new RoutedEventArgs());
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

        var existing = _editingContact
            ?? _contacts.FirstOrDefault(contact => contact.Number == number);
        if (existing is not null)
        {
            _contacts.Remove(existing);
        }

        var savedContact = new ContactEntry
        {
            Name = name,
            Number = number,
            Company = ContactCompanyTextBox.Text.Trim(),
            Notes = ContactNotesTextBox.Text.Trim()
        };

        _contacts.Add(savedContact);

        await _contactStore.SaveAsync(_contacts);
        PhonebookContactsListView.SelectedItem = savedContact;
        _editingContact = savedContact;
        FooterStatusText.Text = "Contact saved.";
    }

    private async void DeleteContactButton_Click(object sender, RoutedEventArgs e)
    {
        if (PhonebookContactsListView.SelectedItem is not ContactEntry contact)
        {
            FooterStatusText.Text = "Select a contact first.";
            return;
        }

        _contacts.Remove(contact);
        await _contactStore.SaveAsync(_contacts);
        _editingContact = null;
        ContactNameTextBox.Text = "";
        ContactNumberTextBox.Text = "";
        ContactCompanyTextBox.Text = "";
        ContactNotesTextBox.Text = "";
        FooterStatusText.Text = "Contact deleted.";
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        var audioInput = AudioInputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioInput;
        var audioOutput = AudioOutputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioOutput;
        var videoSource = VideoSourceComboBox.SelectedItem as MediaDeviceInfo ?? _config.VideoSource;
        var ringtone = RingtoneComboBox.SelectedItem as RingtoneChoice;
        _config = _config with
        {
            Server = AppStartupConfig.FixedSipServer,
            Port = AppStartupConfig.FixedSipPort,
            Domain = AppStartupConfig.FixedSipServer,
            Extension = ExtensionTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            AudioInput = audioInput,
            AudioOutput = audioOutput,
            VideoSource = videoSource,
            Ringtone = ringtone?.Id ?? _config.Ringtone
        };
        _config = _config.WithFixedSipEndpoint();

        await _cacheService.SaveSettingsAsync(_config);
        SettingsOverlay.Visibility = Visibility.Collapsed;
        _registered = false;
        UpdateCallControls();
        await RegisterSipAsync();
    }

    private async void ProvisionAndReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        ProvisionAndReconnectButton.IsEnabled = false;
        FooterStatusText.Text = "Provisioning account.";

        try
        {
            var audioInput = AudioInputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioInput;
            var audioOutput = AudioOutputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioOutput;
            var videoSource = VideoSourceComboBox.SelectedItem as MediaDeviceInfo ?? _config.VideoSource;
            var ringtone = RingtoneComboBox.SelectedItem as RingtoneChoice;

            var result = await _provisioningService.ProvisionAsync(
                SettingsProvisioningCodeTextBox.Text,
                _config.LicenseKey,
                _config.LicenseStatus,
                audioInput,
                audioOutput,
                videoSource);

            if (!result.Success || result.Config is null)
            {
                FooterStatusText.Text = result.Message;
                ServerStatusText.Text = result.Message;
                return;
            }

            _config = result.Config.WithFixedSipEndpoint() with
            {
                Ringtone = ringtone?.Id ?? _config.Ringtone
            };
            ApplyStartupConfig();
            await _cacheService.SaveSettingsAsync(_config);
            SettingsProvisioningCodeTextBox.Text = string.Empty;
            SettingsOverlay.Visibility = Visibility.Collapsed;
            FooterStatusText.Text = "Account provisioned.";
            _registered = false;
            UpdateCallControls();
            await RegisterSipAsync();
        }
        finally
        {
            ProvisionAndReconnectButton.IsEnabled = true;
        }
    }

    private void ShowDialButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = PhoneTab;
    }

    private void ShowContactsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = ContactsTab;
        PhonebookContactsListView.Focus();
    }

    private void ShowCallsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = CallsTab;
    }

    private async void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsTabs.SelectedItem = SettingsAccountTab;
        await RefreshConnectionDiagnosticsAsync();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void SettingsAccountButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsAccountTab;
    }

    private void SettingsDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsDevicesTab;
    }

    private async void SettingsStatusButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsStatusTab;
        await RefreshConnectionDiagnosticsAsync();
    }

    private void SettingsUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsUpdatesTab;
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _callHistory.Clear();
        await _callHistoryStore.SaveAsync(_callHistory);
        FooterStatusText.Text = "Call history cleared.";
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates...";

        var result = await _updateService.CheckForUpdatesAsync();
        UpdateStatusText.Text = result.Message;
        UpdateNotesText.Text = result.Notes ?? string.Empty;
        FooterStatusText.Text = result.Message;

        if (result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            var notes = string.IsNullOrWhiteSpace(result.Notes) ? "" : $"\n\n{result.Notes}";
            var install = MessageBox.Show(
                $"{result.Message}{notes}\n\nDownload and install now?",
                "Update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (install == MessageBoxResult.Yes)
            {
                try
                {
                    var progress = new Progress<int>(percent =>
                    {
                        UpdateStatusText.Text = $"Downloading update... {percent}%";
                    });
                    var installerPath = await _updateService.DownloadInstallerAsync(result, progress);
                    UpdateStatusText.Text = "Starting installer...";
                    FooterStatusText.Text = "Starting update installer. Merlin SIP will close.";
                    Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\"")
                    {
                        UseShellExecute = true
                    });
                    Application.Current.Shutdown();
                }
                catch (Exception error)
                {
                    DebugLog.Write($"UPDATE INSTALL failed error={error.Message}");
                    UpdateStatusText.Text = "Unable to download the update right now.";
                    FooterStatusText.Text = UpdateStatusText.Text;
                }
            }
        }

        CheckUpdatesButton.IsEnabled = true;
    }

    private void UpdateCallControls()
    {
        var hasDestination = !string.IsNullOrWhiteSpace(DestinationTextBox.Text);
        DialButton.IsEnabled = _registered && hasDestination && !_callInProgress && !_incomingRinging;
        HangupButton.IsEnabled = _callInProgress || _incomingRinging;
        MuteButton.IsEnabled = _callConnected && _sipRegistrationService.CanControlAudio;
        HoldButton.IsEnabled = _callConnected && _sipRegistrationService.CanControlAudio;
        TransferButton.IsEnabled = _callConnected;
        DndButton.IsEnabled = true;
    }

    private void SendErrorLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logText = File.Exists(DebugLog.Path)
                ? File.ReadAllText(DebugLog.Path)
                : "No error log has been created yet.";

            const int maxBodyLength = 12000;
            if (logText.Length > maxBodyLength)
            {
                logText = logText[^maxBodyLength..];
            }

            var subject = Uri.EscapeDataString("Merlin SIP error log");
            var body = Uri.EscapeDataString($"Please investigate this Merlin SIP error log.\r\n\r\n{logText}");
            Process.Start(new ProcessStartInfo($"mailto:sip-log@chriskendall.media?subject={subject}&body={body}")
            {
                UseShellExecute = true
            });

            FooterStatusText.Text = "Error log email opened.";
        }
        catch (Exception error)
        {
            FooterStatusText.Text = $"Unable to open error log email: {error.Message}";
        }
    }

    private async void ResetCacheButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Clear saved account settings, contacts, and call history? Merlin SIP will close so setup can run again next time.",
            "Reset app",
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
