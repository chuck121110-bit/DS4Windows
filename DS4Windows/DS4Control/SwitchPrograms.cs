using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    // Switch Programs: a NEW special action, separate from Cycle
    // Programs, which flashes the Alt-Tab menu for a split second and
    // closes because it releases Alt immediately. Switch Programs
    // actually holds Alt: Alt down, Tab tap once, Alt stays down until
    // the user clicks something, switches to / focuses a window, presses
    // the action again, presses Esc, or a 30 second safety timeout.
    // The timeout is a safety net only, so Alt can never stay stuck if
    // the user walks away; it is not part of the requested behavior.
    // If DS4 stops or the process exits, the virtual device goes away
    // with it, so Alt cannot stay stuck in Windows either.
    public static class SwitchPrograms
    {
        private static readonly object gate = new object();
        private static readonly bool[] held = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static Action<uint> pressKey;
        private static Action<uint> releaseKey;
        private static uint altKey;
        private static uint tabKey;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;
        private const int VK_MBUTTON = 0x04;
        private const int VK_ESCAPE = 0x1B;

        public static bool IsHeld(int device) =>
            device >= 0 && device < held.Length && held[device];

        public static void Toggle(int device, Action<uint> press, Action<uint> release, uint alt, uint tab)
        {
            if (device < 0 || device >= held.Length)
                return;
            lock (gate)
            {
                pressKey = press;
                releaseKey = release;
                altKey = alt;
                tabKey = tab;
                if (held[device])
                {
                    ReleaseAlt(device);
                    return;
                }
                held[device] = true;
            }
            // Alt down, Tab tap, Alt stays down. Run on a background
            // task so the down state is synced across mapping ticks.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    pressKey?.Invoke(altKey);
                    Thread.Sleep(30);
                    pressKey?.Invoke(tabKey);
                    Thread.Sleep(50);
                    releaseKey?.Invoke(tabKey);
                }
                catch { }
                StartWatchdog(device);
            });
        }

        private static void ReleaseAlt(int device)
        {
            // Called with gate held, or from the watchdog thread which
            // takes the gate first. Release once, then clear.
            if (device < 0 || device >= held.Length)
                return;
            if (!held[device])
                return;
            held[device] = false;
            try { releaseKey?.Invoke(altKey); } catch { }
        }

        private static void StartWatchdog(int device)
        {
            Thread watchdog = new Thread(() =>
            {
                try
                {
                    DateTime start = DateTime.UtcNow;
                    bool prevMouseDown = false;
                    bool prevEscDown = false;
                    // Grace: ignore clicks / focus changes in the first
                    // 500 ms, so the Tab tap itself and the switcher
                    // appearing do not immediately release Alt. The
                    // baseline window is captured AFTER the grace, so
                    // the switcher itself (if it becomes foreground)
                    // is the baseline, not a change away from it.
                    Thread.Sleep(500);
                    IntPtr startWindow = IntPtr.Zero;
                    try { startWindow = GetForegroundWindow(); } catch { }
                    // Baseline the mouse / Esc state too, so a button
                    // already held before the grace ends is not read
                    // as a new click.
                    try
                    {
                        prevMouseDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 ||
                            (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 ||
                            (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
                        prevEscDown = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
                    }
                    catch { }
                    while (true)
                    {
                        lock (gate)
                        {
                            if (!held[device])
                                return;
                        }
                        if ((DateTime.UtcNow - start).TotalSeconds >= 30)
                        {
                            lock (gate) { ReleaseAlt(device); }
                            return;
                        }
                        bool mouseDown = false;
                        try
                        {
                            mouseDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 ||
                                (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 ||
                                (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
                        }
                        catch { }
                        bool escDown = false;
                        try { escDown = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0; } catch { }
                        bool focusChanged = false;
                        try
                        {
                            IntPtr nowWindow = GetForegroundWindow();
                            focusChanged = nowWindow != IntPtr.Zero && nowWindow != startWindow;
                        }
                        catch { }
                        // New click edge, Esc edge, or focus change.
                        if ((mouseDown && !prevMouseDown) || (escDown && !prevEscDown) || focusChanged)
                        {
                            lock (gate) { ReleaseAlt(device); }
                            return;
                        }
                        prevMouseDown = mouseDown;
                        prevEscDown = escDown;
                        Thread.Sleep(50);
                    }
                }
                catch { }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
        }
    }
}
