using System.Runtime.InteropServices;

namespace WhisperKeyboard;

public static class WindowsInputSender
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf(typeof(INPUT));
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    public static void SendText(string text, int delayMs = 0, bool useVirtualSpaceKey = false, int virtualSpaceHoldMs = 5)
    {
        if (delayMs > 0)
        {
            // Per-character mode: send each char with a delay between.
            var pair = new INPUT[2];
            foreach (char c in text)
            {
                if (useVirtualSpaceKey && c == ' ')
                {
                    SendVirtualSpace(virtualSpaceHoldMs);
                }
                else
                {
                    pair[0] = MakeUnicodeInput(c, false);
                    pair[1] = MakeUnicodeInput(c, true);
                    SendInput(2, pair, INPUT.Size);
                }
                Thread.Sleep(delayMs);
            }
            return;
        }

        var inputs = new List<INPUT>();

        foreach (char c in text)
        {
            if (useVirtualSpaceKey && c == ' ')
            {
                // Flush any accumulated Unicode chars first.
                if (inputs.Count > 0)
                {
                    SendInput((uint)inputs.Count, inputs.ToArray(), INPUT.Size);
                    inputs.Clear();
                }
                SendVirtualSpace(virtualSpaceHoldMs);
            }
            else
            {
                inputs.Add(MakeUnicodeInput(c, false));
                inputs.Add(MakeUnicodeInput(c, true));
            }
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), INPUT.Size);
        }
    }

    private static void SendVirtualSpace(int holdMs)
    {
        // Send VK_SPACE down and up as TWO separate SendInput calls with a hold time.
        // Some terminal apps (Claude Code in Windows Terminal, etc.) miss the keypress when
        // down and up arrive in the same call or within microseconds of each other.
        var down = new INPUT[1] { MakeKeyInput(0x20, false) };
        var up = new INPUT[1] { MakeKeyInput(0x20, true) };
        SendInput(1, down, INPUT.Size);
        if (holdMs > 0) Thread.Sleep(holdMs);
        SendInput(1, up, INPUT.Size);
    }

    private static INPUT MakeUnicodeInput(char c, bool up)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = c,
                    dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public static void SendEnter()
    {
        // VK_RETURN = 0x0D
        SendKey(0x0D);
    }

    public static void SendPaste()
    {
        // VK_CONTROL = 0x11, V = 0x56
        // Sent as separate calls with short gaps: some apps (browsers included) can miss
        // a Ctrl+V whose four events arrive in the same instant, the same way terminals
        // miss a space whose down and up arrive together (see SendVirtualSpace).
        bool ok = SendOne(MakeKeyInput(0x11, false));
        Thread.Sleep(15);
        ok &= SendOne(MakeKeyInput(0x56, false));
        Thread.Sleep(15);
        ok &= SendOne(MakeKeyInput(0x56, true));
        Thread.Sleep(15);
        ok &= SendOne(MakeKeyInput(0x11, true));

        if (!ok)
        {
            // ERROR_ACCESS_DENIED (5) here means the focused window runs as administrator,
            // which Windows won't let a normal app send keystrokes to.
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WindowsInputSender: SendInput for Ctrl+V was blocked (error {Marshal.GetLastWin32Error()})");
        }
    }

    private static bool SendOne(INPUT input)
    {
        return SendInput(1, new[] { input }, INPUT.Size) == 1;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // VK_SHIFT, VK_CONTROL, VK_MENU (Alt), VK_LWIN, VK_RWIN
    private static readonly int[] ModifierVks = { 0x10, 0x11, 0x12, 0x5B, 0x5C };

    /// <summary>
    /// Waits until no modifier key is held (or the timeout passes). Push-to-talk stops
    /// when either of its keys is released, so the other may still be down; a paste or
    /// typed text sent then turns into Ctrl+Alt+V or a shortcut and goes nowhere.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        bool logged = false;
        while (ModifierVks.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0))
        {
            if (!logged)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WindowsInputSender: waiting for modifier keys to be released");
                logged = true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] WindowsInputSender: modifier keys still held after {timeout.TotalMilliseconds:F0} ms - sending anyway");
                return;
            }
            await Task.Delay(20);
        }
    }

    private static void SendKey(ushort vk)
    {
        var inputs = new INPUT[2];
        
        // Down
        inputs[0] = MakeKeyInput(vk, false);
        // Up
        inputs[1] = MakeKeyInput(vk, true);

        SendInput((uint)inputs.Length, inputs, INPUT.Size);
    }

    private static INPUT MakeKeyInput(ushort vk, bool up)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = up ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}
