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
using MerlinSip.ViewModels;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;
using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;

namespace MerlinSip;

public partial class MainWindow : Window
{
    private readonly ContactStore _contactStore = new();
    private readonly CallHistoryStore _callHistoryStore = new();
    private readonly ChatMessageStore _chatMessageStore = new();
    private readonly AppCacheService _cacheService = new();
    private readonly DeviceDiscoveryService _deviceDiscoveryService = new();
    private readonly SipRegistrationService _sipRegistrationService = new();
    private readonly LicenseService _licenseService = new();
    private readonly RingtonePlayer _ringtonePlayer = new();
    private readonly UpdateService _updateService = new();
    private readonly ProvisioningService _provisioningService = new();
    private readonly SipsorceryCompatibilityService _sipsorceryCompatibilityService = new();
    private readonly MainWindowViewModel _viewModel = new();
    private WinForms.NotifyIcon? _trayIcon;
    private readonly ObservableCollection<ContactEntry> _contacts = [];
    private readonly ObservableCollection<CallHistoryEntry> _callHistory = [];
    private readonly ObservableCollection<ChatMessageEntry> _chatMessages = [];
    private readonly ObservableCollection<ChatMessageEntry> _chatThreadMessages = [];
    private readonly ObservableCollection<ContactEntry> _filteredDirectoryContacts = [];
    private readonly ObservableCollection<ContactEntry> _filteredDirectoryFavorites = [];
    private readonly ObservableCollection<ContactEntry> _filteredPhonebookContacts = [];
    private readonly ObservableCollection<CallHistoryEntry> _filteredRecentCalls = [];
    private readonly ObservableCollection<CallHistoryEntry> _filteredCallHistory = [];
    private readonly DispatcherTimer _callTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _connectionWatchdog = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _licenseWatchdog = new() { Interval = TimeSpan.FromHours(6) };
    private CancellationTokenSource? _earlyMediaRingbackCancellation;
    private CancellationTokenSource? _incomingNoAnswerCancellation;
    private bool _connectionCheckInProgress;
    private bool _licenseCheckInProgress;
    private bool _licenseLocked;
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
    private bool _localRingbackActive;
    private bool _allowExit;
    private bool _networkDiagnosticsRunning;
    private NetworkEngine? _activeNetworkDiagnostics;
    private readonly List<string> _networkDiagnosticsProgress = [];
    private string _selectedChatNumber = "";
    private string _activeRemoteNumber = "";
    private string _userSelectedPresence = "Available";
    private ContactEntry? _editingContact;
    private IncomingCallWindow? _incomingCallWindow;
    private System.Windows.Interop.HwndSource? _hwndSource;
    private GlobalHotkeyService? _hotkeyService;
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private string _lastClipboardText = "";

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public MainWindow(AppStartupConfig config)
    {
        _config = config;
        InitializeComponent();
        DataContext = _viewModel;
        ApplyStartupConfig();
        ApplyAppVersion();
        LoadDefaultDeviceSelectors();
        DialContactsListView.ItemsSource = _filteredDirectoryContacts;
        DialFavoritesListView.ItemsSource = _filteredDirectoryFavorites;
        PhonebookContactsListView.ItemsSource = _filteredPhonebookContacts;
        ChatContactsListView.ItemsSource = _contacts;
        RecentCallsListView.ItemsSource = _filteredRecentCalls;
        CallHistoryListView.ItemsSource = _filteredCallHistory;
        ChatMessagesListView.ItemsSource = _chatThreadMessages;
        _sipRegistrationService.IncomingCall += SipRegistrationService_IncomingCall;
        _sipRegistrationService.IncomingMessage += SipRegistrationService_IncomingMessage;
        _sipRegistrationService.CallProgress += SipRegistrationService_CallProgress;
        _sipRegistrationService.CallEnded += SipRegistrationService_CallEnded;
        _sipRegistrationService.ContactPresenceChanged += SipRegistrationService_ContactPresenceChanged;
        _sipRegistrationService.HeartbeatStatus += SipRegistrationService_HeartbeatStatus;
        _callTimer.Tick += CallTimer_Tick;
        _connectionWatchdog.Tick += ConnectionWatchdog_Tick;
        _licenseWatchdog.Tick += LicenseWatchdog_Tick;
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
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
        WindowsStartupService.EnableLaunchOnWindowsStartup();
        _licenseWatchdog.Start();
        _ = CheckForUpdatesOnStartupAsync();
        await VerifyLicenseAsync();
        _ = RegisterSipAsync();
        _connectionWatchdog.Start();
        InitGlobalHotkeys();
        InitHwndHooks();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        HideIncomingCallSurfaces();
        _earlyMediaRingbackCancellation?.Cancel();
        _earlyMediaRingbackCancellation?.Dispose();
        _earlyMediaRingbackCancellation = null;
        _connectionWatchdog.Stop();
        _licenseWatchdog.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        _ringtonePlayer.Dispose();
        _sipRegistrationService.Dispose();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit)
        {
            CleanupHooks();
            return;
        }

        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        FooterStatusText.Text = "Merlin SIP is running in the notification area.";
    }

    private void CleanupHooks()
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(HwndHook);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) RemoveClipboardFormatListener(handle);
            _hwndSource = null;
        }
        _hotkeyService?.Dispose();
        _hotkeyService = null;
    }

    private void InitHwndHooks()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(HwndHook);
            AddClipboardFormatListener(handle);
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && (wParam.ToInt32() == DBT_DEVNODES_CHANGED || wParam.ToInt32() == 0x8000))
        {
            DebugLog.Write("USB audio device change detected. Re-enumerating audio devices...");
            Dispatcher.InvokeAsync(LoadDeviceSelectors, DispatcherPriority.Background);
        }
        else if (msg == WM_CLIPBOARDUPDATE)
        {
            ProcessClipboardUpdate();
        }
        return IntPtr.Zero;
    }

    private void ProcessClipboardUpdate()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText().Trim();
                if (text != _lastClipboardText && text.Length >= 7 && text.Length <= 18 && text.All(c => char.IsDigit(c) || c == '+' || c == '-' || c == ' '))
                {
                    _lastClipboardText = text;
                    DestinationTextBox.Text = text;
                    NoticeText.Text = "Copied number ready to dial: " + text;
                }
            }
        }
        catch { }
    }

    private void InitGlobalHotkeys()
    {
        try
        {
            _hotkeyService = new GlobalHotkeyService(this);
            _hotkeyService.AnswerRequested += delegate
            {
                if (_incomingRinging) AnswerIncomingCall();
                else if (!_callInProgress && !string.IsNullOrWhiteSpace(DestinationTextBox.Text)) DialButton_Click(this, new RoutedEventArgs());
            };
            _hotkeyService.HoldRequested += delegate { if (_callInProgress) HoldButton_Click(this, new RoutedEventArgs()); };
            _hotkeyService.TransferRequested += delegate { if (_callInProgress) TransferButton_Click(this, new RoutedEventArgs()); };
            _hotkeyService.Register();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Failed to initialize global hotkeys: " + ex.Message);
        }
    }

    private void InitializeTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CKMedia-Icon.ico");
        var icon = File.Exists(iconPath) ? new DrawingIcon(iconPath) : DrawingSystemIcons.Application;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open Merlin SIP", null, (_, _) => Dispatcher.InvokeAsync(RestoreFromTray));
        menu.Items.Add("Exit Merlin SIP", null, (_, _) => Dispatcher.InvokeAsync(ExitFromTray));

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "Merlin SIP",
            Icon = icon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.InvokeAsync(RestoreFromTray);
    }

    private bool _isMiniWidgetMode;

    public void ToggleMiniWidgetMode()
    {
        _isMiniWidgetMode = !_isMiniWidgetMode;
        if (_isMiniWidgetMode)
        {
            this.Width = 360.0;
            this.Height = 160.0;
            this.Topmost = true;
            this.WindowStyle = WindowStyle.ToolWindow;
            NoticeText.Text = "Mini-Widget Mode Active";
        }
        else
        {
            this.Width = 1420.0;
            this.Height = 900.0;
            this.Topmost = false;
            this.WindowStyle = WindowStyle.SingleBorderWindow;
            NoticeText.Text = "Standard Mode";
        }
    }

    private void ToggleMiniWidgetMode_Click(object sender, RoutedEventArgs e)
    {
        ToggleMiniWidgetMode();
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void HandleTelProtocolLaunch(string rawUrl)
    {
        var number = ProtocolHandlerService.ParseTelUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(number))
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            RestoreFromTray();
            MainTabs.SelectedItem = PhoneTab;
            DestinationTextBox.Text = number;
            NoticeText.Text = $"Tel link target: {number}";
            FooterStatusText.Text = $"Opened tel: link for {number}";
            UpdateCallControls();

            if (/*_config.AutoDialTelLinks &&*/ !_callInProgress && !_incomingRinging && _registered && !_licenseLocked)
            {
                DialButton_Click(this, new RoutedEventArgs());
            }
        });
    }

    private void ExitFromTray()
    {
        // Trigger full application shutdown ensuring all resources are cleaned up
        _allowExit = true;
        // Close the main window which will invoke cleanup in MainWindow_Closed
        Close();
        // Explicitly shutdown the WPF application (ShutdownMode is OnExplicitShutdown)
        System.Windows.Application.Current?.Shutdown();
    }

    private async void ConnectionWatchdog_Tick(object? sender, EventArgs e)
    {
        await EnsureConnectionReadyAsync();
    }

    private async void LicenseWatchdog_Tick(object? sender, EventArgs e)
    {
        await VerifyLicenseAsync();
    }

    private async Task EnsureConnectionReadyAsync()
    {
        if (_licenseLocked || _connectionCheckInProgress || _callInProgress || _incomingRinging)
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
        catch (Exception error)
        {
            DebugLog.Write($"EnsureConnectionReady failed error={error.Message}");
            _registered = false;
            SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
            UpdateCallControls();
        }
        finally
        {
            _connectionCheckInProgress = false;
        }
    }

    private void CallTimer_Tick(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            UpdateCallTimer();
        }
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

    private void ShowDialingCallTimer()
    {
        _callTimer.Stop();
        _activeCallConnectedAt = null;
        CallTimerText.Text = "Ringing";
        CallTimerPill.Visibility = Visibility.Visible;
        CallTimerPill.Background = (WpfBrush)new BrushConverter().ConvertFromString("#FFF3D6")!;
        CallTimerText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#8A4F08")!;
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

    private void StartLocalRingback()
    {
        if (_localRingbackActive || !_callInProgress || _callConnected || _incomingRinging)
        {
            return;
        }

        _localRingbackActive = true;
        _ringtonePlayer.StartUkRingback(_config.AudioOutput, _config.HeadphoneVolume);
        DebugLog.Write("LOCAL RINGBACK start cadence=uk");
    }

    private void StopLocalRingback()
    {
        _earlyMediaRingbackCancellation?.Cancel();
        if (!_localRingbackActive)
        {
            return;
        }

        _localRingbackActive = false;
        _ringtonePlayer.Stop();
        DebugLog.Write("LOCAL RINGBACK stop");
    }

    private async void UseRingbackIfEarlyMediaIsSilent()
    {
        _earlyMediaRingbackCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _earlyMediaRingbackCancellation = cancellation;

        try
        {
            await Task.Delay(1200, cancellation.Token);
            if (_sipRegistrationService.HasInboundRtpAudio)
            {
                StopLocalRingback();
                return;
            }

            if (_activeCallDirection == "Outbound" && _callInProgress && !_callConnected && !_incomingRinging && !_sipRegistrationService.HasInboundRtpAudio)
            {
                StartLocalRingback();
            }

            while (_activeCallDirection == "Outbound" && _callInProgress && !_callConnected && !cancellation.IsCancellationRequested)
            {
                if (_sipRegistrationService.HasInboundRtpAudio)
                {
                    StopLocalRingback();
                    return;
                }

                await Task.Delay(500, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_earlyMediaRingbackCancellation, cancellation))
            {
                _earlyMediaRingbackCancellation = null;
            }

            cancellation.Dispose();
        }
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

    private bool IsSilentRingingEnabled()
    {
        return string.Equals(_config.DndMode, "Silent ringing", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldSendBusyWhenBusy(string callerNumber)
    {
        var action = IsInternalNumber(callerNumber)
            ? _config.InternalBusyAction
            : _config.ExternalBusyAction;

        return action.Contains("busy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInternalNumber(string number)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && digits.Length <= 4;
    }

    private void StartIncomingNoAnswerTimeout(string callerNumber)
    {
        CancelIncomingNoAnswerTimeout();
        var seconds = IsInternalNumber(callerNumber)
            ? _config.InternalNoAnswerSeconds
            : _config.ExternalNoAnswerSeconds;

        if (seconds <= 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _incomingNoAnswerCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_incomingRinging && _sipRegistrationService.HasPendingIncomingCall)
                    {
                        FooterStatusText.Text = "Incoming call timed out.";
                        DeclineIncomingCall();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelIncomingNoAnswerTimeout()
    {
        _incomingNoAnswerCancellation?.Cancel();
        _incomingNoAnswerCancellation?.Dispose();
        _incomingNoAnswerCancellation = null;
    }

    private async Task ResetFailedCallDisplayAsync(string message)
    {
        var seconds = Math.Max(1, _config.FailedCallDisplaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await Dispatcher.InvokeAsync(() =>
        {
            if (string.Equals(FooterStatusText.Text, message, StringComparison.Ordinal) ||
                string.Equals(NoticeText.Text, message, StringComparison.Ordinal))
            {
                NoticeText.Text = "Application ready.";
                FooterStatusText.Text = "Application ready.";
            }
        });
    }

    private void HideIncomingCallSurfaces()
    {
        CancelIncomingNoAnswerTimeout();
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
        Dispatcher.InvokeAsync(() =>
        {
            var contact = _contactStore.FindByNumber(_contacts, e.CallerNumber);
            var callerName = contact?.Name ?? e.CallerNumber;
            var alreadyOnCall = _callConnected || (_callInProgress && !_incomingRinging);
            var silentRinging = IsSilentRingingEnabled();
            var useDesktopPopup = !silentRinging && ShouldUseDesktopIncomingPopup();
            _activeRemoteNumber = e.CallerNumber;
            SetContactPresence(e.CallerNumber, "Ringing");
            SetPresenceDisplay("Busy");
            ShowDialingCallTimer();
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

            if (alreadyOnCall && ShouldSendBusyWhenBusy(e.CallerNumber))
            {
                NoticeText.Text = "Incoming call declined because the line is busy.";
                FooterStatusText.Text = "Busy response sent.";
                _ = AddCallHistory("Inbound", callerName, e.CallerNumber, "Busy", "Line was already in use.");
                DeclineIncomingCall();
                return;
            }

            if (!silentRinging)
            {
                _ringtonePlayer.Start(_config.AudioOutput, _config.Ringtone, _config.HeadphoneVolume);
            }

            if (useDesktopPopup)
            {
                ShowIncomingCallWindow(callerName, e.CallerNumber);
            }

            StartIncomingNoAnswerTimeout(e.CallerNumber);
        });
    }

    private void SipRegistrationService_CallProgress(object? sender, CallProgressEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (e.Connected)
            {
                StopLocalRingback();
                HideIncomingCallSurfaces();
                SetContactPresence(_activeRemoteNumber, "Busy");
                _ = _sipRegistrationService.PublishPresenceAsync("Busy");
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
                if (_activeCallDirection == "Outbound")
                {
                    if (e.Code == 180)
                    {
                        StartLocalRingback();
                    }
                    else
                    {
                        UseRingbackIfEarlyMediaIsSilent();
                    }
                }
                else
                {
                    StopLocalRingback();
                }

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
                NoticeText.Text = e.Message;
                FooterStatusText.Text = e.Message;
                _ = ResetFailedCallDisplayAsync(e.Message);
                
                string state = "Failed";
                if (e.Code == 486) state = "Busy";
                else if (e.Code == 487) state = "Cancelled";
                else if (e.Code == 603) state = "Declined";
                
                _ = OnCallFinished(state, e.Message);
            }
        });
    }

    private void SipRegistrationService_ContactPresenceChanged(object? sender, ContactPresenceEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var isOwnExtension = false;
            if (!string.IsNullOrWhiteSpace(_config.Extension) &&
                string.Equals(NormalizeDialDestination(e.Number), NormalizeDialDestination(_config.Extension), StringComparison.OrdinalIgnoreCase))
            {
                isOwnExtension = true;
            }
            else if (!string.IsNullOrWhiteSpace(_config.Username) &&
                     string.Equals(NormalizeDialDestination(e.Number), NormalizeDialDestination(_config.Username), StringComparison.OrdinalIgnoreCase))
            {
                isOwnExtension = true;
            }

            if (isOwnExtension)
            {
                if (e.Presence == "Busy" || e.Presence == "Ringing")
                {
                    UpdateMainPresenceDisplayOnly(e.Presence);
                }
                else
                {
                    UpdateMainPresenceDisplayOnly(_userSelectedPresence);
                }
            }

            SetContactPresence(e.Number, e.Presence);
        });
    }

    private void SipRegistrationService_HeartbeatStatus(object? sender, HeartbeatStatusEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_callInProgress || _incomingRinging)
            {
                return;
            }

            if (e.Success)
            {
                _registered = true;
                SetConnectionState("Connected", "#DFF8EE", "#106247");
                UpdateCallControls();
                return;
            }

            DebugLog.Write($"HEARTBEAT status failed code={e.ResponseCode} failures={e.ConsecutiveFailures} message={e.Message}");
            if (e.ConsecutiveFailures >= 3)
            {
                _registered = false;
                SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
                UpdateCallControls();
            }
        }, DispatcherPriority.Background);
    }

    private void SipRegistrationService_CallEnded(object? sender, CallEndedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            NoticeText.Text = "Call ended.";
            FooterStatusText.Text = e.Message;
            
            string state = "Ended";
            if (!_callConnected)
            {
                state = _activeCallDirection == "Outbound" ? "No Answer" : "Missed";
            }
            await OnCallFinished(state, e.Message);
        });
    }

    private void SipRegistrationService_IncomingMessage(object? sender, IncomingMessageEventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
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
        var customEndpoint = _config.AllowsCustomSipEndpoint;
        PrivatePbxSettingsPanel.Visibility = customEndpoint ? Visibility.Visible : Visibility.Collapsed;
        PrivatePbxSettingsTextBox.Text = customEndpoint ? _config.Server : string.Empty;
        SipAlgCompatibilityCheckBox.IsChecked = _config.SipAlgCompatibilityMode;
        IgnoreSslErrorsCheckBox.IsChecked = _config.IgnoreSslErrors;
        SelectSipTransportMode(_config.SipSignallingTransport);
        LicenseStatusText.Text = ShortLicenseStatus(_config.LicenseStatus);
        LicensedToText.Text = LicenseeFromStatus(_config.LicenseStatus);
        LoadApplicationSettingsControls();
        UpdateNetworkAssistanceText();
        ApplyDndMode();
    }

    private void LoadApplicationSettingsControls()
    {
        MobileNumberTextBox.Text = _config.MobileNumber;
        SelectComboBoxItem(DndModeComboBox, _config.DndMode);
        SelectComboBoxItem(DeclineActionComboBox, _config.DeclineIncomingAction);
        SelectComboBoxItem(InternalBusyActionComboBox, _config.InternalBusyAction);
        SelectComboBoxItem(InternalNoAnswerTimeoutComboBox, $"{_config.InternalNoAnswerSeconds} seconds");
        SelectComboBoxItem(ExternalBusyActionComboBox, _config.ExternalBusyAction);
        SelectComboBoxItem(ExternalNoAnswerTimeoutComboBox, $"{_config.ExternalNoAnswerSeconds} seconds");
        QueuePickupCheckBox.IsChecked = _config.QueuePickupEnabled;
        CombineContactsCheckBox.IsChecked = _config.CombineContactsInSearch;
        SelectComboBoxItem(FailedCallTimeoutComboBox, $"{_config.FailedCallDisplaySeconds} seconds");
    }

    private static void SelectComboBoxItem(System.Windows.Controls.ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private static string ComboBoxText(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Content is not null
            ? item.Content.ToString() ?? fallback
            : fallback;
    }

    private static string ComboBoxTag(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;
    }

    private void SelectSipTransportMode(string transport)
    {
        var normalized = string.Equals(transport, AppStartupConfig.TransportTcp, StringComparison.OrdinalIgnoreCase)
            ? AppStartupConfig.TransportTcp
            : string.Equals(transport, AppStartupConfig.TransportTls, StringComparison.OrdinalIgnoreCase)
                ? AppStartupConfig.TransportTls
                : AppStartupConfig.TransportUdp;

        foreach (var item in SipTransportModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                SipTransportModeComboBox.SelectedItem = item;
                return;
            }
        }

        SipTransportModeComboBox.SelectedIndex = 0;
    }

    private static int ComboBoxSeconds(System.Windows.Controls.ComboBox comboBox, int fallback)
    {
        var text = ComboBoxText(comboBox, $"{fallback} seconds");
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : fallback;
    }

    private async Task VerifyLicenseAsync()
    {
        if (_licenseCheckInProgress)
        {
            return;
        }

        _licenseCheckInProgress = true;
        try
        {
            var result = await _licenseService.VerifyAsync(_config.LicenseKey, _config.LicenseLocalKey);
            if (!result.Checked)
            {
                FooterStatusText.Text = result.Message;
                return;
            }

            if (!result.Active)
            {
                LockForInactiveLicense(result.Message);
                return;
            }

            _licenseLocked = false;
            var status = string.IsNullOrWhiteSpace(result.Message) ? _config.LicenseStatus : result.Message;
            _config = _config with
            {
                LicenseStatus = status,
                LicenseLocalKey = _licenseService.LocalKey ?? _config.LicenseLocalKey
            };
            await _cacheService.SaveSettingsAsync(_config.WithFixedSipEndpoint());
            LicenseStatusText.Text = ShortLicenseStatus(status);
            LicensedToText.Text = string.IsNullOrWhiteSpace(result.Licensee)
                ? LicenseeFromStatus(status)
                : result.Licensee;
            UpdateCallControls();
        }
        finally
        {
            _licenseCheckInProgress = false;
        }
    }

    private void LockForInactiveLicense(string message)
    {
        _licenseLocked = true;
        _registered = false;
        _callInProgress = false;
        _callConnected = false;
        _incomingRinging = false;
        StopLocalRingback();
        _ringtonePlayer.Stop();
        HideIncomingCallSurfaces();
        _sipRegistrationService.Dispose();
        SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
        LicenseStatusText.Text = "Licence inactive";
        LicensedToText.Text = "Inactive";
        NoticeText.Text = "Licence inactive.";
        FooterStatusText.Text = string.IsNullOrWhiteSpace(message)
            ? "This licence is inactive. Contact CK Media Services."
            : message;
        UpdateCallControls();
    }

    private static string ShortLicenseStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "Licensed" : status;
    }

    private static string LicenseeFromStatus(string status)
    {
        const string prefix = "Licensed to ";
        if (status.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return status[prefix.Length..].Trim();
        }

        return string.IsNullOrWhiteSpace(status) ? "CK Media Services" : status;
    }

    private void LoadDeviceSelectors()
    {
        AudioInputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioInputs();
        AudioOutputComboBox.ItemsSource = _deviceDiscoveryService.GetAudioOutputs();
        RingtoneComboBox.ItemsSource = RingtonePlayer.Choices;
        SelectDevice(AudioInputComboBox, _config.AudioInput);
        SelectDevice(AudioOutputComboBox, _config.AudioOutput);
        SelectRingtone(_config.Ringtone);
        LoadVolumeSliders();
    }

    private void LoadDefaultDeviceSelectors()
    {
        AudioInputComboBox.ItemsSource = new[] { _config.AudioInput };
        AudioOutputComboBox.ItemsSource = new[] { _config.AudioOutput };
        RingtoneComboBox.ItemsSource = RingtonePlayer.Choices;
        AudioInputComboBox.SelectedIndex = 0;
        AudioOutputComboBox.SelectedIndex = 0;
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
        if (_licenseLocked)
        {
            SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
            ServerStatusText.Text = "Licence inactive.";
            UpdateCallControls();
            return;
        }

        SetConnectionState("Connecting...", "#FFF1D6", "#8A4F08");
        ServerStatusText.Text = "Checking account connection.";
        await RefreshConnectionDiagnosticsAsync();
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _registered = false;
            SetConnectionState("No network", "#FFE2E2", "#9B1C1C");
            ServerStatusText.Text = "No network connectivity detected.";
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
            await RefreshPresenceSubscriptionsAsync();
        }
        else
        {
            if (!_config.UsesTcpSignalling && !_config.UsesTlsSignalling &&
                (result.Message.Contains("Timed out", StringComparison.OrdinalIgnoreCase) || 
                 result.Message.Contains("Socket error", StringComparison.OrdinalIgnoreCase) ||
                 result.Message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase)))
            {
                DebugLog.Write("UDP register failed/timed out. Attempting automatic fallback to TCP signalling for SIP ALG / Virgin Media line compatibility...");
                var tcpConfig = _config with { SipSignallingTransport = AppStartupConfig.TransportTcp };
                try
                {
                    var tcpResult = await _sipRegistrationService.RegisterAsync(tcpConfig);
                    if (tcpResult.Connected)
                    {
                        DebugLog.Write("TCP fallback registration successful! Updating config to use TCP.");
                        _config = tcpConfig;
                        await _cacheService.SaveSettingsAsync(_config.WithFixedSipEndpoint());
                        ApplyStartupConfig();
                        result = tcpResult;
                    }
                }
                catch (Exception tcpError)
                {
                    DebugLog.Write($"TCP fallback failed: {tcpError.Message}");
                }
            }

            if (result.Connected)
            {
                _registered = true;
                SetConnectionState("Connected", "#DFF8EE", "#106247");
                ServerStatusText.Text = ToCustomerConnectionMessage(result.Message);
                await RefreshPresenceSubscriptionsAsync();
            }
            else
            {
                _registered = false;
                SetConnectionState("Not connected", "#FFE2E2", "#9B1C1C");
                ServerStatusText.Text = ToCustomerConnectionMessage(result.Message);
            }
        }

        await RefreshConnectionDiagnosticsAsync();
        UpdateCallControls();
    }

    private void SetConnectionState(string text, string background, string foreground)
    {
        _viewModel.ConnectionState = text;
        ConnectionStatusText.Text = text;
        ConnectionPill.Background = (WpfBrush)new BrushConverter().ConvertFromString(background)!;
        ConnectionStatusText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(foreground)!;
        FooterStatusBar.Background = (WpfBrush)new BrushConverter().ConvertFromString(background)!;
        FooterStatusText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(foreground)!;
        FooterStatusText.Text = text.Equals("Connected", StringComparison.OrdinalIgnoreCase)
            ? "Live connectivity status: Connected"
            : text.Equals("Connecting...", StringComparison.OrdinalIgnoreCase)
                ? "Live connectivity status: Checking account connection"
                : text.Equals("No network", StringComparison.OrdinalIgnoreCase)
                    ? "Live connectivity status: No network connection detected"
                    : "Live connectivity status: Not connected";
    }

    private void ApplyAppVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version is null
            ? "Unknown"
            : version.Revision > 0
                ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        AppVersionText.Text = $"v{versionStr}";
        AboutVersionText.Text = versionStr;
        UpdateStatusText.Text = $"Version {versionStr}";
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

        ApplyGlobalSearchFilter();
        _ = RefreshPresenceSubscriptionsAsync();
    }

    private async Task RefreshPresenceSubscriptionsAsync()
    {
        if (!_registered)
        {
            return;
        }

        var extensions = _contacts.Select(contact => contact.Number);
        if (!string.IsNullOrWhiteSpace(_config.Extension))
        {
            extensions = extensions.Concat(new[] { _config.Extension });
        }
        await _sipRegistrationService.SubscribeToContactPresenceAsync(extensions.Distinct());
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

        ApplyGlobalSearchFilter();
    }

    private async Task LoadCallHistoryAsync()
    {
        _callHistory.Clear();
        foreach (var call in await _callHistoryStore.LoadAsync())
        {
            _callHistory.Add(call);
        }

        ApplyGlobalSearchFilter();
    }

    private void ApplyGlobalSearchFilter()
    {
        SearchDebounceTimer_Tick(null, EventArgs.Empty);
    }


    private IEnumerable<ContactEntry> FilterFavorites(IEnumerable<ContactEntry> source, string query)
    {
        var favorites = source.Where(c => c.IsFavorite);
        return string.IsNullOrWhiteSpace(query)
            ? favorites
            : favorites.Where(contact =>
                ContainsSearchText(contact.Name, query) ||
                ContainsSearchText(contact.Number, query) ||
                ContainsSearchText(contact.Company, query) ||
                ContainsSearchText(contact.Notes, query) ||
                ContainsSearchText(contact.PresenceLabel, query));
    }

    private IEnumerable<ContactEntry> FilterContacts(IEnumerable<ContactEntry> source, string query)
    {
        return string.IsNullOrWhiteSpace(query)
            ? source
            : source.Where(contact =>
                ContainsSearchText(contact.Name, query) ||
                ContainsSearchText(contact.Number, query) ||
                ContainsSearchText(contact.Company, query) ||
                ContainsSearchText(contact.Notes, query) ||
                ContainsSearchText(contact.PresenceLabel, query));
    }

    private IEnumerable<CallHistoryEntry> FilterCalls(IEnumerable<CallHistoryEntry> source, string query)
    {
        return string.IsNullOrWhiteSpace(query)
            ? source
            : source.Where(call =>
                ContainsSearchText(call.Name, query) ||
                ContainsSearchText(call.Number, query) ||
                ContainsSearchText(call.Direction, query) ||
                ContainsSearchText(call.Result, query) ||
                ContainsSearchText(call.Detail, query) ||
                ContainsSearchText(call.StartedAt, query));
    }

    private static bool ContainsSearchText(string value, string query)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
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

    private void GlobalSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        var query = GlobalSearchTextBox.Text.Trim();
        
        var contacts = _contacts.ToList();
        var callHistory = _callHistory.ToList();
        
        var result = await Task.Run(() => 
        {
            return new 
            {
                FilteredContacts = FilterContacts(contacts, query).ToList(),
                FilteredFavorites = FilterFavorites(contacts, query).ToList(),
                FilteredCalls = FilterCalls(callHistory, query).ToList()
            };
        });
        
        ReplaceCollection(_filteredDirectoryContacts, result.FilteredContacts);
        ReplaceCollection(_filteredPhonebookContacts, result.FilteredContacts);
        ReplaceCollection(_filteredDirectoryFavorites, result.FilteredFavorites);
        ReplaceCollection(_filteredRecentCalls, result.FilteredCalls);
        ReplaceCollection(_filteredCallHistory, result.FilteredCalls);
        
        ApplyGlobalSearchNavigation();
    }

    private void ApplyGlobalSearchNavigation()
    {
        var query = GlobalSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        MainTabs.SelectedItem = PhoneTab;
        if (_filteredDirectoryContacts.Count > 0)
        {
            DirectoryTabs.SelectedItem = DirectoryContactsTab;
        }
        else
        {
            DirectoryTabs.SelectedItem = DirectoryHistoryTab;
        }

        if (IsDialableSearch(query))
        {
            DestinationTextBox.Text = NormalizeDialDestination(query);
        }
    }

    private static bool IsDialableSearch(string query)
    {
        return query.All(character => char.IsDigit(character) || character is '+' or '*' or '#' or ' ' or '-' or '(' or ')');
    }

    private void GlobalSearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var query = GlobalSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            DestinationTextBox.Focus();
            return;
        }

        var normalized = NormalizeDialDestination(query);
        var firstContact = _filteredDirectoryContacts.FirstOrDefault()
            ?? _filteredPhonebookContacts.FirstOrDefault();
        var firstCall = _filteredRecentCalls.FirstOrDefault()
            ?? _filteredCallHistory.FirstOrDefault();

        if (query.Any(char.IsLetter) && firstContact is not null)
        {
            UseSelectedContact(firstContact);
        }
        else if (query.Any(char.IsLetter) && firstCall is not null)
        {
            DestinationTextBox.Text = firstCall.Number;
            MainTabs.SelectedItem = PhoneTab;
        }
        else
        {
            DestinationTextBox.Text = string.IsNullOrWhiteSpace(normalized) ? query : normalized;
        }

        MainTabs.SelectedItem = PhoneTab;
        DialButton_Click(sender, e);
    }

    private void PresenceButton_Click(object sender, RoutedEventArgs e)
    {
        PresenceButton.ContextMenu.IsOpen = true;
    }

    private void UpdateMainPresenceDisplayOnly(string status)
    {
        PresenceText.Text = status;
        var colour = status.ToLowerInvariant() switch
        {
            "available" => "#16A34A",
            "busy" => "#EF4444",
            "dnd" => "#DC2626",
            "away" or "appear away" => "#F59E0B",
            "offline" or "appear offline" => "#94A3B8",
            _ => "#16A34A"
        };
        PresenceDot.Fill = (WpfBrush)new BrushConverter().ConvertFromString(colour)!;
    }

    private void SetPresenceDisplay(string status)
    {
        UpdateMainPresenceDisplayOnly(status);
        PublishCurrentPresence();
    }

    private void PublishCurrentPresence()
    {
        if (!_registered)
        {
            return;
        }

        var status = PresenceText.Text;
        _ = Task.Run(async () =>
        {
            try
            {
                await _sipRegistrationService.PublishPresenceAsync(status);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Error publishing presence: {ex.Message}");
            }
        });
    }

    private void PresenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item || item.Header is not string status)
        {
            return;
        }

        _userSelectedPresence = status;
        SetPresenceDisplay(status);
        if (status.Equals("DND", StringComparison.OrdinalIgnoreCase) && !_dndEnabled)
        {
            DndButton_Click(sender, e);
            return;
        }

        if (!status.Equals("DND", StringComparison.OrdinalIgnoreCase) && _dndEnabled)
        {
            DndButton_Click(sender, e);
        }

        FooterStatusText.Text = $"Presence set to {status}.";
    }

    private async void DialpadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button)
        {
            return;
        }

        var digitText = button.Content?.ToString();
        if (string.IsNullOrWhiteSpace(digitText))
        {
            return;
        }

        var digit = digitText[0];
        if (_callConnected)
        {
            var result = await _sipRegistrationService.SendDtmfAsync(digit);
            FooterStatusText.Text = result.Signalled
                ? $"Sent tone {digit}."
                : result.Message;
            return;
        }

        DestinationTextBox.Text += digitText;
        DestinationTextBox.CaretIndex = DestinationTextBox.Text.Length;
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
        StopLocalRingback();
        _ringtonePlayer.Stop();
        NoticeText.Text = $"Calling {name}.";
        _activeCallStartedAt = DateTimeOffset.Now;
        _activeCallDirection = "Outbound";
        _activeRemoteNumber = destination;
        SetContactPresence(destination, "Ringing");
        _ = _sipRegistrationService.PublishPresenceAsync("Busy");
        SetPresenceDisplay("Busy");
        ShowDialingCallTimer();
        _callInProgress = true;
        _callConnected = false;
        UpdateCallControls();
        var result = await _sipRegistrationService.InviteAsync(destination);
        FooterStatusText.Text = result.Message;
        if (!result.Signalled)
        {
            NoticeText.Text = result.Message;
            _ = ResetFailedCallDisplayAsync(result.Message);
            await OnCallFinished("Failed", result.Message);
        }
    }

    private async void HangupButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await _sipRegistrationService.EndCallAsync();
        NoticeText.Text = "Call ended.";
        FooterStatusText.Text = result.Message;
        
        string state = "Ended";
        if (!_callConnected)
        {
            state = _activeCallDirection == "Outbound" ? "No Answer" : "Declined";
        }
        await OnCallFinished(state, result.Message);
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
        try
        {
            StopLocalRingback();
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
                SetContactPresence(_activeRemoteNumber, "Offline");
                _activeRemoteNumber = "";
            }
            UpdateCallControls();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Error answering incoming call: {ex.Message}");
            NoticeText.Text = "Failed to answer call.";
            FooterStatusText.Text = "An error occurred while answering the call.";
            _incomingRinging = false;
            _callInProgress = false;
            _callConnected = false;
            UpdateCallControls();
        }
    }

    private async void DeclineIncomingCall()
    {
        var result = await _sipRegistrationService.EndCallAsync();
        NoticeText.Text = "Incoming call declined.";
        FooterStatusText.Text = result.Message;
        await OnCallFinished("Declined", result.Message);
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

    private async void ConferenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_sipRegistrationService.CanControlAudio)
        {
            FooterStatusText.Text = "Connect a call before using conference.";
            return;
        }

        var favorites = _config.ShowFavouriteExtensionsOnTransfer ? _contacts.Where(c => c.IsFavorite) : null;
        var conferenceWindow = new TransferCallWindow(DestinationTextBox.Text, favorites)
        {
            Owner = this,
            Title = "Add to conference"
        };

        if (conferenceWindow.ShowDialog() != true)
        {
            return;
        }

        var target = NormalizeDialDestination(conferenceWindow.TransferTarget);
        if (string.IsNullOrWhiteSpace(target))
        {
            FooterStatusText.Text = "Enter a number to conference in.";
            return;
        }

        var result = await _sipRegistrationService.ConferenceAsync(target);
        FooterStatusText.Text = result.Message;
        if (result.Signalled)
        {
            NoticeText.Text = "Conference call in progress.";
        }
    }

    private async void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        var favorites = _config.ShowFavouriteExtensionsOnTransfer ? _contacts.Where(c => c.IsFavorite) : null;
        var transferWindow = new TransferCallWindow(DestinationTextBox.Text, favorites)
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

    private void ApplyDndMode()
    {
        _dndEnabled = string.Equals(_config.DndMode, "Reject calls", StringComparison.OrdinalIgnoreCase);
        _sipRegistrationService.SetRejectIncomingCalls(_dndEnabled);
        DndButton.Content = _dndEnabled ? "DND on" : "DND";
        if (_dndEnabled)
        {
            SetPresenceDisplay("DND");
        }
        else if (string.Equals(PresenceText.Text, "DND", StringComparison.OrdinalIgnoreCase))
        {
            SetPresenceDisplay("Available");
        }
    }

    private async void DndButton_Click(object sender, RoutedEventArgs e)
    {
        _dndEnabled = !_dndEnabled;
        _config = _config with { DndMode = _dndEnabled ? "Reject calls" : "Off" };
        SelectComboBoxItem(DndModeComboBox, _config.DndMode);
        await _cacheService.SaveSettingsAsync(_config.WithFixedSipEndpoint());
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
        SetPresenceDisplay(_dndEnabled ? "DND" : "Available");
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
        ContactIsFavoriteCheckBox.IsChecked = contact.IsFavorite;
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
            Notes = ContactNotesTextBox.Text.Trim(),
            IsFavorite = ContactIsFavoriteCheckBox.IsChecked == true
        };

        _contacts.Add(savedContact);

        await _contactStore.SaveAsync(_contacts);
        PhonebookContactsListView.SelectedItem = null;
        _editingContact = null;
        ContactNameTextBox.Text = "";
        ContactNumberTextBox.Text = "";
        ContactCompanyTextBox.Text = "";
        ContactNotesTextBox.Text = "";
        ContactIsFavoriteCheckBox.IsChecked = false;
        ApplyGlobalSearchFilter();
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
        ContactIsFavoriteCheckBox.IsChecked = false;
        ApplyGlobalSearchFilter();
        FooterStatusText.Text = "Contact deleted.";
    }

    private void NewContactButton_Click(object sender, RoutedEventArgs e)
    {
        PhonebookContactsListView.SelectedItem = null;
        _editingContact = null;
        ContactNameTextBox.Text = "";
        ContactNumberTextBox.Text = "";
        ContactCompanyTextBox.Text = "";
        ContactNotesTextBox.Text = "";
        ContactIsFavoriteCheckBox.IsChecked = false;
        FooterStatusText.Text = "Ready to create a new contact.";
    }

    private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        var mediaConfig = BuildConfigFromSettings();
        var server = _config.AllowsCustomSipEndpoint
            ? PrivatePbxSettingsTextBox.Text.Trim()
            : AppStartupConfig.FixedSipServer;
        if (_config.AllowsCustomSipEndpoint && string.IsNullOrWhiteSpace(server))
        {
            FooterStatusText.Text = "Enter the SIP server.";
            return;
        }

        _config = _config with
        {
            Server = server,
            Port = AppStartupConfig.FixedSipPort,
            Domain = server,
            Extension = ExtensionTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            AudioInput = mediaConfig.AudioInput,
            AudioOutput = mediaConfig.AudioOutput,
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
        var ringtone = RingtoneComboBox.SelectedItem as RingtoneChoice;
        return _config with
        {
            Server = _config.AllowsCustomSipEndpoint
                ? PrivatePbxSettingsTextBox.Text.Trim()
                : AppStartupConfig.FixedSipServer,
            Domain = _config.AllowsCustomSipEndpoint
                ? PrivatePbxSettingsTextBox.Text.Trim()
                : AppStartupConfig.FixedSipServer,
            AudioInput = audioInput,
            AudioOutput = audioOutput,
            Ringtone = ringtone?.Id ?? _config.Ringtone,
            MicrophoneVolume = Math.Clamp(MicrophoneVolumeSlider.Value / 100, 0.25, 2.0),
            HeadphoneVolume = Math.Clamp(HeadphoneVolumeSlider.Value / 100, 0.25, 2.0),
            SipAlgCompatibilityMode = SipAlgCompatibilityCheckBox.IsChecked == true,
            IgnoreSslErrors = IgnoreSslErrorsCheckBox.IsChecked == true,
            SipSignallingTransport = ComboBoxTag(SipTransportModeComboBox, AppStartupConfig.TransportUdp)
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

    private async void ProvisionAndReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        ProvisionAndReconnectButton.IsEnabled = false;
        FooterStatusText.Text = "Applying account setup.";

        try
        {
            var audioInput = AudioInputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioInput;
            var audioOutput = AudioOutputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioOutput;
            var ringtone = RingtoneComboBox.SelectedItem as RingtoneChoice;

            var result = await _provisioningService.ProvisionAsync(
                SettingsProvisioningCodeTextBox.Text,
                _config.LicenseKey,
                _config.LicenseStatus,
                _config.LicenseLocalKey,
                audioInput,
                audioOutput);

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
                SipAlgCompatibilityMode = SipAlgCompatibilityCheckBox.IsChecked == true,
                SipSignallingTransport = ComboBoxTag(SipTransportModeComboBox, AppStartupConfig.TransportUdp)
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

    private void ShowDirectoryFavoritesButton_Click(object sender, RoutedEventArgs e)
    {
        DirectoryTabs.SelectedItem = DirectoryFavoritesTab;
    }

    private void FavoritesListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DialFavoritesListView.SelectedItem is ContactEntry contact)
        {
            UseSelectedContact(contact);
        }
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
        UpdateActiveSettingsTab(TabBtnAccount);
        await RefreshConnectionDiagnosticsAsync();
        await EnsureConnectionReadyAsync();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateActiveSettingsTab(System.Windows.Controls.Button activeBtn)
    {
        var activeBg = (System.Windows.Media.Brush)this.FindResource("PrimaryBrush")!;
        var activeFg = System.Windows.Media.Brushes.White;
        var inactiveBg = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9")!;
        var inactiveFg = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#475569")!;

        foreach (var btn in new[] { TabBtnGeneral, TabBtnAccount, TabBtnHandling, TabBtnAudio, TabBtnDiagnostics, TabBtnAbout })
        {
            if (btn == activeBtn)
            {
                btn.Background = activeBg;
                btn.Foreground = activeFg;
            }
            else
            {
                btn.Background = inactiveBg;
                btn.Foreground = inactiveFg;
            }
        }
    }

    private async void SettingsAccountButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsAccountTab;
        UpdateActiveSettingsTab(TabBtnAccount);
        await RefreshConnectionDiagnosticsAsync();
        await EnsureConnectionReadyAsync();
    }

    private void SettingsGeneralButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsGeneralTab;
        UpdateActiveSettingsTab(TabBtnGeneral);
    }

    private void SettingsHandlingButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsHandlingTab;
        UpdateActiveSettingsTab(TabBtnHandling);
    }

    private void SettingsAudioButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsAudioTab;
        UpdateActiveSettingsTab(TabBtnAudio);
    }

    private void SettingsDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsDiagnosticsTab;
        UpdateActiveSettingsTab(TabBtnDiagnostics);
    }

    private void SettingsAboutButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsAboutTab;
        UpdateActiveSettingsTab(TabBtnAbout);
    }

    private void SipAlgCompatibilityCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateNetworkAssistanceText();
    }

    private void IgnoreSslErrorsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
    }

    private void SipTransportModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateNetworkAssistanceText();
    }

    private void UpdateNetworkAssistanceText()
    {
        var compatibilityOn = SipAlgCompatibilityCheckBox.IsChecked == true;
        var transport = ComboBoxTag(SipTransportModeComboBox, AppStartupConfig.TransportUdp);
        var tcpMode = string.Equals(transport, AppStartupConfig.TransportTcp, StringComparison.OrdinalIgnoreCase);
        var tlsMode = string.Equals(transport, AppStartupConfig.TransportTls, StringComparison.OrdinalIgnoreCase);

        NatKeepaliveStatusText.Text = compatibilityOn ? "On" : "Off";
        NatKeepaliveStatusText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(compatibilityOn ? "#106247" : "#64748B")!;
        RportStatusText.Text = tlsMode ? "TLS" : tcpMode ? "TCP" : "On";
        AutoRecoveryStatusText.Text = "On";
    }

    private async void RunPbxDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunNetworkDiagnosticsAsync();
    }

    private async Task RunNetworkDiagnosticsAsync()
    {
        if (_networkDiagnosticsRunning)
        {
            return;
        }

        _networkDiagnosticsRunning = true;
        _networkDiagnosticsProgress.Clear();
        RunPbxDiagnosticsButton.IsEnabled = false;
        RunPbxDiagnosticsButton.Content = "Running";
        PbxDiagnosticsText.Text = "1/10 DNS Resolution - RUNNING";
        FooterStatusText.Text = "Running network diagnostics in the background.";
        DebugLog.Write("Network diagnostics started from Settings.");

        var engine = new NetworkEngine
        {
            SelectedTests = [true, true, true, true, true, true, true, true, true, true]
        };
        _activeNetworkDiagnostics = engine;
        int? finalScore = null;

        engine.OnLog += (message, isError) =>
        {
            DebugLog.Write(isError ? $"NETWORK DIAGNOSTICS ERROR: {message}" : $"NETWORK DIAGNOSTICS: {message}");
        };
        engine.OnProgress += (testName, status, details) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                UpdateNetworkDiagnosticsProgress(testName, status, details);
            }, DispatcherPriority.Background);
        };
        engine.OnComplete += (_, score) =>
        {
            finalScore = score;
        };

        try
        {
            var passed = await Task.Run(async () => await engine.RunDiagnosticsAsync());
            PbxDiagnosticsText.Text = BuildNetworkDiagnosticsSummary(passed, finalScore);
            FooterStatusText.Text = passed
                ? "Network diagnostics complete."
                : "Network diagnostics found issues.";
            DebugLog.Write($"Network diagnostics completed. Passed={passed}. Score={finalScore?.ToString() ?? "n/a"}.");
        }
        catch (Exception error)
        {
            DebugLog.Write($"Network diagnostics failed: {error}");
            PbxDiagnosticsText.Text = "Network diagnostics failed. Check debug.log for details.";
            FooterStatusText.Text = "Network diagnostics failed.";
        }
        finally
        {
            _activeNetworkDiagnostics = null;
            _networkDiagnosticsRunning = false;
            RunPbxDiagnosticsButton.Content = "Run test";
            RunPbxDiagnosticsButton.IsEnabled = true;
        }
    }

    private void UpdateNetworkDiagnosticsProgress(string testName, string status, string details)
    {
        var line = $"{testName}: {status}. {details}";
        var existingIndex = _networkDiagnosticsProgress.FindIndex(item => item.StartsWith($"{testName}:", StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _networkDiagnosticsProgress[existingIndex] = line;
        }
        else
        {
            _networkDiagnosticsProgress.Add(line);
        }

        var displayTest = GetNetworkDiagnosticDisplayTest(testName);
        PbxDiagnosticsText.Text = $"{displayTest.Number}/10 {displayTest.Name} - {GetNetworkDiagnosticDisplayStatus(status)}";
    }

    private static (int Number, string Name) GetNetworkDiagnosticDisplayTest(string testName)
    {
        return testName switch
        {
            "DNS Domain & Resolution Check" => (1, "DNS Resolution"),
            "HTTP/HTTPS Outbound Probes" => (2, "Web Connectivity"),
            "NTP Subsystem (UDP 123)" => (3, "Time Sync"),
            "Primary STUN Servers" => (4, "PBX Reachability"),
            "Google STUN Servers" => (5, "Public STUN"),
            "NAT Routing & Hops Check" => (6, "NAT Routing"),
            "NAT Port Translation (Random Port)" => (7, "NAT Port Mapping"),
            "SIP ALG Detection" => (8, "SIP ALG"),
            "RTP Jitter/Loss Check" => (9, "Media Quality"),
            "Inbound Signalling & Presence" => (10, "Signalling Reachability"),
            _ => (1, "Network Check")
        };
    }

    private static string GetNetworkDiagnosticDisplayStatus(string status)
    {
        return status switch
        {
            "Running" => "RUNNING",
            "Passed" or "Pass" => "PASSED",
            "Failed" or "Fail" => "FAILED",
            "Skipped" => "SKIPPED",
            _ => status.ToUpperInvariant()
        };
    }

    private string BuildNetworkDiagnosticsSummary(bool passed, int? score)
    {
        var report = new StringBuilder();
        report.AppendLine(passed
            ? "Network diagnostics complete: Passed."
            : "Network diagnostics complete: Issues found.");
        if (score.HasValue)
        {
            report.AppendLine($"Weighted score: {score}/100.");
        }
        report.AppendLine("Full details are available in the diagnostic log.");
        return report.ToString().Trim();
    }




    private async void SaveAllSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var previousInput = _config.AudioInput;
        var previousOutput = _config.AudioOutput;
        var previousTransport = _config.SipSignallingTransport;
        var previousExtension = _config.Extension;
        var previousUsername = _config.Username;
        var previousPassword = _config.Password;
        var previousServer = _config.Server;

        var audioInput = AudioInputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioInput;
        var audioOutput = AudioOutputComboBox.SelectedItem as MediaDeviceInfo ?? _config.AudioOutput;
        var ringtone = RingtoneComboBox.SelectedItem as RingtoneChoice;

        var server = _config.AllowsCustomSipEndpoint
            ? PrivatePbxSettingsTextBox.Text.Trim()
            : AppStartupConfig.FixedSipServer;

        _config = _config with
        {
            Server = server,
            Port = AppStartupConfig.FixedSipPort,
            Domain = server,
            Extension = ExtensionTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordBox.Password,
            AudioInput = audioInput,
            AudioOutput = audioOutput,
            Ringtone = ringtone?.Id ?? _config.Ringtone,
            MicrophoneVolume = Math.Clamp(MicrophoneVolumeSlider.Value / 100, 0.25, 2.0),
            HeadphoneVolume = Math.Clamp(HeadphoneVolumeSlider.Value / 100, 0.25, 2.0),
            SipAlgCompatibilityMode = SipAlgCompatibilityCheckBox.IsChecked == true,
            SipSignallingTransport = ComboBoxTag(SipTransportModeComboBox, AppStartupConfig.TransportUdp),
            MobileNumber = MobileNumberTextBox.Text.Trim(),
            DndMode = ComboBoxText(DndModeComboBox, "Off"),
            DeclineIncomingAction = ComboBoxText(DeclineActionComboBox, "Send busy"),
            InternalBusyAction = ComboBoxText(InternalBusyActionComboBox, "Send busy"),
            InternalNoAnswerSeconds = ComboBoxSeconds(InternalNoAnswerTimeoutComboBox, 90),
            InternalNoAnswerAction = "Send busy",
            ExternalBusyAction = ComboBoxText(ExternalBusyActionComboBox, "Send busy"),
            ExternalNoAnswerSeconds = ComboBoxSeconds(ExternalNoAnswerTimeoutComboBox, 90),
            ExternalNoAnswerAction = "Send busy",
            QueuePickupEnabled = QueuePickupCheckBox.IsChecked == true,
            CombineContactsInSearch = CombineContactsCheckBox.IsChecked == true,
            FailedCallDisplaySeconds = ComboBoxSeconds(FailedCallTimeoutComboBox, 5)
        };

        await _cacheService.SaveSettingsAsync(_config.WithFixedSipEndpoint());
        ApplyDndMode();
        _sipRegistrationService.UpdateNetworkAssistance(_config.SipAlgCompatibilityMode);
        UpdateNetworkAssistanceText();

        var mediaDeviceChanged = previousInput.Id != _config.AudioInput.Id || previousOutput.Id != _config.AudioOutput.Id;
        var transportChanged = !string.Equals(previousTransport, _config.SipSignallingTransport, StringComparison.OrdinalIgnoreCase);
        var credentialsChanged = !string.Equals(previousExtension, _config.Extension) ||
                                 !string.Equals(previousUsername, _config.Username) ||
                                 !string.Equals(previousPassword, _config.Password) ||
                                 !string.Equals(previousServer, _config.Server);

        SettingsOverlay.Visibility = Visibility.Collapsed;
        FooterStatusText.Text = "Settings saved successfully.";

        if (transportChanged || credentialsChanged)
        {
            _registered = false;
            UpdateCallControls();
            await RegisterSipAsync();
        }
        else if (mediaDeviceChanged)
        {
            UpdateCallControls();
        }
    }

    private void QueuePickupButton_Click(object sender, RoutedEventArgs e)
    {
        if (QueuePickupCheckBox.IsChecked != true)
        {
            FooterStatusText.Text = "Enable queue pickup first.";
            return;
        }

        DestinationTextBox.Text = "*8";
        MainTabs.SelectedItem = PhoneTab;
        DialButton_Click(sender, e);
    }

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_callHistory.Count == 0)
        {
            FooterStatusText.Text = "Call history is already empty.";
            return;
        }

        if (!ConfirmDialogWindow.Confirm(this, "Clear call history", "Clear all call history?", "Clear history"))
        {
            return;
        }

        _callHistory.Clear();
        await _callHistoryStore.SaveAsync(_callHistory);
        ApplyGlobalSearchFilter();
        FooterStatusText.Text = "Call history cleared.";
    }

    private void DiagnosticsSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiagnosticsSummaryDialog();
        dialog.Owner = this;
        dialog.ShowDialog();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates...";

        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            UpdateStatusText.Text = result.Message;
            UpdateNotesText.Text = result.Notes ?? string.Empty;
            FooterStatusText.Text = result.Message;

            if (result.UpdateAvailable && !string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                var notes = string.IsNullOrWhiteSpace(result.Notes) ? "" : $"\n\n{result.Notes}";
                var install = ConfirmDialogWindow.Confirm(
                    this,
                    "Update available",
                    $"{result.Message}{notes}\n\nDownload and install now?",
                    "Install update",
                    "Not now");

                if (install)
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
                        StartInstallerAndRestart(installerPath);
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
        }
        catch (Exception error)
        {
            DebugLog.Write($"UPDATE CHECK UI failed error={error.Message}");
            UpdateStatusText.Text = "Unable to check for updates right now.";
            FooterStatusText.Text = UpdateStatusText.Text;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_startupUpdateCheckCompleted)
        {
            return;
        }

        _startupUpdateCheckCompleted = true;
        await Task.Delay(TimeSpan.FromSeconds(2));

        var result = await _updateService.CheckForUpdatesAsync();
        if (!result.UpdateAvailable || string.IsNullOrWhiteSpace(result.DownloadUrl))
        {
            return;
        }

        UpdateStatusText.Text = result.Message;
        UpdateNotesText.Text = result.Notes ?? string.Empty;
        FooterStatusText.Text = "A Merlin SIP update is available.";

        var notes = string.IsNullOrWhiteSpace(result.Notes) ? "" : $"\n\n{result.Notes}";
        var install = ConfirmDialogWindow.Confirm(
            this,
            "Merlin SIP update available",
            $"{result.Message}{notes}\n\nInstall this update now?",
            "Install update",
            "Not now");

        if (install)
        {
            try
            {
                var progress = new Progress<int>(percent => { FooterStatusText.Text = $"Downloading update... {percent}%"; });
                var installerPath = await _updateService.DownloadInstallerAsync(result, progress);
                StartInstallerAndRestart(installerPath);
                System.Windows.Application.Current.Shutdown();
            }
            catch (Exception error)
            {
                DebugLog.Write($"STARTUP UPDATE failed error={error.Message}");
                FooterStatusText.Text = "Unable to start the update right now.";
            }
        }
    }

    private static void StartInstallerAndRestart(string installerPath)
    {
        WindowsStartupService.QueueLaunchAfterUpdate();
        var executablePath = WindowsStartupService.GetExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{installerPath}\"")
            {
                UseShellExecute = true
            });
            return;
        }

        var command = $"/c start /wait \"\" msiexec.exe /i \"{installerPath}\" && start \"\" \"{executablePath}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", command)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private void UpdateCallControls()
    {
        var hasDestination = !string.IsNullOrWhiteSpace(DestinationTextBox.Text);
        DialButton.IsEnabled = !_licenseLocked && _registered && hasDestination && !_callInProgress && !_incomingRinging;
        HangupButton.IsEnabled = !_licenseLocked && (_callInProgress || _incomingRinging);
        MuteButton.IsEnabled = !_licenseLocked && _callConnected && _sipRegistrationService.CanControlAudio;
        HoldButton.IsEnabled = !_licenseLocked && _callConnected && _sipRegistrationService.CanControlAudio;
        ConferenceButton.IsEnabled = !_licenseLocked && _callConnected && _sipRegistrationService.CanControlAudio;
        TransferButton.IsEnabled = !_licenseLocked && _callConnected;
        DndButton.IsEnabled = !_licenseLocked;

        var activeCall = _callInProgress || _incomingRinging || _callConnected;

        if (activeCall)
        {
            DialButton.Visibility = Visibility.Collapsed;
            HangupButton.Visibility = Visibility.Visible;
            DialButtonColumn.Width = new GridLength(0);
            HangupButtonColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            DialButton.Visibility = Visibility.Visible;
            HangupButton.Visibility = Visibility.Collapsed;
            DialButtonColumn.Width = new GridLength(1, GridUnitType.Star);
            HangupButtonColumn.Width = new GridLength(0);
        }

        if (activeCall)
        {
            InCallButtonsGrid.Visibility = Visibility.Visible;
            DndButton.Visibility = Visibility.Collapsed;

            DestinationPreviewText.FontSize = 36;
            CallerLookupText.FontSize = 15;
        }
        else
        {
            InCallButtonsGrid.Visibility = Visibility.Collapsed;
            DndButton.Visibility = Visibility.Collapsed;

            DestinationPreviewText.FontSize = 28;
            CallerLookupText.FontSize = 13;
        }
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
        if (!ConfirmDialogWindow.Confirm(
            this,
            "Reset application",
            "Clear saved account settings, contacts, and call history? Merlin SIP will close so setup can run again next time.",
            "Reset application"))
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

    private async Task OnCallFinished(string resultState, string message)
    {
        HideIncomingCallSurfaces();
        StopLocalRingback();
        _ringtonePlayer.Stop();
        
        _incomingRinging = false;
        _callInProgress = false;
        _callConnected = false;
        _muted = false;
        _held = false;
        _sipRegistrationService.SetMuted(false);
        _sipRegistrationService.SetHeldLocal(false);
        MuteButton.Content = "Mute";
        HoldButton.Content = "Hold";

        StopCallTimer();
        ClearDialpadAfterCall();

        var number = _activeRemoteNumber;
        var direction = _activeCallDirection;
        var startAt = _activeCallStartedAt;

        _activeCallStartedAt = null;
        _activeRemoteNumber = "";
        
        if (!string.IsNullOrWhiteSpace(number))
        {
            // Let BLF system handle contact presence updates naturally
            var contact = _contactStore.FindByNumber(_contacts, number);
            var name = contact?.Name ?? number;
            await AddCallHistory(direction, name, number, resultState, message, startAt);
        }

        // Restore user's selected presence after call ends
        SetPresenceDisplay(_userSelectedPresence);
        UpdateCallControls();
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
        ApplyGlobalSearchFilter();
    }

    private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
    {
        await SendCurrentMessageAsync();
    }

    private async Task SendCurrentMessageAsync()
    {
        if (!SendMessageButton.IsEnabled)
        {
            return;
        }

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
                MessageBodyTextBox.Focus();
            }
        }
        finally
        {
            SendMessageButton.IsEnabled = true;
        }
    }

    private void MessageBodyTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendCurrentMessageAsync();
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
            ChatThreadSubtitleText.Text = "";
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
        if (!ConfirmDialogWindow.Confirm(
            this,
            "Clear conversation",
            $"Clear message history with {displayName}?",
            "Clear chat"))
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

    private void ContactTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveContactButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
