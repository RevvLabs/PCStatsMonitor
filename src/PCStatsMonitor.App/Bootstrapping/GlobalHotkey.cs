using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PCStatsMonitor.App.Bootstrapping;

/// <summary>
/// System-wide Alt+M hotkey that works even while a game has focus.
/// RegisterHotKey with a null HWND posts WM_HOTKEY to the registering thread's
/// message queue, so no window is needed — but registration, the message loop,
/// and unregistration must all happen on that same dedicated thread.
/// If the combo is already taken by another app, registration fails and the
/// hotkey is silently unavailable for this session.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000; // no auto-repeat while held down
    private const uint VkM = 0x4D;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    private readonly Action _pressed;
    private volatile uint _threadId;

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        new Thread(MessageLoop) { IsBackground = true, Name = "GlobalHotkey" }.Start();
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, ModAlt | ModNoRepeat, VkM))
            return;
        try
        {
            // Returns 0 on WM_QUIT, -1 on error — both end the loop
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmHotkey && (int)msg.wParam == HotkeyId)
                {
                    try { _pressed(); } catch { /* callback must never kill the loop */ }
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        }
    }

    public void Dispose()
    {
        uint tid = _threadId;
        if (tid != 0)
            PostThreadMessage(tid, WmQuit, UIntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
