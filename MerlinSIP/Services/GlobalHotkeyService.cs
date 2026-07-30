using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MerlinSip.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312; // 786 in dec
    private const uint MOD_ALT = 1u;
    private const uint MOD_CONTROL = 2u;
    private const int HOTKEY_ANSWER = 9001;
    private const int HOTKEY_HOLD = 9002;
    private const int HOTKEY_TRANSFER = 9003;

    private readonly Window _window;
    private HwndSource? _hwndSource;
    private bool _registered;

    public event EventHandler? AnswerRequested;
    public event EventHandler? HoldRequested;
    public event EventHandler? TransferRequested;

    public GlobalHotkeyService(Window window)
    {
        _window = window;
    }

    public void Register()
    {
        if (!_registered)
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero)
            {
                _hwndSource = HwndSource.FromHwnd(handle);
                _hwndSource?.AddHook(HwndHook);
                RegisterHotKey(handle, HOTKEY_ANSWER, MOD_CONTROL | MOD_ALT, 0x41); // 'A'
                RegisterHotKey(handle, HOTKEY_HOLD, MOD_CONTROL | MOD_ALT, 0x48); // 'H'
                RegisterHotKey(handle, HOTKEY_TRANSFER, MOD_CONTROL | MOD_ALT, 0x54); // 'T'
                _registered = true;
                DebugLog.Write("Global hotkeys registered (Ctrl+Alt+A, Ctrl+Alt+H, Ctrl+Alt+T).");
            }
        }
    }

    public void Unregister()
    {
        if (_registered)
        {
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero)
            {
                UnregisterHotKey(handle, HOTKEY_ANSWER);
                UnregisterHotKey(handle, HOTKEY_HOLD);
                UnregisterHotKey(handle, HOTKEY_TRANSFER);
            }
            _hwndSource?.RemoveHook(HwndHook);
            _hwndSource = null;
            _registered = false;
            DebugLog.Write("Global hotkeys unregistered.");
        }
    }

    private nint HwndHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (((IntPtr)wParam).ToInt32())
            {
                case HOTKEY_ANSWER:
                    AnswerRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case HOTKEY_HOLD:
                    HoldRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
                case HOTKEY_TRANSFER:
                    TransferRequested?.Invoke(this, EventArgs.Empty);
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
