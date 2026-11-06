using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    // DualTouch port, written clean in C# from the audited layout
    // description only. No DualTouch code, binary, script, installer,
    // DLL, font, or glyph image was copied, downloaded, or run.
    // No network call, persistence, autostart, registry Run key,
    // Steam shortcut editing, tray code, or hidden code is added here.
    //
    // Layout: DualTouch QWERTY 75%, 6 rows, full rectangle, from
    // windows/data/cfg/keyboard-layout.yaml (parent audit):
    //  Row 1 function: Esc, F1-F12, Home, End, Ins.
    //  Row 2 number: ` ~, 1 ! .. 0 ), - _, = +, Backspace (1.4).
    //  Row 3: Tab (1.4), q-p, [ {, ] }, \ |.
    //  Row 4: Caps (1.6), a-l, ; :, ' ", Enter (1.7).
    //  Row 5: Shift (2.0), z-m, , <, . >, / ?, Select (2.0, replaces
    //    right Shift; see SelectKey below).
    //  Row 6 bottom: Ctrl (1.2), Alt (1.1), Win (1.1), Space (4.5),
    //    Left, Up, Down, Right arrows (0.9 each), Paste (1.2),
    //    Hide (replaces Move; see Hide below).
    //  Every row is a star-column Grid in the window, so every row
    //  fills the full window width; Space and Shift are wide enough
    //  that the bottom does not taper.
    //
    // Palette: DualTouch default Gruvbox, NOT the DS4Windows palette.
    //  PALETTE EXCEPTION, stated plainly: Chuck's earlier rule was
    //  all key text black, but black text is unreadable on DualTouch's
    //  dark #141414 keys, so this port uses DualTouch's cream text
    //  #EBDBB2 on dark keys. He said he may switch palette later.
    //  Do not claim black text in this build.
    //  background #000000, key inactive #141414, key hover/focus
    //  #2A2A2A, key click/press #3F3F3F, modifier idle #1A1A1A,
    //  text #EBDBB2 cream, shadow #6B6B6B, accent #1A9FFF.
    //  The window itself and its outer border are transparent; key
    //  borders are transparent except a subtle accent border on the
    //  selected key. No white bars top or bottom, no white-on-white.
    //  Selected key only changes shade, never size (no enlargement,
    //  no scale, no taller key, no larger font). Pressed key uses the
    //  DualTouch click shade #3F3F3F on #141414 keys: dimmer, not
    //  fully black.
    //
    // Triggers (DS4 conventions, visible badges in the window):
    //  R2 = Enter, L2 hold = Shift (live tracked), L3 = Caps,
    //  Square = Backspace, X (Cross) = type the highlighted key,
    //  Circle = close. Square Backspace and R2 Enter are real sends,
    //  not just badges (see Mapping.ProcessOnScreenKeyboardTick).
    //  L3 Caps and L2 Shift worked before and are kept working.
    //  Shift/Caps change the key labels themselves to capitals and
    //  shifted symbols, like Steam (see the window's DisplayLabel).
    //
    // Hide: the old Move button is gone completely. There is no Move
    // position cycling at all. The Hide key closes the keyboard
    // unconditionally (Circle does too); it no longer needs Shift.
    //
    // Select: DualTouch's text-selection mode, adapted so it cannot
    // break typing. Tapping Select toggles selection mode on/off.
    // While on, arrow keys send Shift+Arrow (extending a selection);
    // typing any character, Space, Tab, Enter, or Backspace clears
    // selection mode and types normally. It is not omitted.
    //
    // Paste: Paste key on the keyboard itself. Normally Ctrl+V,
    // Ctrl+C when Shift is held (shifted label shows Copy). It uses
    // normal SendInput key taps via Mapping.TapMultiKeys, no
    // clipboard tricks beyond that. Copy/Paste were removed from
    // Special Actions only (see SystemAction files); other System
    // Actions are untouched.
    //
    // Touchpad (2026-11-04, Chuck: touchpad must do a cursor like Steam,
    // not move the selection, and up/down was inverted): while the
    // Python keyboard is open, the one DualSense touchpad feeds ONLY
    // the Python pad cursor via TouchDelta. It does NOT move the C#
    // selection here, and the Python Y flip (see ds4_main.py) fixes
    // the inversion. Mouse movement is locked off while open and
    // restored on close (Mouse.cs / ControlService suppression).
    //
    // Emoji/symbols: OMITTED from this layout. DualTouch's 6-row
    // layout has no emoji/symbols toggle key, and adding a 7th row
    // or squeezing a toggle into a row would break the one-to-one
    // port. The old emoji/symbols panel state and handlers are kept
    // in this file (dormant, no toggle key can open them), so the
    // port itself does not break if a toggle is added later.
    public static class OnScreenKeyboardState
    {
        public enum KeyKind
        {
            Char, Backspace, Enter, Space, ShiftHold, Caps, Hide,
            EmojiToggle, ArrowLeft, ArrowDown, ArrowUp, ArrowRight, Emoji,
            Tab, SymbolsToggle, Symbol,
            Function, CtrlKey, AltKey, WinKey, PasteKey, SelectKey,
        }

        public struct KeyDef
        {
            public KeyKind Kind;
            public string Label;
            public ushort Vk;
            public string Text;
            public KeyDef(KeyKind kind, string label, ushort vk = 0, string text = null)
            {
                Kind = kind; Label = label; Vk = vk; Text = text;
            }
        }

        public static readonly KeyDef[][] Rows = new KeyDef[][]
        {
            // Row 1: function row.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Function, "Esc", 0x1B),
                new KeyDef(KeyKind.Function, "F1", 0x70),
                new KeyDef(KeyKind.Function, "F2", 0x71),
                new KeyDef(KeyKind.Function, "F3", 0x72),
                new KeyDef(KeyKind.Function, "F4", 0x73),
                new KeyDef(KeyKind.Function, "F5", 0x74),
                new KeyDef(KeyKind.Function, "F6", 0x75),
                new KeyDef(KeyKind.Function, "F7", 0x76),
                new KeyDef(KeyKind.Function, "F8", 0x77),
                new KeyDef(KeyKind.Function, "F9", 0x78),
                new KeyDef(KeyKind.Function, "F10", 0x79),
                new KeyDef(KeyKind.Function, "F11", 0x7A),
                new KeyDef(KeyKind.Function, "F12", 0x7B),
                new KeyDef(KeyKind.Function, "Home", 0x24),
                new KeyDef(KeyKind.Function, "End", 0x23),
                new KeyDef(KeyKind.Function, "Ins", 0x2D),
            },
            // Row 2: number row, Backspace 1.4 wide in the window.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Char, "~\n`", 0xC0, "`"),
                new KeyDef(KeyKind.Char, "!\n1", 0x31, "1"),
                new KeyDef(KeyKind.Char, "@\n2", 0x32, "2"),
                new KeyDef(KeyKind.Char, "#\n3", 0x33, "3"),
                new KeyDef(KeyKind.Char, "$\n4", 0x34, "4"),
                new KeyDef(KeyKind.Char, "%\n5", 0x35, "5"),
                new KeyDef(KeyKind.Char, "^\n6", 0x36, "6"),
                new KeyDef(KeyKind.Char, "&\n7", 0x37, "7"),
                new KeyDef(KeyKind.Char, "*\n8", 0x38, "8"),
                new KeyDef(KeyKind.Char, "(\n9", 0x39, "9"),
                new KeyDef(KeyKind.Char, ")\n0", 0x30, "0"),
                new KeyDef(KeyKind.Char, "_\n-", 0xBD, "-"),
                new KeyDef(KeyKind.Char, "+\n=", 0xBB, "="),
                new KeyDef(KeyKind.Backspace, "Backspace", 0x08),
            },
            // Row 3: Tab 1.4 wide, q-p, brackets, backslash.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Tab, "Tab", 0x09),
                new KeyDef(KeyKind.Char, "q", 0x51, "q"), new KeyDef(KeyKind.Char, "w", 0x57, "w"),
                new KeyDef(KeyKind.Char, "e", 0x45, "e"), new KeyDef(KeyKind.Char, "r", 0x52, "r"),
                new KeyDef(KeyKind.Char, "t", 0x54, "t"), new KeyDef(KeyKind.Char, "y", 0x59, "y"),
                new KeyDef(KeyKind.Char, "u", 0x55, "u"), new KeyDef(KeyKind.Char, "i", 0x49, "i"),
                new KeyDef(KeyKind.Char, "o", 0x4F, "o"), new KeyDef(KeyKind.Char, "p", 0x50, "p"),
                new KeyDef(KeyKind.Char, "{\n[", 0xDB, "["),
                new KeyDef(KeyKind.Char, "}\n]", 0xDD, "]"),
                new KeyDef(KeyKind.Char, "|\n\\", 0xDC, "\\"),
            },
            // Row 4: Caps 1.6 wide, a-l, semicolon, quote, Enter 1.7.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Caps, "Caps"),
                new KeyDef(KeyKind.Char, "a", 0x41, "a"), new KeyDef(KeyKind.Char, "s", 0x53, "s"),
                new KeyDef(KeyKind.Char, "d", 0x44, "d"), new KeyDef(KeyKind.Char, "f", 0x46, "f"),
                new KeyDef(KeyKind.Char, "g", 0x47, "g"), new KeyDef(KeyKind.Char, "h", 0x48, "h"),
                new KeyDef(KeyKind.Char, "j", 0x4A, "j"), new KeyDef(KeyKind.Char, "k", 0x4B, "k"),
                new KeyDef(KeyKind.Char, "l", 0x4C, "l"),
                new KeyDef(KeyKind.Char, ":\n;", 0xBA, ";"),
                new KeyDef(KeyKind.Char, "\"\n'", 0xDE, "'"),
                new KeyDef(KeyKind.Enter, "Enter", 0x0D),
            },
            // Row 5: Shift 2.0 wide, z-m, comma, period, slash,
            // Select 2.0 wide (replaces right Shift).
            new KeyDef[]
            {
                new KeyDef(KeyKind.ShiftHold, "Shift"),
                new KeyDef(KeyKind.Char, "z", 0x5A, "z"), new KeyDef(KeyKind.Char, "x", 0x58, "x"),
                new KeyDef(KeyKind.Char, "c", 0x43, "c"), new KeyDef(KeyKind.Char, "v", 0x56, "v"),
                new KeyDef(KeyKind.Char, "b", 0x42, "b"), new KeyDef(KeyKind.Char, "n", 0x4E, "n"),
                new KeyDef(KeyKind.Char, "m", 0x4D, "m"),
                new KeyDef(KeyKind.Char, "<\n,", 0xBC, ","),
                new KeyDef(KeyKind.Char, ">\n.", 0xBE, "."),
                new KeyDef(KeyKind.Char, "?\n/", 0xBF, "/"),
                new KeyDef(KeyKind.SelectKey, "Select"),
            },
            // Row 6 bottom: Ctrl 1.2, Alt 1.1, Win 1.1, Space 4.5,
            // Left, Up, Down, Right 0.9 each, Paste 1.2, Hide.
            new KeyDef[]
            {
                new KeyDef(KeyKind.CtrlKey, "Ctrl", 0x11),
                new KeyDef(KeyKind.AltKey, "Alt", 0x12),
                new KeyDef(KeyKind.WinKey, "Win", 0x5B),
                new KeyDef(KeyKind.Space, "Space", 0x20, " "),
                new KeyDef(KeyKind.ArrowLeft, "", 0x25),
                new KeyDef(KeyKind.ArrowUp, "", 0x26),
                new KeyDef(KeyKind.ArrowDown, "", 0x28),
                new KeyDef(KeyKind.ArrowRight, "", 0x27),
                new KeyDef(KeyKind.PasteKey, "Paste"),
                new KeyDef(KeyKind.Hide, "Hide"),
            },
        };

        // Emoji panel: kept dormant (see header note). Selecting one
        // types it via Unicode SendInput, never via paste.
        public static readonly KeyDef[] EmojiRow = new KeyDef[]
        {
            new KeyDef(KeyKind.Emoji, "Smile", 0, "\U0001F600"),
            new KeyDef(KeyKind.Emoji, "Laugh", 0, "\U0001F602"),
            new KeyDef(KeyKind.Emoji, "Heart", 0, "❤️"),
            new KeyDef(KeyKind.Emoji, "Thumbs Up", 0, "\U0001F44D"),
            new KeyDef(KeyKind.Emoji, "Fire", 0, "\U0001F525"),
            new KeyDef(KeyKind.Emoji, "Check", 0, "✅"),
        };

        // Symbols panel, kept dormant (see header note).
        public static readonly KeyDef[] SymbolsRow = new KeyDef[]
        {
            new KeyDef(KeyKind.Symbol, "\u20ac", 0, "\u20ac"),
            new KeyDef(KeyKind.Symbol, "\u00a3", 0, "\u00a3"),
            new KeyDef(KeyKind.Symbol, "\u00a5", 0, "\u00a5"),
            new KeyDef(KeyKind.Symbol, "\u00a9", 0, "\u00a9"),
            new KeyDef(KeyKind.Symbol, "\u00ae", 0, "\u00ae"),
            new KeyDef(KeyKind.Symbol, "\u2122", 0, "\u2122"),
            new KeyDef(KeyKind.Symbol, "\u00b0", 0, "\u00b0"),
            new KeyDef(KeyKind.Symbol, "\u00b1", 0, "\u00b1"),
            new KeyDef(KeyKind.Symbol, "\u00d7", 0, "\u00d7"),
            new KeyDef(KeyKind.Symbol, "\u00f7", 0, "\u00f7"),
        };

        private static readonly bool[] active = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] useTouchpadCursor = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] selRow = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] selCol = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] capsOn = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] shiftLatch = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] shiftHeld = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] ctrlLatch = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] altLatch = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] winLatch = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] selectMode = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] pressedRow = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] pressedCol = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] pressedPanel = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly DateTime[] pressedAt = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] emojiPanel = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] symbolsPanel = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        // Navigation repeat state, per device.
        private static readonly DateTime[] lastNavTime = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] lastNavDx = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] lastNavDy = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        // Touchpad cursor accumulation, per device.
        private static readonly double[] touchAccX = new double[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] touchpadTapPending = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly double[] touchAccY = new double[Global.MAX_DS4_CONTROLLER_COUNT];

        public static event Action<int, bool> KeyboardOpened;
        public static event Action<int> KeyboardClosed;
        public static event Action<int> SelectionChanged;
        // DS4 Python keyboard: touchpad delta feed for the runner.
        // Fired from MoveByTouch, which no longer moves the C# selection
        // (2026-11-04): the Python pad cursor is the only touchpad
        // target, like original DualTouch / Steam.
        public static event Action<int, double, double> TouchDelta;

        public static bool IsActive(int device) =>
            device >= 0 && device < active.Length && active[device];
        public static void RequestTouchpadTap(int device) { if (device >= 0 && device < touchpadTapPending.Length) touchpadTapPending[device] = true; }
        public static bool ConsumeTouchpadTap(int device) { if (device < 0 || device >= touchpadTapPending.Length) return false; bool v = touchpadTapPending[device]; touchpadTapPending[device] = false; return v; }
        public static bool UseTouchpadCursor(int device) =>
            device >= 0 && device < useTouchpadCursor.Length && useTouchpadCursor[device];
        public static int SelectedRow(int device) => selRow[device];
        public static int SelectedCol(int device) => selCol[device];
        public static bool CapsOn(int device) => capsOn[device];
        public static bool ShiftLatch(int device) => shiftLatch[device];
        public static bool ShiftHeld(int device) => device >= 0 && device < shiftHeld.Length && shiftHeld[device];
        public static bool CtrlLatch(int device) => device >= 0 && device < ctrlLatch.Length && ctrlLatch[device];
        public static bool AltLatch(int device) => device >= 0 && device < altLatch.Length && altLatch[device];
        public static bool WinLatch(int device) => device >= 0 && device < winLatch.Length && winLatch[device];
        public static bool SelectModeOn(int device) => device >= 0 && device < selectMode.Length && selectMode[device];
        public static void SetShiftHeld(int device, bool held)
        {
            if (device < 0 || device >= shiftHeld.Length) return;
            if (shiftHeld[device] == held) return;
            shiftHeld[device] = held;
            try { SelectionChanged?.Invoke(device); } catch { }
        }
        public static void SetShiftLatch(int device) { if (device >= 0 && device < shiftLatch.Length) { shiftLatch[device] = true; try { SelectionChanged?.Invoke(device); } catch { } } }
        public static void ClearShiftLatch(int device) { if (device >= 0 && device < shiftLatch.Length) { if (!shiftLatch[device]) return; shiftLatch[device] = false; try { SelectionChanged?.Invoke(device); } catch { } } }
        public static void ToggleCtrlLatch(int device) { if (device >= 0 && device < ctrlLatch.Length) { ctrlLatch[device] = !ctrlLatch[device]; try { SelectionChanged?.Invoke(device); } catch { } } }
        public static void ToggleAltLatch(int device) { if (device >= 0 && device < altLatch.Length) { altLatch[device] = !altLatch[device]; try { SelectionChanged?.Invoke(device); } catch { } } }
        public static void ToggleWinLatch(int device) { if (device >= 0 && device < winLatch.Length) { winLatch[device] = !winLatch[device]; try { SelectionChanged?.Invoke(device); } catch { } } }
        public static void ToggleSelectMode(int device) { if (device >= 0 && device < selectMode.Length) { selectMode[device] = !selectMode[device]; try { SelectionChanged?.Invoke(device); } catch { } } }
        public static void ClearSelectMode(int device) { if (device >= 0 && device < selectMode.Length && selectMode[device]) { selectMode[device] = false; try { SelectionChanged?.Invoke(device); } catch { } } }

        // Modifier keys for a tap, in Ctrl, Alt, Win, Shift order.
        // includeCapsAsShift is true only for character keys, where
        // Caps acts as shift; function/tab/arrow keys do not get Caps.
        public static List<ushort> ModifierKeys(int device, bool shiftHeldParam, bool includeCapsAsShift)
        {
            var mods = new List<ushort>();
            if (device < 0 || device >= active.Length) return mods;
            if (ctrlLatch[device]) mods.Add(0x11);
            if (altLatch[device]) mods.Add(0x12);
            if (winLatch[device]) mods.Add(0x5B);
            if (shiftHeldParam || shiftLatch[device] || (includeCapsAsShift && capsOn[device]))
                mods.Add(0x10);
            return mods;
        }

        // Clear one-shot modifier latches after a normal key is typed.
        // Caps and Select mode are sticky and are NOT cleared here;
        // Select mode is cleared separately by typing (see ClearSelectMode).
        public static void ClearModifierLatches(int device)
        {
            if (device < 0 || device >= active.Length) return;
            bool changed = shiftLatch[device] || ctrlLatch[device] || altLatch[device] || winLatch[device];
            shiftLatch[device] = false;
            ctrlLatch[device] = false;
            altLatch[device] = false;
            winLatch[device] = false;
            if (changed) { try { SelectionChanged?.Invoke(device); } catch { } }
        }

        public static void MarkPressed(int device)
        {
            if (device < 0 || device >= pressedRow.Length || !IsActive(device)) return;
            pressedPanel[device] = symbolsPanel[device] ? 2 : (emojiPanel[device] ? 1 : 0);
            pressedRow[device] = pressedPanel[device] == 0 ? selRow[device] : 0;
            pressedCol[device] = selCol[device];
            pressedAt[device] = DateTime.UtcNow;
            try { SelectionChanged?.Invoke(device); } catch { }
        }
        public static void MarkPressedAt(int device, int row, int col, int panel)
        {
            if (device < 0 || device >= pressedRow.Length) return;
            pressedRow[device] = row; pressedCol[device] = col; pressedPanel[device] = panel;
            pressedAt[device] = DateTime.UtcNow;
        }
        // Visual press feedback for a trigger key (R2 Enter, Square
        // Backspace) that is not the currently highlighted key.
        public static void MarkKeyPressed(int device, KeyKind kind)
        {
            if (device < 0 || device >= pressedRow.Length) return;
            for (int ri = 0; ri < Rows.Length; ri++)
                for (int ci = 0; ci < Rows[ri].Length; ci++)
                    if (Rows[ri][ci].Kind == kind)
                    {
                        MarkPressedAt(device, ri, ci, 0);
                        return;
                    }
        }
        public static bool IsPressed(int device, int row, int col, int panel)
        {
            if (device < 0 || device >= pressedRow.Length) return false;
            return pressedRow[device] == row && pressedCol[device] == col && pressedPanel[device] == panel &&
                (DateTime.UtcNow - pressedAt[device]).TotalMilliseconds < 400;
        }
        public static bool EmojiPanelActive(int device) => emojiPanel[device];
        public static bool SymbolsPanelActive(int device) => symbolsPanel[device];

        public static void Open(int device, bool touchpadCursor)
        {
            if (device < 0 || device >= active.Length)
                return;
            if (active[device])
                return;
            active[device] = true;
            useTouchpadCursor[device] = touchpadCursor;
            selRow[device] = 0;
            selCol[device] = 0;
            capsOn[device] = false;
            shiftLatch[device] = false;
            ctrlLatch[device] = false;
            altLatch[device] = false;
            winLatch[device] = false;
            selectMode[device] = false;
            emojiPanel[device] = false;
            symbolsPanel[device] = false;
            pressedAt[device] = DateTime.MinValue;
            lastNavTime[device] = DateTime.MinValue;
            lastNavDx[device] = 0;
            lastNavDy[device] = 0;
            touchAccX[device] = 0;
            touchAccY[device] = 0;
            try { KeyboardOpened?.Invoke(device, touchpadCursor); } catch { }
        }

        public static void Close(int device)
        {
            if (device < 0 || device >= active.Length)
                return;
            if (!active[device])
                return;
            active[device] = false;
            useTouchpadCursor[device] = false;
            emojiPanel[device] = false;
            symbolsPanel[device] = false;
            shiftHeld[device] = false;
            ctrlLatch[device] = false;
            altLatch[device] = false;
            winLatch[device] = false;
            selectMode[device] = false;
            pressedAt[device] = DateTime.MinValue;
            try { KeyboardClosed?.Invoke(device); } catch { }
        }

        public static KeyDef CurrentKey(int device)
        {
            if (symbolsPanel[device])
            {
                int c = Math.Max(0, Math.Min(SymbolsRow.Length - 1, selCol[device]));
                return SymbolsRow[c];
            }
            if (emojiPanel[device])
            {
                int c = Math.Max(0, Math.Min(EmojiRow.Length - 1, selCol[device]));
                return EmojiRow[c];
            }
            int r = Math.Max(0, Math.Min(Rows.Length - 1, selRow[device]));
            int col = Math.Max(0, Math.Min(Rows[r].Length - 1, selCol[device]));
            return Rows[r][col];
        }

        public static void Move(int device, int dx, int dy)
        {
            if (!IsActive(device))
                return;
            if (symbolsPanel[device])
            {
                int nc = selCol[device] + dx;
                if (nc >= 0 && nc < SymbolsRow.Length)
                    selCol[device] = nc;
            }
            else if (emojiPanel[device])
            {
                int nc = selCol[device] + dx;
                if (nc >= 0 && nc < EmojiRow.Length)
                    selCol[device] = nc;
            }
            else
            {
                int nr = selRow[device] + dy;
                if (nr >= 0 && nr < Rows.Length)
                {
                    selRow[device] = nr;
                    if (selCol[device] >= Rows[nr].Length)
                        selCol[device] = Rows[nr].Length - 1;
                }
                int nc = selCol[device] + dx;
                if (nc >= 0 && nc < Rows[selRow[device]].Length)
                    selCol[device] = nc;
            }
            try { SelectionChanged?.Invoke(device); } catch { }
        }

        // Navigation with repeat: first move is immediate, then repeat
        // every 150 ms while the same direction is held.
        public static bool TryMoveWithRepeat(int device, int dx, int dy, DateTime now)
        {
            if (dx == 0 && dy == 0)
            {
                lastNavDx[device] = 0;
                lastNavDy[device] = 0;
                return false;
            }
            if (dx != lastNavDx[device] || dy != lastNavDy[device] ||
                lastNavTime[device] == DateTime.MinValue ||
                (now - lastNavTime[device]).TotalMilliseconds >= 150)
            {
                lastNavDx[device] = dx;
                lastNavDy[device] = dy;
                lastNavTime[device] = now;
                Move(device, dx, dy);
                return true;
            }
            return false;
        }

        // Touchpad cursor (2026-11-04): do NOT move the C# selection.
        // The touchpad is the Python pad cursor only, like original
        // DualTouch / Steam. Forward the raw delta to Python, where
        // Y is flipped once (ds4_main.py). The old cell-per-200-units
        // selection move is removed, not just gated.
        public static void MoveByTouch(int device, double deltaX, double deltaY)
        {
            if (!IsActive(device))
                return;
            try { TouchDelta?.Invoke(device, deltaX, deltaY); } catch { }
        }

        public static void ToggleCaps(int device)
        {
            if (!IsActive(device))
                return;
            capsOn[device] = !capsOn[device];
            try { SelectionChanged?.Invoke(device); } catch { }
        }

        public static void ToggleEmojiPanel(int device)
        {
            if (!IsActive(device))
                return;
            symbolsPanel[device] = false;
            emojiPanel[device] = !emojiPanel[device];
            selCol[device] = 0;
            if (!emojiPanel[device])
                selRow[device] = 0;
            try { SelectionChanged?.Invoke(device); } catch { }
        }

        public static void ToggleSymbolsPanel(int device)
        {
            if (!IsActive(device))
                return;
            emojiPanel[device] = false;
            symbolsPanel[device] = !symbolsPanel[device];
            selCol[device] = 0;
            if (!symbolsPanel[device])
                selRow[device] = 0;
            try { SelectionChanged?.Invoke(device); } catch { }
        }

        // Unicode typing for emoji, via SendInput with KEYEVENTF_UNICODE.
        // No clipboard, no paste button.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
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
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public static void SendUnicodeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            List<INPUT> inputs = new List<INPUT>();
            foreach (char ch in text)
            {
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE }
                    }
                });
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
                    }
                });
            }
            try { SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT))); } catch { }
        }
    }
}
