using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MSFS2024AIPilot.Core.Atc;

/// <summary>
/// MSFS 2024 exposes no SimConnect event that opens the ATC menu — ATC_MENU_OPEN /
/// KEY_ATC_MENU_OPEN are localization strings only, and ATC_MENU_n presses are ignored
/// while the menu is closed. The only reliable way to raise the menu is the bound
/// keyboard key, so this injects a key press into the simulator window via Win32
/// SendInput. Scroll Lock is the conventional ATC binding; it is easily changed in
/// the sim's controls options.
/// </summary>
public static class AtcKeyInjector
{
    private const ushort VkScrollLock = 0x91;

    public static bool PressAtcKey()
    {
        var hwnd = FindSimWindow();
        if (hwnd == IntPtr.Zero) return false;

        var foreground = GetForegroundWindow();
        var simThread = GetWindowThreadProcessId(hwnd, out _);
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);

        // Attach to the foreground thread's input queue so SetForegroundWindow is
        // permitted from a background process.
        if (foregroundThread != simThread && foregroundThread != 0)
            AttachThreadInput(GetCurrentThreadId(), foregroundThread, true);
        AttachThreadInput(GetCurrentThreadId(), simThread, true);
        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            Thread.Sleep(80);

            var inputs = new[]
            {
                new INPUT { type = InputKeyboard, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VkScrollLock } } },
                new INPUT { type = InputKeyboard, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VkScrollLock, dwFlags = KeyEventKeyUp } } },
            };
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
        }
        finally
        {
            AttachThreadInput(GetCurrentThreadId(), simThread, false);
            if (foregroundThread != simThread && foregroundThread != 0)
                AttachThreadInput(GetCurrentThreadId(), foregroundThread, false);
        }
    }

    private static IntPtr FindSimWindow()
    {
        foreach (var p in Process.GetProcessesByName("FlightSimulator2024"))
        {
            if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
        }
        // Fallback: any top-level window owned by a simulator process.
        foreach (var p in Process.GetProcessesByName("FlightSimulator2024"))
        {
            var hwnd = FindWindowForProcess(p.Id);
            if (hwnd != IntPtr.Zero) return hwnd;
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindWindowForProcess(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid == pid && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const int SwRestore = 9;

    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
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
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
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
}
