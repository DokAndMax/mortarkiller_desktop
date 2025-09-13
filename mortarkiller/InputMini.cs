using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace mortarkiller;

public static class InputMini
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint uCode, uint uMapType);

    const int SW_RESTORE = 9;
    const uint MAPVK_VK_TO_VSC = 0;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    const int VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    const byte VK_M = 0x4D, VK_SPACE = 0x20;

    // Фіксовані SC (US): M=0x32, Space=0x39
    const byte SC_M_US = 0x32, SC_SPACE = 0x39;

    static long _lastPressM, _lastPressSpace;
    const int DEBOUNCE_MS = 120;

    // Опційно: фокус вікна гри (викликайте ззовні перед натисканням)
    public static bool FocusProcess(string name)
    {
        var p = Process.GetProcessesByName(name).LastOrDefault(x => x.MainWindowHandle != nint.Zero);
        if (p == null) return false;
        ShowWindow(p.MainWindowHandle, SW_RESTORE);
        return SetForegroundWindow(p.MainWindowHandle);
    }

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    static void ReleaseMods()
    {
        if (IsDown(VK_LMENU)) keybd_event(VK_LMENU, 0, KEYEVENTF_KEYUP, nuint.Zero);
        if (IsDown(VK_RMENU)) keybd_event(VK_RMENU, 0, KEYEVENTF_KEYUP, nuint.Zero);
        if (IsDown(VK_MENU)) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, nuint.Zero);
        if (IsDown(VK_SHIFT)) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, nuint.Zero);
        if (IsDown(VK_CONTROL)) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, nuint.Zero);
    }

    // Один “клік” M (без фокусу всередині)
    public static void PressM_KeybdEvent(bool useFixedUSScan = false, bool releaseModifiers = true)
    {
        long now = Environment.TickCount64;
        if (now - _lastPressM < DEBOUNCE_MS) return;
        _lastPressM = now;

        if (releaseModifiers) ReleaseMods();
        Thread.Sleep(10);

        byte sc = useFixedUSScan ? SC_M_US : (byte)MapVirtualKey(VK_M, MAPVK_VK_TO_VSC);
        if (sc == 0) sc = SC_M_US;

        keybd_event(0, sc, KEYEVENTF_SCANCODE, nuint.Zero);
        Thread.Sleep(3);
        keybd_event(0, sc, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, nuint.Zero);
    }

    // Один “клік” Space (без фокусу всередині)
    public static void PressSpace_KeybdEvent(bool useFixedUSScan = true, bool releaseModifiers = true)
    {
        long now = Environment.TickCount64;
        if (now - _lastPressSpace < DEBOUNCE_MS) return;
        _lastPressSpace = now;

        if (releaseModifiers) ReleaseMods();
        Thread.Sleep(10);

        byte sc = useFixedUSScan ? SC_SPACE : (byte)MapVirtualKey(VK_SPACE, MAPVK_VK_TO_VSC);
        if (sc == 0) sc = SC_SPACE;

        keybd_event(0, sc, KEYEVENTF_SCANCODE, nuint.Zero);
        Thread.Sleep(3);
        keybd_event(0, sc, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, nuint.Zero);
    }
}