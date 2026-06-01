using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MerlinSip.Models;
using MerlinSip.Services;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace MerlinSip;

public partial class MainWindow : Window
{
    private readonly ContactStore _contactStore = new();
    private readonly CallHistoryStore _callHistoryStore = new();
    private readonly ChatMessageStore _chatMessageStore = new();
    private readonly AppCacheService _cacheService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private readonly SipRegistrationService _sipRegistrationService = new();
    private readonly RingtonePlayer _ringtonePlayer = new();
    private readonly UpdateService _updateService = new();
    private readonly ProvisioningService _provisioningService = new();
    private WinForms.NotifyIcon? _trayIcon;
    private readonly ObservableCollection<ContactEntry> _contacts = [];
    private readonly ObservableCollection<CallHistoryEntry> _callHistory = [];
    private readonly ObservableCollection<ChatMessageEntry> _chatMessages = [];
    private readonly ObservableCollection<ChatMessageEntry> _chatThreadMessages = [];
    private readonly DispatcherTimer _callTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _connectionWatchdog = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _connectionCheckInProgress;
    private bool _startupUpdateCheckCompleted;
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
    private bool _allowExit;
    private string _selectedChatNumber = "";
    private string _activeRemoteNumber = "";
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
        ChatContactsListView.ItemsSource = _contacts;
        RecentCallsListView.ItemsSource = _callHistory;
        CallHistoryListView.ItemsSource = _callHistory;
        ChatMessagesListView.ItemsSource = _chatThreadMessages;
        _sipRegistrationService.IncomingCall += SipRegistrationService_IncomingCall;
        _sipRegistrationService.IncomingMessage += SipRegistrationService_IncomingMessage;
        _sipRegistrationService.CallProgress += SipRegistrationService_CallProgress;
        _sipRegistrationService.CallEnded += SipRegistrationService_CallEnded;
        _sipRegistrationService.ContactPresenceChanged += SipRegistrationService_ContactPresenceChanged;
        _callTimer.Tick += CallTimer_Tick;
        _connectionWatchdog.Tick += ConnectionWatchdog_Tick;
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        InitializeTrayIcon();
        UpdateCallControls();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadContactsAsync();
        await LoadCallHistoryAsync();
        await LoadChatMessagesAsync();
        await Dispatcher.InvokeAsync(LoadDeviceSelectors, DispatcherPriority.Background);
        _ = RegisterSipAsync();
        _connectionWatchdog.Start();
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        HideIncomingCallSurfaces();
        _connectionWatchdog.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _ringtonePlayer.Dispose();
        _sipRegistrationService.Dispose();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        FooterStatusText.Text = "Merlin SIP is running in the notification area.";
        _trayIcon?.ShowBalloonTip(1800, "Merlin SIP", "Still running for calls and messages.", WinForms.ToolTipIcon.Info);
    }

    private void InitializeTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CKMedia-Icon.ico");
        var icon = File.Exists(iconPath) ? new DrawingIcon(iconPath) : DrawingSystemIcons.Application;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Merlin SIP", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("Exit Merlin SIP", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "Merlin SIP",
            Icon = icon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private async void ConnectionWatchdog_Tick(object? sender, EventArgs e)
    {
        await EnsureConnectionReadyAsync();
    }

    private async Task EnsureConnectionReadyAsync()
    {
        if (_connectionCheckInProgress || _callInProgress || _incomingRinging)
        {
            return;
        }

        _connectionCheckInProgress = true;
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                _registered = false;
                SetConnectionState("No network", "#FFE2E2", "#9B1C1C");
                FooterStatusText.Text = "No network connectivity.";
                UpdateCallControls();
                return;
            }

            if (!_registered)
            {
                await RegisterSipAsync();
                return;
            }

            var result = await _sipRegistrationService.RefreshRegistrationAsync();
            if (!result.Connected)
            {
                _registered = false;
                SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
                await RegisterSipAsync();
                return;
            }

            _registered = true;
            SetConnectionState("Connected", "#DFF8EE", "#106247");
        }
        finally
        {
            _connectionCheckInProgress = false;
        }
    }

    private void CallTimer_Tick(object? sender, EventArgs e)
    {
        UpdateCallTimer();
    }

    private void StartCallTimer()
    {
        _activeCallConnectedAt = DateTimeOffset.Now;
        CallTimerPill.Visibility = Visibility.Visible;
        CallTimerPill.Background = (WpfBrush)new BrushConverter().ConvertFromString("#DFF8EE")!;
        CallTimerText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#106247")!;
        UpdateCallTimer();
        _callTimer.Start();
    }

    private void StopCallTimer()
    {
        _callTimer.Stop();
        _activeCallConnectedAt = null;
        CallTimerText.Text = "Inactive";
        CallTimerPill.Visibility = Visibility.Visible;
        CallTimerPill.Background = (WpfBrush)new BrushConverter().ConvertFromString("#FFE2E2")!;
        CallTimerText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#9B1C1C")!;
    }

    private void UpdateCallTimer()
    {
        if (_activeCallConnectedAt is null)
        {
            CallTimerText.Text = "Inactive";
            return;
        }

        var elapsed = DateTimeOffset.Now - _activeCallConnectedAt.Value;
        var time = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
        CallTimerText.Text = $"Active {time}";
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

    private bool ShouldUseDesktopIncomingPopup()
    {
        return !IsVisible || WindowState == WindowState.Minimized || !IsActive;
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
            var useDesktopPopup = ShouldUseDesktopIncomingPopup();
            _activeRemoteNumber = e.CallerNumber;
            SetContactPresence(e.CallerNumber, "Ringing");
            DestinationTextBox.Text = e.CallerNumber;
            IncomingCallerNameText.Text = callerName;
            IncomingCallerNumberText.Text = e.CallerNumber;
            IncomingCallOverlay.Visibility = useDesktopPopup ? Visibility.Collapsed : Visibility.Visible;
            CallerLookupText.Text = contact is null ? "Unknown caller" : $"{contact.Name}  {contact.Company}".Trim();
            NoticeText.Text = $"Incoming call from {callerName}.";
            FooterStatusText.Text = "Incoming call received.";
            _activeCallStartedAt = DateTimeOffset.Now;
            _activeCallDirection = "Inbound";
            _incomingRinging = true;
            _callInProgress = true;
            _callConnected = false;
            UpdateCallControls();
            _ringtonePlayer.Start(_config.AudioOutput, _config.Ringtone, _config.HeadphoneVolume);
            if (useDesktopPopup)
            {
                ShowIncomingCallWindow(callerName, e.CallerNumber);
            }

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
                SetContactPresence(_activeRemoteNumber, "Busy");
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
                SetContactPresence(_activeRemoteNumber, "Ringing");
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
                SetContactPresence(_activeRemoteNumber, "Available");
                _activeRemoteNumber = "";
                UpdateCallControls();
            }
        });
    }

    private void SipRegistrationService_ContactPresenceChanged(object? sender, ContactPresenceEventArgs e)
    {
        Dispatcher.Invoke(() => SetContactPresence(e.Number, e.Presence));
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
            SetContactPresence(_activeRemoteNumber, "Available");
            _activeRemoteNumber = "";
            StopCallTimer();
            ClearDialpadAfterCall();
            MuteButton.Content = "Mute";
            HoldButton.Content = "Hold";
            UpdateCallControls();
        });
    }

    private void SipRegistrationService_IncomingMessage(object? sender, IncomingMessageEventArgs e)
    {
        Dispatcher.Invoke(async () =>
        {
            var contact = _contactStore.FindByNumber(_contacts, e.SenderNumber);
            var senderName = contact?.Name ?? e.SenderNumber;
            await AddChatMessage("Inbound", senderName, e.SenderNumber, e.Message, "Received");
            FooterStatusText.Text = $"Message received from {senderName}.";
            if (!MessageBelongsToThread(new ChatMessageEntry { Number = e.SenderNumber }, _selectedChatNumber))
            {
                return;
            }

            RefreshChatThread();
        });
    }

    private void ApplyStartupConfig()
    {
        _config = _config.WithFixedSipEndpoint();
        ExtensionTextBox.Text = _config.Extension;
        UsernameTextBox.Text = _config.Username;
        PasswordBox.Password = _config.Password;
        SipAlgCompatibilityCheckBox.IsChecked = _config.SipAlgCompatibilityMode;
        LicenseStatusText.Text = "Licensed";
        UpdateNetworkAssistanceText();
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
        LoadVolumeSliders();
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
        LoadVolumeSliders();
    }

    private static void SelectDevice(System.Windows.Controls.ComboBox comboBox, MediaDeviceInfo selected)
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

    private void LoadVolumeSliders()
    {
        HeadphoneVolumeSlider.Value = _config.HeadphoneVolume * 100;
        MicrophoneVolumeSlider.Value = _config.MicrophoneVolume * 100;
        UpdateVolumeText();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateVolumeText();
    }

    private void UpdateVolumeText()
    {
        if (!IsLoaded)
        {
            return;
        }

        HeadphoneVolumeText.Text = $"{HeadphoneVolumeSlider.Value:0}%";
        MicrophoneVolumeText.Text = $"{MicrophoneVolumeSlider.Value:0}%";
    }

    private async Task RegisterSipAsync()
    {
        SetConnectionState("Connecting...", "#FFF1D6", "#8A4F08");
        ServerStatusText.Text = "Checking account connection.";
        await RefreshConnectionDiagnosticsAsync();
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _registered = false;
            SetConnectionState("No network", "#FFE2E2", "#9B1C1C");
            ServerStatusText.Text = "No network connectivity detected.";
            FooterStatusText.Text = "No network connectivity.";
            UpdateCallControls();
            return;
        }

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
            await RefreshPresenceSubscriptionsAsync();
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
        ConnectionPill.Background = (WpfBrush)new BrushConverter().ConvertFromString(background)!;
        ConnectionStatusText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(foreground)!;

        var mainText = text.Equals("Connected", StringComparison.OrdinalIgnoreCase)
            ? "Connected"
            : text.Equals("Connecting...", StringComparison.OrdinalIgnoreCase)
                ? "Checking"
                : "Not connected";
        MainConnectionStatusText.Text = mainText;
        MainConnectionPill.Background = (WpfBrush)new BrushConverter().ConvertFromString(background)!;
        MainConnectionStatusText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(foreground)!;
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
            if (reply.Status == IPStatus.Success)
            {
                PingStatusText.Text = $"{reply.RoundtripTime} ms";
                return;
            }

            PingStatusText.Text = NetworkInterface.GetIsNetworkAvailable()
                ? reply.Status.ToString()
                : "No network connectivity";
        }
        catch (Exception error)
        {
            DebugLog.Write($"PING failed error={error.Message}");
            PingStatusText.Text = NetworkInterface.GetIsNetworkAvailable()
                ? "Unavailable"
                : "No network connectivity";
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
            return NetworkInterface.GetIsNetworkAvailable()
                ? "Unable to reach the service."
                : "No network connectivity detected.";
        }

        if (message.Contains("Timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection timed out.";
        }

        if (message.Contains("Call in progress", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Connection check in progress", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection is being maintained.";
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

        _ = RefreshPresenceSubscriptionsAsync();
    }

    private async Task RefreshPresenceSubscriptionsAsync()
    {
        if (!_registered)
        {
            return;
        }

        await _sipRegistrationService.SubscribeToContactPresenceAsync(_contacts.Select(contact => contact.Number));
    }

    private void SetContactPresence(string number, string presence)
    {
        if (string.IsNullOrWhiteSpace(number))
        {
            return;
        }

        var normalized = NormalizeDialDestination(number);
        for (var index = 0; index < _contacts.Count; index++)
        {
            if (!string.Equals(NormalizeDialDestination(_contacts[index].Number), normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _contacts[index] = _contacts[index] with { Presence = presence };
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
        if (sender is WpfButton button)
        {
            DestinationTextBox.Text += button.Content?.ToString();
            DestinationTextBox.CaretIndex = DestinationTextBox.Text.Length;
        }
    }

    private async Task LoadChatMessagesAsync()
    {
        _chatMessages.Clear();
        foreach (var message in await _chatMessageStore.LoadAsync())
        {
            _chatMessages.Add(message);
        }

        RefreshChatThread();
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
        DebugLog.Write($"UI dial requested destination={destination}");

        var contact = _contactStore.FindByNumber(_contacts, destination);
        var name = contact?.Name ?? destination;
        _ringtonePlayer.Stop();
        NoticeText.Text = $"Calling {name}.";
        _activeCallStartedAt = DateTimeOffset.Now;
        _activeCallDirection = "Outbound";
        _activeRemoteNumber = destination;
        SetContactPresence(destination, "Ringing");
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
            SetContactPresence(destination, "Available");
            _activeRemoteNumber = "";
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
        _sipRegistrationService.SetHeldLocal(false);
        MuteButton.Content = "Mute";
        HoldButton.Content = "Hold";
        _incomingRinging = false;
        _callInProgress = false;
        _callConnected = false;
        SetContactPresence(_activeRemoteNumber, "Available");
        _activeRemoteNumber = "";
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
            SetContactPresence(_activeRemoteNumber, "Busy");
            StartCallTimer();
        }
        else
        {
            SetContactPresence(_activeRemoteNumber, "Available");
            _activeRemoteNumber = "";
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
        SetContactPresence(_activeRemoteNumber, "Available");
        _activeRemoteNumber = "";
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

    private void DestinationTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialButton_Click(sender, e);
        }
    }

    private void ContactsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is WpfListBox listBox && listBox.SelectedItem is ContactEntry contact)
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

    private async void HoldButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_sipRegistrationService.CanControlAudio)
        {
            FooterStatusText.Text = "Connect a call before using hold.";
            return;
        }

        var nextHeld = !_held;
        HoldButton.IsEnabled = false;
        var result = await _sipRegistrationService.SetHeldAsync(nextHeld);
        HoldButton.IsEnabled = true;
        if (!result.Signalled)
        {
            FooterStatusText.Text = result.Message;
            return;
        }

        _held = nextHeld;
        HoldButton.Content = _held ? "Resume" : "Hold";
        FooterStatusText.Text = _held
            ? "Call is on hold. PBX hold music will be used if enabled."
            : "Call resumed.";
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
        if (result.Signalled)
        {
            _muted = false;
            _held = false;
            MuteButton.Content = "Mute";
            HoldButton.Content = "Hold";
            _callInProgress = false;
            _callConnected = false;
            StopCallTimer();
            ClearDialpadAfterCall();
            UpdateCallControls();
        }
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

        if (!_dndEnabled && !_registered && NetworkInterface.GetIsNetworkAvailable())
        {
            FooterStatusText.Text = "Reconnecting after do not disturb.";
            await RegisterSipAsync();
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
        _ = RefreshPresenceSubscriptionsAsync();
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
        var mediaConfig = BuildConfigFromSettings();
        _config = _config with
        {
            Server = AppStartupConfig.FixedSipServer,
            Port = AppStartupConfig.FixedSipPort,
            Domain = AppStartupConfig.FixedSipServer,
            Extension = ExtensionTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            AudioInput = mediaConfig.AudioInput,
            AudioOutput = mediaConfig.AudioOutput,
            VideoSource = mediaConfig.VideoSource,
            Ringtone = mediaConfig.Ringtone,
            MicrophoneVolume = mediaConfig.MicrophoneVolume,
            HeadphoneVolume = mediaConfig.HeadphoneVolume,
            SipAlgCompatibilityMode = mediaConfig.SipAlgCompatibilityMode
        };
        _config = _config.WithFixedSipEndpoint();

        await _cacheService.SaveSettingsAsync(_config);
        SettingsOverlay.Visibility = Visibility.Collapsed;
        _registered = false;
        UpdateCallControls();
        await RegisterSipAsync();
    }

    private AppStartupConfig BuildConfigFromSettings()
    {
        var audioInput = AudioInputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioInput;
        var audioOutput = AudioOutputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioOutput;
        var videoSource = VideoSourceComboBox.SelectedItem as MediaDeviceInfo ?? _config.VideoSource;
        var ringtone = RingtoneComboBox.SelectedItem as RingtoneChoice;
        return _config with
        {
            AudioInput = audioInput,
            AudioOutput = audioOutput,
            VideoSource = videoSource,
            Ringtone = ringtone?.Id ?? _config.Ringtone,
            MicrophoneVolume = Math.Clamp(MicrophoneVolumeSlider.Value / 100, 0.25, 2.0),
            HeadphoneVolume = Math.Clamp(HeadphoneVolumeSlider.Value / 100, 0.25, 2.0),
            SipAlgCompatibilityMode = SipAlgCompatibilityCheckBox.IsChecked == true
        };
    }

    private void TestToneButton_Click(object sender, RoutedEventArgs e)
    {
        var output = AudioOutputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioOutput;
        var ringtone = RingtoneComboBox.SelectedItem as RingtoneChoice;
        _ringtonePlayer.Start(output, ringtone?.Id ?? _config.Ringtone, HeadphoneVolumeSlider.Value / 100);
        _ = Task.Run(async () =>
        {
            await Task.Delay(2200);
            await Dispatcher.InvokeAsync(() => _ringtonePlayer.Stop());
        });
    }

    private async void SaveDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        var previousInput = _config.AudioInput;
        var previousOutput = _config.AudioOutput;
        var previousVideo = _config.VideoSource;
        _config = BuildConfigFromSettings().WithFixedSipEndpoint();
        await _cacheService.SaveSettingsAsync(_config);

        var mediaDeviceChanged =
            previousInput.Id != _config.AudioInput.Id ||
            previousOutput.Id != _config.AudioOutput.Id ||
            previousVideo.Id != _config.VideoSource.Id;

        SettingsOverlay.Visibility = Visibility.Collapsed;
        FooterStatusText.Text = mediaDeviceChanged
            ? "Device settings saved. Current registration stays active."
            : "Ringtone saved.";
        UpdateCallControls();
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
                Ringtone = ringtone?.Id ?? _config.Ringtone,
                MicrophoneVolume = Math.Clamp(MicrophoneVolumeSlider.Value / 100, 0.25, 2.0),
                HeadphoneVolume = Math.Clamp(HeadphoneVolumeSlider.Value / 100, 0.25, 2.0),
                SipAlgCompatibilityMode = SipAlgCompatibilityCheckBox.IsChecked == true
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

    private void ShowDirectoryHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        DirectoryTabs.SelectedItem = DirectoryHistoryTab;
    }

    private void ShowDirectoryContactsButton_Click(object sender, RoutedEventArgs e)
    {
        DirectoryTabs.SelectedItem = DirectoryContactsTab;
    }

    private void ShowCallsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = CallsTab;
    }

    private void ShowMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = MessagesTab;
        if (string.IsNullOrWhiteSpace(_selectedChatNumber) && _contacts.Count > 0)
        {
            SelectChatContact(_contacts[0]);
        }
    }

    private async void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsTabs.SelectedItem = SettingsAccountTab;
        await RefreshConnectionDiagnosticsAsync();
        await EnsureConnectionReadyAsync();
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
        await EnsureConnectionReadyAsync();
    }

    private async void SaveNetworkModeButton_Click(object sender, RoutedEventArgs e)
    {
        _config = _config with
        {
            SipAlgCompatibilityMode = SipAlgCompatibilityCheckBox.IsChecked == true
        };

        await _cacheService.SaveSettingsAsync(_config.WithFixedSipEndpoint());
        _sipRegistrationService.UpdateNetworkAssistance(_config.SipAlgCompatibilityMode);
        UpdateNetworkAssistanceText();
        FooterStatusText.Text = _config.SipAlgCompatibilityMode
            ? "Router keepalive assist is on."
            : "Standard network mode is on.";
    }

    private void SipAlgCompatibilityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateNetworkAssistanceText();
    }

    private void UpdateNetworkAssistanceText()
    {
        var compatibilityOn = SipAlgCompatibilityCheckBox.IsChecked == true;
        NatKeepaliveStatusText.Text = compatibilityOn ? "On" : "Off";
        NatKeepaliveStatusText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(compatibilityOn ? "#106247" : "#64748B")!;
        RportStatusText.Text = "On";
        AutoRecoveryStatusText.Text = "On";
    }

    private async void RunPbxDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        RunPbxDiagnosticsButton.IsEnabled = false;
        PbxDiagnosticsText.Text = "Running PBX compatibility checks...";
        FooterStatusText.Text = "Running PBX compatibility checks.";

        try
        {
            PbxDiagnosticsText.Text = await RunPbxDiagnosticsAsync();
            FooterStatusText.Text = "PBX diagnostics complete.";
        }
        finally
        {
            RunPbxDiagnosticsButton.IsEnabled = true;
        }
    }

    private async Task<string> RunPbxDiagnosticsAsync()
    {
        DebugLog.Write("PBX diagnostics started. Internal note: Yeastar S100 reaches EOL on 2027-07-01; plan P-Series migration support.");
        var report = new StringBuilder();

        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            report.AppendLine("Network: No network connectivity detected.");
            report.AppendLine("Registration: Not tested because the PC is offline.");
            return report.ToString().Trim();
        }

        var registration = await _sipRegistrationService.RefreshRegistrationAsync();
        if (registration.Connected)
        {
            _registered = true;
            SetConnectionState("Connected", "#DFF8EE", "#106247");
            report.AppendLine("Registration: OK.");
        }
        else
        {
            report.AppendLine($"Registration: Failed. {ToCustomerConnectionMessage(registration.Message)}");
        }

        var options = await _sipRegistrationService.SendOptionsAsync();
        report.AppendLine(options.Signalled
            ? "PBX response: OK. The server answered OPTIONS."
            : $"PBX response: Failed. {options.Message}");

        var message = await _sipRegistrationService.SendMessageAsync(_config.Extension, "PBX compatibility test from CK Media Services.");
        report.AppendLine(message.Signalled
            ? "Extension messaging: OK. SIP MESSAGE was accepted."
            : $"Extension messaging: Failed. {message.Message}");

        report.AppendLine($"RTP audio: {_sipRegistrationService.RtpStatus}");
        report.AppendLine($"Outbound route clue: {_sipRegistrationService.LastCallFailureReason}");
        report.AppendLine(_config.SipAlgCompatibilityMode
            ? "Router keepalive assist: On. Standard SIP registration is unchanged; extra keepalive traffic is being added."
            : "Router keepalive assist: Off. Merlin SIP is using standard SIP registration.");
        report.AppendLine("Video: H.264 readiness is noted, but video calling remains disabled until real video RTP/SDP negotiation is implemented.");

        return report.ToString().Trim();
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
            var install = WpfMessageBox.Show(
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
                    System.Windows.Application.Current.Shutdown();
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

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_startupUpdateCheckCompleted)
        {
            return;
        }

        _startupUpdateCheckCompleted = true;
        await Task.Delay(TimeSpan.FromSeconds(8));

        var result = await _updateService.CheckForUpdatesAsync();
        if (!result.UpdateAvailable || string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            return;
        }

        UpdateStatusText.Text = result.Message;
        UpdateNotesText.Text = result.Notes ?? string.Empty;
        FooterStatusText.Text = "A Merlin SIP update is available.";

        var notes = string.IsNullOrWhiteSpace(result.Notes) ? "" : $"\n\n{result.Notes}";
        var install = WpfMessageBox.Show(
            $"{result.Message}{notes}\n\nInstall this update now?",
            "Merlin SIP update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (install == MessageBoxResult.Yes)
        {
            try
            {
                var installerPath = await _updateService.DownloadInstallerAsync(result);
                Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\"")
                {
                    UseShellExecute = true
                });
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception error)
            {
                DebugLog.Write($"STARTUP UPDATE failed error={error.Message}");
                FooterStatusText.Text = "Unable to start the update right now.";
            }
        }
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
        var confirm = WpfMessageBox.Show(
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
        _chatMessages.Clear();
        await Task.Delay(50);
        System.Windows.Application.Current.Shutdown();
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

    private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
    {
        var destination = NormalizeDialDestination(MessageToTextBox.Text);
        var message = MessageBodyTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(destination))
        {
            FooterStatusText.Text = "Enter an extension to message.";
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            FooterStatusText.Text = "Enter a message first.";
            return;
        }

        SendMessageButton.IsEnabled = false;
        try
        {
            var contact = _contactStore.FindByNumber(_contacts, destination);
            var name = contact?.Name ?? destination;
            if (!string.Equals(_selectedChatNumber, destination, StringComparison.OrdinalIgnoreCase))
            {
                _selectedChatNumber = destination;
                ChatThreadTitleText.Text = name;
                ChatThreadSubtitleText.Text = contact is null || string.IsNullOrWhiteSpace(contact.Company)
                    ? destination
                    : $"{destination}  {contact.Company}";
            }

            var result = await _sipRegistrationService.SendMessageAsync(destination, message);
            await AddChatMessage("Outbound", name, destination, message, result.Signalled ? "Sent" : "Failed");
            FooterStatusText.Text = result.Message;
            if (result.Signalled)
            {
                MessageBodyTextBox.Text = string.Empty;
            }
        }
        finally
        {
            SendMessageButton.IsEnabled = true;
        }
    }

    private void MessageBodyTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SendMessageButton_Click(sender, e);
        }
    }

    private void ChatContactsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChatContactsListView.SelectedItem is not ContactEntry contact)
        {
            return;
        }

        SelectChatContact(contact);
    }

    private void ChatContactsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatContactsListView.SelectedItem is ContactEntry contact)
        {
            SelectChatContact(contact);
        }
    }

    private void SelectChatContact(ContactEntry contact)
    {
        _selectedChatNumber = NormalizeDialDestination(contact.Number);
        MessageToTextBox.Text = contact.Number;
        ChatThreadTitleText.Text = contact.Name;
        ChatThreadSubtitleText.Text = string.IsNullOrWhiteSpace(contact.Company)
            ? contact.Number
            : $"{contact.Number}  {contact.Company}";
        RefreshChatThread();
        MessageBodyTextBox.Focus();
    }

    private void RefreshChatThread()
    {
        _chatThreadMessages.Clear();
        if (string.IsNullOrWhiteSpace(_selectedChatNumber))
        {
            ChatThreadTitleText.Text = "Choose a conversation";
            ChatThreadSubtitleText.Text = "Select a contact to view messages.";
            return;
        }

        foreach (var message in _chatMessages
            .Where(message => MessageBelongsToThread(message, _selectedChatNumber))
            .OrderBy(message => ParseChatTimestamp(message.SentAt)))
        {
            _chatThreadMessages.Add(message);
        }
    }

    private async void ClearChatThreadButton_Click(object sender, RoutedEventArgs e)
    {
        var destination = NormalizeDialDestination(MessageToTextBox.Text);
        if (string.IsNullOrWhiteSpace(destination))
        {
            FooterStatusText.Text = "Choose a conversation first.";
            return;
        }

        var contact = _contactStore.FindByNumber(_contacts, destination);
        var displayName = contact?.Name ?? destination;
        var confirm = WpfMessageBox.Show(
            $"Clear message history with {displayName}?",
            "Clear conversation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var threadMessages = _chatMessages
            .Where(message => MessageBelongsToThread(message, destination))
            .ToList();

        foreach (var message in threadMessages)
        {
            _chatMessages.Remove(message);
        }

        await _chatMessageStore.SaveAsync(_chatMessages);
        RefreshChatThread();
        FooterStatusText.Text = $"Conversation with {displayName} cleared.";
    }

    private static bool MessageBelongsToThread(ChatMessageEntry message, string destination)
    {
        return NormalizeDialDestination(message.Number).Equals(destination, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AddChatMessage(string direction, string name, string number, string message, string result)
    {
        var entry = new ChatMessageEntry
        {
            Direction = direction,
            Name = name,
            Number = number,
            Message = message,
            SentAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Result = result
        };

        _chatMessages.Add(entry);
        await _chatMessageStore.SaveAsync(_chatMessages.OrderBy(message => ParseChatTimestamp(message.SentAt)));
        if (MessageBelongsToThread(entry, _selectedChatNumber))
        {
            RefreshChatThread();
        }
    }

    private static DateTimeOffset ParseChatTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }
}
