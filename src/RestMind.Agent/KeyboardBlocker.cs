using System.Runtime.Versioning;

namespace RestMind.Agent;

/// <summary>
/// Swallows the key combinations that would otherwise dismiss or step around the overlay,
/// using a low-level keyboard hook so the shell never sees them.
/// </summary>
/// <remarks>
/// Ctrl+Alt+Del cannot be intercepted: it is handled on the secure desktop, above anything a
/// standard-user process can reach. That is fine by design. Signing out or rebooting from
/// there does not shorten the break, because the break deadline is wall-clock and lives in the
/// service, and the overlay reappears at the next sign-in.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class KeyboardBlocker : IDisposable
{
    // The delegate must be held alive for as long as the hook, or it gets collected and the
    // callback crashes the process.
    private readonly NativeMethods.HookProc _callback;
    private IntPtr _hook = IntPtr.Zero;

    public KeyboardBlocker() => _callback = OnKey;

    /// <summary>When false the hook passes everything through.</summary>
    public bool Blocking { get; set; }

    public void Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || !Blocking)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        var message = (int)wParam;
        if (message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        {
            var data = System.Runtime.InteropServices.Marshal
                .PtrToStructure<NativeMethods.KeyboardLowLevelHookStruct>(lParam);

            if (ShouldSwallow((int)data.vkCode))
            {
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool ShouldSwallow(int key)
    {
        var alt = IsDown(NativeMethods.VkMenu);
        var ctrl = IsDown(NativeMethods.VkControl);

        return key switch
        {
            NativeMethods.VkLwin or NativeMethods.VkRwin => true,
            NativeMethods.VkTab when alt => true,          // Alt+Tab
            NativeMethods.VkF4 when alt => true,           // Alt+F4
            NativeMethods.VkEscape when alt || ctrl => true, // Alt+Esc, Ctrl+Esc
            NativeMethods.VkSpace when alt => true,        // window menu
            _ => false,
        };
    }

    private static bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
}
