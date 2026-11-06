using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    // Switch Programs: a NEW special action, separate from Cycle
    // Programs, which flashes the Alt-Tab menu for a split second and
    // closes because it releases Alt immediately. Switch Programs
    // actually holds Alt: Alt down, Tab tap once to OPEN the menu,
    // Alt stays down until the user clicks something, switches to /
    // focuses a window, presses the action again, presses X, presses
    // Esc, or a 30 second safety timeout.
    // REPLACED 2026-11-04, per Chuck: the old Tab / Shift+Tab D-pad
    // navigation did not work, so it is removed. Navigation now uses
    // a tracked menu-open state (see menuOpen / IsMenuOpen below)
    // and D-pad Left / Right arrow keys, which move through the
    // modern Alt-Tab menu, while Alt is held. If DS4 stops or the
    // process exits, the virtual device goes away with it, so Alt
    // cannot stay stuck in Windows either.
    public static class SwitchPrograms
    {
        private static readonly object gate = new object();
        private static readonly bool[] held = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // Menu-open state tracker, per device, as Chuck specified:
        // set when the Switch Programs special action fires (menu
        // opened), cleared when X selects, when the action fires
        // again, on click, focus change, Esc, on the 30 second
        // timeout, or when the watchdog's constant check finds
        // the switcher window is no longer present. held[] IS
        // this flag: IsMenuOpen is the public name for it, so
        // Mapping and the watchdog share one single source of
        // truth, not two flags that can drift apart.
        private static readonly DateTime[] menuOpenedAt = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] menuSeen = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // Alt-Tab navigation state, per device. Edge-triggered, with
        // hold repeat, so a tap moves one window and holding a D-pad
        // direction keeps stepping, like a held Tab key autorepeats.
        private static readonly bool[] prevNavLeft = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] prevNavRight = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] prevNavCross = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // DS4 FIX 2026-11-04 (Switch Programs does nothing, regression):
        // The trigger IS Cross (Actions.xml: PS/Cross = SwitchPrograms),
        // and Cross is ALSO the select button. Toggle never armed Cross,
        // so the very tick Toggle set held=true, ProcessNavigation saw
        // Cross still down, prevNavCross=false, and logged CROSS-SELECT
        // and released Alt in the same tick. Menu never stayed open.
        // crossArmed is false on open, becomes true only after Cross
        // is released at least once since opening, so only a NEW Cross
        // press after release can select. The opening press is ignored.
        private static readonly bool[] crossArmed = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] crossIgnoreLogged = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly DateTime[] navLeftNext = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly DateTime[] navRightNext = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        // Arrow keys, as VirtualKBMMapping uint values, which are
        // the Windows VK codes: VK_LEFT 0x25, VK_RIGHT 0x27.
        // FakerInputMapping already maps VK Left / Right to
        // LeftArrow / RightArrow, the same pattern as KEY_TAB
        // 0x09 and KEY_LALT 0x12 already used by Toggle, and as
        // VK_SHIFT 0x10 was used by the old StepLeft. No named
        // arrow constant exists in VirtualKBMMapping, only
        // KEY_TAB / KEY_LALT, so these are defined here.
        private static Action<uint> pressKey;
        private static Action<uint> releaseKey;
        private static Action syncKeys;
        private static uint altKey;
        private static uint tabKey;
        private static uint leftKey;
        private static uint rightKey;
        // DS4 FIX 2026-11-04 (D-pad still dead after 20:50 arrow fix):
        // Diagnostic log, separate file, never breaks DS4. Proves
        // whether Toggle / navigation / watchdog actually ran.
        private static void AltTabLog(string msg)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "DS4Windows", "Logs");
                System.IO.Directory.CreateDirectory(dir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + msg + System.Environment.NewLine;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "alttab_log.txt"), line);
            }
            catch { }
        }
        // Synchronous arrow tap state: press in the MapCustom tick,
        // release on a later tick after 80 ms. The old ThreadPool
        // Step raced with the main tick Commit Sync and could not
        // be proven from any log.
        private static readonly uint[] pendingArrow = new uint[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly DateTime[] pendingArrowReleaseAt = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // Constant menu-open check, as Chuck specified: is the
        // Alt-Tab switcher window still present? Classic
        // switcher class is TaskSwitcherWnd, modern task view
        // frame is MultitaskingViewFrame. If the switcher was
        // seen at least once and then is absent for 10 polls
        // (500 ms at 50 ms), the menu is treated as closed.
        // If the modern switcher is never enumerable, menuSeen
        // stays false and this check never fires on its own,
        // so the other clears (X, second press, click, focus,
        // Esc, timeout) still work, with no false clear.
        private static bool SwitcherPresent()
        {
            bool found = false;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    var sb = new System.Text.StringBuilder(256);
                    if (GetClassName(hWnd, sb, sb.Capacity) == 0) return true;
                    string cls = sb.ToString();
                    if (cls == "TaskSwitcherWnd" || cls == "MultitaskingViewFrame") { found = true; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;
        private const int VK_MBUTTON = 0x04;
        private const int VK_ESCAPE = 0x1B;

        public static bool IsHeld(int device) => IsMenuOpen(device);

        // The menu-open state tracker, public name. True only
        // between Switch Programs firing and one of the clears
        // above. Mapping uses this, never a separate flag.
        public static bool IsMenuOpen(int device) =>
            device >= 0 && device < held.Length && held[device];

        public static DateTime MenuOpenedAt(int device) =>
            device >= 0 && device < menuOpenedAt.Length ? menuOpenedAt[device] : DateTime.MinValue;

        public static void Toggle(int device, Action<uint> press, Action<uint> release, Action sync, uint alt, uint tab, uint left, uint right)
        {
            if (device < 0 || device >= held.Length)
                return;
            lock (gate)
            {
                pressKey = press;
                releaseKey = release;
                syncKeys = sync;
                altKey = alt;
                tabKey = tab;
                leftKey = left;
                rightKey = right;
                if (held[device])
                {
                    AltTabLog("TOGGLE-OFF device=" + device);
                    ReleaseAlt(device);
                    return;
                }
                held[device] = true;
                pendingArrow[device] = 0;
                menuOpenedAt[device] = DateTime.UtcNow;
                menuSeen[device] = false;
                // Opening press is Cross itself: consume it, do not
                // treat it as a select. crossArmed only becomes true
                // after Cross is released (see ProcessNavigation).
                prevNavCross[device] = true;
                prevNavLeft[device] = false;
                prevNavRight[device] = false;
                crossArmed[device] = false;
                crossIgnoreLogged[device] = false;
                AltTabLog("TOGGLE-ON device=" + device);
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
                    Thread.Sleep(80);
                    releaseKey?.Invoke(tabKey);
                }
                catch { }
                StartWatchdog(device);
            });
        }

        // Alt-Tab navigation, called from Mapping.MapCustom once per
        // tick, ONLY while the menu-open state is true, i.e. only
        // while Switch Programs is holding Alt and the Alt-Tab menu
        // is open. It is never called when menu-open is false, so
        // D-pad / Cross keep their normal profile behavior at all
        // other times, and it never switches DS4 profiles: profile
        // switching is a separate action, untouched.
        // Right = Right arrow (forward, left to right in the menu).
        // Left = Left arrow (backward, right to left), NOT Tab and
        // NOT Shift+Tab: the old Tab method is removed, per Chuck,
        // because D-pad did not work with it anymore. Arrow keys
        // move through the modern Alt-Tab switcher (Windows 11
        // default) with Alt held. Plain caveat: in the classic
        // Alt-Tab switcher, arrow keys do not move, Tab /
        // Shift+Tab do, so if a user has forced the classic
        // switcher, arrows will not move; the default modern
        // switcher is what Chuck uses, and arrows are correct there.
        // Cross = select: release Alt, exactly like a click does in
        // the watchdog, so the highlighted window gains focus and
        // menu-open clears. The caller clears the consumed buttons
        // from the field maps, so Cross is not also sent to the
        // newly focused window, and D-pad left/right are not also
        // sent to a game.
        // Joystick navigation is deliberately NOT added: the sticks
        // already drive games / mouse output, and translating stick
        // deflection to arrow steps would conflict with that.
        // D-pad left/right is the requested, conflict-free control.
        public static void ProcessNavigation(int device, DS4State cState)
        {
            if (device < 0 || device >= held.Length || cState == null)
                return;
            lock (gate)
            {
                if (!held[device])
                    return;
            }
            // Pending arrow release from a previous tick.
            if (pendingArrow[device] != 0 && DateTime.UtcNow >= pendingArrowReleaseAt[device])
            {
                try { releaseKey?.Invoke(pendingArrow[device]); syncKeys?.Invoke(); AltTabLog("ARROW-RELEASE device=" + device + " key=" + pendingArrow[device]); } catch { }
                pendingArrow[device] = 0;
            }
            // Cross select, NEW press only, never the opening press.
            // Trigger is PS/Cross, so on the opening tick Cross is
            // still down. crossArmed is false until Cross has been
            // released at least once since Toggle, so that press is
            // swallowed, logged once, and cannot close the menu.
            // After release, crossArmed=true and a fresh Cross edge
            // selects, as before.
            bool cross = cState.Cross;
            if (!cross)
            {
                crossArmed[device] = true;
                prevNavCross[device] = false;
            }
            else if (crossArmed[device] && !prevNavCross[device])
            {
                AltTabLog("CROSS-SELECT device=" + device + " releasing Alt");
                lock (gate) { ReleaseAlt(device); }
                prevNavCross[device] = false;
                crossArmed[device] = false;
                cState.Cross = false;
                cState.DpadLeft = false;
                cState.DpadRight = false;
                return;
            }
            else
            {
                if (!crossIgnoreLogged[device])
                {
                    AltTabLog("CROSS-OPENING-IGNORED device=" + device + " opening Cross press swallowed, release Cross then press again to select");
                    crossIgnoreLogged[device] = true;
                }
                prevNavCross[device] = true;
            }
            lock (gate)
            {
                if (!held[device])
                    return;
            }
            DateTime now = DateTime.UtcNow;
            bool left = cState.DpadLeft;
            bool right = cState.DpadRight;
            if (left || right)
                AltTabLog("DPAD-EDGE device=" + device + " left=" + left + " right=" + right + " menuOpen=true");
            if (left)
            {
                if (!prevNavLeft[device] || now >= navLeftNext[device])
                {
                    StepLeft(device);
                    navLeftNext[device] = now + TimeSpan.FromMilliseconds(prevNavLeft[device] ? 220 : 450);
                }
                cState.DpadLeft = false;
            }
            else
            {
                navLeftNext[device] = DateTime.MinValue;
            }
            if (right)
            {
                if (!prevNavRight[device] || now >= navRightNext[device])
                {
                    StepRight(device);
                    navRightNext[device] = now + TimeSpan.FromMilliseconds(prevNavRight[device] ? 220 : 450);
                }
                cState.DpadRight = false;
            }
            else
            {
                navRightNext[device] = DateTime.MinValue;
            }
            prevNavLeft[device] = left;
            prevNavRight[device] = right;
        }

        private static void StepRight(int device)
        {
            Action<uint> press, release; Action sync;
            lock (gate) { press = pressKey; release = releaseKey; sync = syncKeys; }
            if (press == null || release == null || pendingArrow[device] != 0)
                return;
            try { press(rightKey); sync?.Invoke(); pendingArrow[device] = rightKey; pendingArrowReleaseAt[device] = DateTime.UtcNow.AddMilliseconds(80); AltTabLog("ARROW-SENT device=" + device + " dir=Right key=" + rightKey); } catch { }
        }

        private static void StepLeft(int device)
        {
            Action<uint> press, release; Action sync;
            lock (gate) { press = pressKey; release = releaseKey; sync = syncKeys; }
            if (press == null || release == null || pendingArrow[device] != 0)
                return;
            try { press(leftKey); sync?.Invoke(); pendingArrow[device] = leftKey; pendingArrowReleaseAt[device] = DateTime.UtcNow.AddMilliseconds(80); AltTabLog("ARROW-SENT device=" + device + " dir=Left key=" + leftKey); } catch { }
        }

        private static void ReleaseAlt(int device)
        {
            // Called with gate held, or from the watchdog thread which
            // takes the gate first. Release once, then clear.
            if (device < 0 || device >= held.Length)
                return;
            if (!held[device])
                return;
            if (pendingArrow[device] != 0)
            {
                try { releaseKey?.Invoke(pendingArrow[device]); } catch { }
                pendingArrow[device] = 0;
            }
            held[device] = false;
            menuOpenedAt[device] = DateTime.MinValue;
            menuSeen[device] = false;
            crossArmed[device] = false;
            crossIgnoreLogged[device] = false;
            prevNavLeft[device] = prevNavRight[device] = prevNavCross[device] = false;
            navLeftNext[device] = navRightNext[device] = DateTime.MinValue;
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
                    int switcherAbsentPolls = 0;
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
                        // Constant menu-open check: if the switcher
                        // was seen and is now absent for 10 polls
                        // (500 ms), menu is closed, release Alt.
                        try
                        {
                            if (SwitcherPresent()) { menuSeen[device] = true; switcherAbsentPolls = 0; }
                            else if (menuSeen[device] && ++switcherAbsentPolls >= 10)
                            { lock (gate) { ReleaseAlt(device); } return; }
                        }
                        catch { }
                        if ((DateTime.UtcNow - start).TotalSeconds >= 30)
                        {
                            AltTabLog("WATCHDOG release device=" + device + " reason=timeout-30s");
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
                        // DS4 FIX 2026-11-04: focus change NO LONGER kills Alt.
                        // The Alt-Tab switcher itself changes what
                        // GetForegroundWindow returns, so this fired
                        // spuriously and closed the menu before any
                        // D-pad press could be processed. It is now
                        // only logged, never a release reason.
                        bool focusChanged = false;
                        try
                        {
                            IntPtr nowWindow = GetForegroundWindow();
                            focusChanged = nowWindow != IntPtr.Zero && nowWindow != startWindow;
                        }
                        catch { }
                        if (focusChanged)
                            AltTabLog("WATCHDOG focus-changed-ignored device=" + device);
                        // New click edge or Esc edge.
                        if ((mouseDown && !prevMouseDown) || (escDown && !prevEscDown))
                        {
                            AltTabLog("WATCHDOG release device=" + device + " reason=click-or-esc");
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
