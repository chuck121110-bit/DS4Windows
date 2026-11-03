using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    // On-screen keyboard LOGIC only. The visual layout follows Chuck's
    // pick: the first / default variant, not compact and not big/couch,
    // keys a little less wide than that default, emojis, arrow keys,
    // and the Hide button at the bottom, not a weird top part, so the
    // bottom keys (caps lock, shift hold, space) are not squished.
    // Backspace and Enter are a little smaller, not super thin, and
    // their text still fits. White/black styling is NOT finalized
    // here; the window uses plain default styling for now.
    public static class OnScreenKeyboardState
    {
        public enum KeyKind
        {
            Char, Backspace, Enter, Space, ShiftHold, Caps, Hide,
            EmojiToggle, ArrowLeft, ArrowDown, ArrowUp, ArrowRight, Emoji,
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

        // Rows, top to bottom. The last row is the bottom row: emoji
        // toggle, arrows, Hide. The row above it is caps / shift / space.
        public static readonly KeyDef[][] Rows = new KeyDef[][]
        {
            // Row 0: numbers, backspace slightly smaller (layout only;
            // the window gives Backspace fewer columns, not a thin key).
            new KeyDef[]
            {
                new KeyDef(KeyKind.Char, "1", 0x31, "1"), new KeyDef(KeyKind.Char, "2", 0x32, "2"),
                new KeyDef(KeyKind.Char, "3", 0x33, "3"), new KeyDef(KeyKind.Char, "4", 0x34, "4"),
                new KeyDef(KeyKind.Char, "5", 0x35, "5"), new KeyDef(KeyKind.Char, "6", 0x36, "6"),
                new KeyDef(KeyKind.Char, "7", 0x37, "7"), new KeyDef(KeyKind.Char, "8", 0x38, "8"),
                new KeyDef(KeyKind.Char, "9", 0x39, "9"), new KeyDef(KeyKind.Char, "0", 0x30, "0"),
                new KeyDef(KeyKind.Backspace, "Backspace", 0x08),
            },
            // Row 1: QWERTY, Enter slightly smaller.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Char, "Q", 0x51, "q"), new KeyDef(KeyKind.Char, "W", 0x57, "w"),
                new KeyDef(KeyKind.Char, "E", 0x45, "e"), new KeyDef(KeyKind.Char, "R", 0x52, "r"),
                new KeyDef(KeyKind.Char, "T", 0x54, "t"), new KeyDef(KeyKind.Char, "Y", 0x59, "y"),
                new KeyDef(KeyKind.Char, "U", 0x55, "u"), new KeyDef(KeyKind.Char, "I", 0x49, "i"),
                new KeyDef(KeyKind.Char, "O", 0x4F, "o"), new KeyDef(KeyKind.Char, "P", 0x50, "p"),
                new KeyDef(KeyKind.Enter, "Enter", 0x0D),
            },
            // Row 2: home row.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Char, "A", 0x41, "a"), new KeyDef(KeyKind.Char, "S", 0x53, "s"),
                new KeyDef(KeyKind.Char, "D", 0x44, "d"), new KeyDef(KeyKind.Char, "F", 0x46, "f"),
                new KeyDef(KeyKind.Char, "G", 0x47, "g"), new KeyDef(KeyKind.Char, "H", 0x48, "h"),
                new KeyDef(KeyKind.Char, "J", 0x4A, "j"), new KeyDef(KeyKind.Char, "K", 0x4B, "k"),
                new KeyDef(KeyKind.Char, "L", 0x4C, "l"),
            },
            // Row 3: bottom row.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Char, "Z", 0x5A, "z"), new KeyDef(KeyKind.Char, "X", 0x58, "x"),
                new KeyDef(KeyKind.Char, "C", 0x43, "c"), new KeyDef(KeyKind.Char, "V", 0x56, "v"),
                new KeyDef(KeyKind.Char, "B", 0x42, "b"), new KeyDef(KeyKind.Char, "N", 0x4E, "n"),
                new KeyDef(KeyKind.Char, "M", 0x4D, "m"),
            },
            // Row 4: caps lock, shift hold, space. Full width, not squished.
            new KeyDef[]
            {
                new KeyDef(KeyKind.Caps, "Caps Lock"),
                new KeyDef(KeyKind.ShiftHold, "Shift (hold)"),
                new KeyDef(KeyKind.Space, "Space", 0x20, " "),
            },
            // Row 5: bottom row, emojis, arrows, Hide.
            new KeyDef[]
            {
                new KeyDef(KeyKind.EmojiToggle, "Emoji"),
                new KeyDef(KeyKind.ArrowLeft, "Left", 0x25),
                new KeyDef(KeyKind.ArrowDown, "Down", 0x28),
                new KeyDef(KeyKind.ArrowUp, "Up", 0x26),
                new KeyDef(KeyKind.ArrowRight, "Right", 0x27),
                new KeyDef(KeyKind.Hide, "Hide"),
            },
        };

        // Emoji panel: a small placeholder set. Selecting one types it
        // via Unicode SendInput, never via paste, and there is no paste
        // button anywhere on the keyboard.
        public static readonly KeyDef[] EmojiRow = new KeyDef[]
        {
            new KeyDef(KeyKind.Emoji, "Smile", 0, "\U0001F600"),
            new KeyDef(KeyKind.Emoji, "Laugh", 0, "\U0001F602"),
            new KeyDef(KeyKind.Emoji, "Heart", 0, "❤️"),
            new KeyDef(KeyKind.Emoji, "Thumbs Up", 0, "\U0001F44D"),
            new KeyDef(KeyKind.Emoji, "Fire", 0, "\U0001F525"),
            new KeyDef(KeyKind.Emoji, "Check", 0, "✅"),
        };

        private static readonly bool[] active = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] useTouchpadCursor = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] selRow = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] selCol = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] capsOn = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] shiftLatch = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] emojiPanel = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
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
        public static void SetShiftLatch(int device) { if (device >= 0 && device < shiftLatch.Length) shiftLatch[device] = true; }
        public static void ClearShiftLatch(int device) { if (device >= 0 && device < shiftLatch.Length) shiftLatch[device] = false; }
        public static bool EmojiPanelActive(int device) => emojiPanel[device];

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
            emojiPanel[device] = false;
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
            try { KeyboardClosed?.Invoke(device); } catch { }
        }

        public static KeyDef CurrentKey(int device)
        {
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
            if (emojiPanel[device])
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

        // Touchpad cursor: accumulate relative touch movement, one cell
        // per ~200 units, so the touchpad moves the highlight instead
        // of the Windows cursor while the keyboard is open.
        public static void MoveByTouch(int device, double deltaX, double deltaY)
        {
            if (!IsActive(device) || !UseTouchpadCursor(device))
                return;
            touchAccX[device] += deltaX;
            touchAccY[device] += deltaY;
            int stepsX = (int)(touchAccX[device] / 200.0);
            int stepsY = (int)(touchAccY[device] / 200.0);
            if (stepsX != 0)
            {
                touchAccX[device] -= stepsX * 200.0;
                Move(device, stepsX, 0);
            }
            if (stepsY != 0)
            {
                touchAccY[device] -= stepsY * 200.0;
                Move(device, 0, stepsY);
            }
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
            emojiPanel[device] = !emojiPanel[device];
            selCol[device] = 0;
            if (!emojiPanel[device])
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
