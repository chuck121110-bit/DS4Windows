using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    // DualTouch port window, written clean in C# from the audited
    // layout description only. No DualTouch code, binary, or image
    // was copied, downloaded, or run.
    //
    // Window 1120x480, the previous DS4 OSK scale; the 6-row
    // DualTouch layout (including the function row) fits it, so no
    // larger window was needed. Full rectangle: each row is a Grid
    // with star columns, so every row fills the full width.
    //
    // Palette: DualTouch default Gruvbox, NOT the DS4Windows palette.
    // PALETTE EXCEPTION, stated plainly: Chuck's earlier rule was
    // all key text black, but black text is unreadable on DualTouch's
    // dark #141414 keys, so this port uses DualTouch's cream text
    // #EBDBB2 on dark keys. He said he may switch palette later.
    // Do not claim black text in this build.
    // Key inactive #141414, modifier idle #1A1A1A, hover/focus
    // (selected) #2A2A2A, click/press #3F3F3F, text #EBDBB2 cream,
    // accent #1A9FFF. Window and outer border transparent, no white
    // bars top or bottom, no white-on-white text.
    //
    // Visual rules from Chuck: NO highlight getting bigger/enlarged
    // on selection. Selected key only changes shade (background
    // #2A2A2A plus a subtle accent border); height, font size, and
    // scale never change. Pressed key is dimmer, #3F3F3F, not fully
    // black. Key borders are transparent except that subtle
    // selection accent border.
    //
    // Icons are WPF Path geometry, NOT text elements: Backspace is a
    // square outline Path, Hide is a circle Path, arrows are filled
    // triangle Paths. Trigger badges are visible on the keys that
    // have DS4 triggers: Enter shows R2, Shift shows L2, Caps shows
    // L3, Backspace's square is its Square badge, Hide's circle is
    // its Circle badge. X (Cross) types the highlighted key and
    // Circle closes, as in Mapping.ProcessOnScreenKeyboardTick.
    //
    // No Move button, no Move position cycling at all. Hide closes
    // unconditionally. Shift/Caps change the key labels themselves
    // to capitals and shifted symbols, like Steam.
    //
    // Emoji/symbols panels are omitted from this layout (see
    // OnScreenKeyboard.cs); their panel code below is dormant, with
    // no toggle key that can open them.
    public partial class OnScreenKeyboardWindow : Window
    {
        private static readonly Color Cream = Color.FromRgb(0xEB, 0xDB, 0xB2);
        private static readonly Color KeyInactive = Color.FromRgb(0x14, 0x14, 0x14);
        private static readonly Color ModifierIdle = Color.FromRgb(0x1A, 0x1A, 0x1A);
        private static readonly Color SelectedShade = Color.FromRgb(0x2A, 0x2A, 0x2A);
        private static readonly Color PressedShade = Color.FromRgb(0x3F, 0x3F, 0x3F);
        private static readonly Color Accent = Color.FromRgb(0x1A, 0x9F, 0xFF);

        private static readonly Dictionary<int, OnScreenKeyboardWindow> openWindows =
            new Dictionary<int, OnScreenKeyboardWindow>();
        private readonly int deviceNum;
        private readonly List<List<Button>> rowButtons = new List<List<Button>>();
        private List<Button> emojiButtons = new List<Button>();
        private StackPanel mainPanel;
        private StackPanel emojiPanel;
        private StackPanel symbolsPanel;
        private List<Button> symbolsButtons = new List<Button>();
        private DispatcherTimer pressedTimer;

        public OnScreenKeyboardWindow(int device)
        {
            InitializeComponent();
            deviceNum = device;
            BuildKeys();
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Height - Height - 20;
            pressedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            pressedTimer.Tick += (s, e) => UpdateSelection();
            pressedTimer.Start();
            OnScreenKeyboardState.SelectionChanged += OnSelectionChanged;
            Closed += (s, e) =>
            {
                pressedTimer.Stop();
                OnScreenKeyboardState.SelectionChanged -= OnSelectionChanged;
                lock (openWindows)
                {
                    if (openWindows.TryGetValue(deviceNum, out var w) && w == this)
                        openWindows.Remove(deviceNum);
                }
            };
            UpdateSelection();
        }

        public static void ShowForDevice(int device)
        {
            lock (openWindows)
            {
                if (openWindows.TryGetValue(device, out var existing) && existing != null)
                {
                    existing.UpdateSelection();
                    return;
                }
                var win = new OnScreenKeyboardWindow(device);
                openWindows[device] = win;
                win.Show();
            }
        }

        public static void CloseForDevice(int device)
        {
            OnScreenKeyboardWindow win = null;
            lock (openWindows)
            {
                openWindows.TryGetValue(device, out win);
            }
            win?.Close();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            int exStyle = GetWindowLong(helper.Handle, -20);
            SetWindowLong(helper.Handle, -20, exStyle | 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */);
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // Star units per row, DualTouch widths from the task.
        private static double UnitsForKind(OnScreenKeyboardState.KeyKind kind)
        {
            switch (kind)
            {
                case OnScreenKeyboardState.KeyKind.Backspace:
                    return 1.4;
                case OnScreenKeyboardState.KeyKind.Tab:
                    return 1.4;
                case OnScreenKeyboardState.KeyKind.Caps:
                    return 1.6;
                case OnScreenKeyboardState.KeyKind.Enter:
                    return 1.7;
                case OnScreenKeyboardState.KeyKind.ShiftHold:
                    return 2.0;
                case OnScreenKeyboardState.KeyKind.SelectKey:
                    return 2.0;
                case OnScreenKeyboardState.KeyKind.CtrlKey:
                    return 1.2;
                case OnScreenKeyboardState.KeyKind.AltKey:
                    return 1.1;
                case OnScreenKeyboardState.KeyKind.WinKey:
                    return 1.1;
                case OnScreenKeyboardState.KeyKind.Space:
                    return 4.5;
                case OnScreenKeyboardState.KeyKind.ArrowLeft:
                case OnScreenKeyboardState.KeyKind.ArrowUp:
                case OnScreenKeyboardState.KeyKind.ArrowDown:
                case OnScreenKeyboardState.KeyKind.ArrowRight:
                    return 0.9;
                case OnScreenKeyboardState.KeyKind.PasteKey:
                    return 1.2;
                case OnScreenKeyboardState.KeyKind.Hide:
                    return 1.4;
                default:
                    return 1.0;
            }
        }

        private void BuildKeys()
        {
            mainPanel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Stretch };
            keysPanel.Children.Add(mainPanel);
            for (int ri = 0; ri < OnScreenKeyboardState.Rows.Length; ri++)
            {
                var row = OnScreenKeyboardState.Rows[ri];
                var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                var buttons = new List<Button>();
                for (int ci = 0; ci < row.Length; ci++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UnitsForKind(row[ci].Kind), GridUnitType.Star) });
                    var btn = MakeButton(row[ci], ri, ci, 0);
                    Grid.SetColumn(btn, ci);
                    buttons.Add(btn);
                    grid.Children.Add(btn);
                }
                rowButtons.Add(buttons);
                mainPanel.Children.Add(grid);
            }
            emojiPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            for (int ci = 0; ci < OnScreenKeyboardState.EmojiRow.Length; ci++)
            {
                var key = OnScreenKeyboardState.EmojiRow[ci];
                var btn = MakeButton(key, 0, ci, 1);
                btn.FontSize = 20;
                btn.Content = key.Text;
                emojiButtons.Add(btn);
                emojiPanel.Children.Add(btn);
            }
            keysPanel.Children.Add(emojiPanel);
            symbolsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            for (int ci = 0; ci < OnScreenKeyboardState.SymbolsRow.Length; ci++)
            {
                var key = OnScreenKeyboardState.SymbolsRow[ci];
                var btn = MakeButton(key, 0, ci, 2);
                btn.FontSize = 20;
                btn.Content = key.Text;
                symbolsButtons.Add(btn);
                symbolsPanel.Children.Add(btn);
            }
            keysPanel.Children.Add(symbolsPanel);
        }

        private static Path MakePath(Geometry geometry, bool filled)
        {
            return new Path
            {
                Data = geometry,
                Stroke = new SolidColorBrush(Cream),
                StrokeThickness = 2,
                Fill = filled ? new SolidColorBrush(Cream) : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private static object IconContent(OnScreenKeyboardState.KeyDef key)
        {
            // Backspace: square outline Path + label. Hide: circle
            // Path + label. Arrows: filled triangle Path only.
            // These are Path geometry, never text glyphs.
            switch (key.Kind)
            {
                case OnScreenKeyboardState.KeyKind.Backspace:
                {
                    var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    stack.Children.Add(MakePath(Geometry.Parse("M3,3 H17 V17 H3 Z"), false));
                    stack.Children.Add(new TextBlock { Text = "Backspace", Foreground = new SolidColorBrush(Cream), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
                    return stack;
                }
                case OnScreenKeyboardState.KeyKind.Hide:
                {
                    var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    stack.Children.Add(MakePath(new EllipseGeometry(new Point(10, 10), 8, 8), false));
                    stack.Children.Add(new TextBlock { Text = "Hide", Foreground = new SolidColorBrush(Cream), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
                    return stack;
                }
                case OnScreenKeyboardState.KeyKind.ArrowLeft:
                    return MakePath(Geometry.Parse("M16,3 L5,10 L16,17 Z"), true);
                case OnScreenKeyboardState.KeyKind.ArrowRight:
                    return MakePath(Geometry.Parse("M4,3 L15,10 L4,17 Z"), true);
                case OnScreenKeyboardState.KeyKind.ArrowUp:
                    return MakePath(Geometry.Parse("M3,16 L10,5 L17,16 Z"), true);
                case OnScreenKeyboardState.KeyKind.ArrowDown:
                    return MakePath(Geometry.Parse("M3,4 L10,15 L17,4 Z"), true);
                default:
                    return null;
            }
        }

        private static string BadgeFor(OnScreenKeyboardState.KeyKind kind)
        {
            // Visible DS4 trigger badges. Backspace and Hide use
            // their Path icons as badges, so they need no text badge.
            switch (kind)
            {
                case OnScreenKeyboardState.KeyKind.Enter: return "R2";
                case OnScreenKeyboardState.KeyKind.ShiftHold: return "L2";
                case OnScreenKeyboardState.KeyKind.Caps: return "L3";
                default: return null;
            }
        }

        private Button MakeButton(OnScreenKeyboardState.KeyDef key, int row, int col, int panel)
        {
            var btn = new Button
            {
                Height = 72,
                Margin = new Thickness(2),
                FontSize = 14,
                Tag = key,
                Background = new SolidColorBrush(KeyInactive),
                Foreground = new SolidColorBrush(Cream),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            object icon = IconContent(key);
            if (icon != null)
            {
                btn.Content = icon;
            }
            else
            {
                string badge = BadgeFor(key.Kind);
                if (badge != null)
                {
                    var grid = new Grid();
                    grid.Children.Add(new TextBlock { Text = key.Label, Foreground = new SolidColorBrush(Cream), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                    grid.Children.Add(new TextBlock { Text = badge, Foreground = new SolidColorBrush(Accent), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 4, 0) });
                    btn.Content = grid;
                }
                else
                {
                    btn.Content = key.Label;
                }
            }
            btn.Click += (s, e) =>
            {
                OnScreenKeyboardState.MarkPressedAt(deviceNum, row, col, panel);
                OnButtonClicked(key);
            };
            return btn;
        }

        private static string ShiftedChar(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length == 1 && text[0] >= 'a' && text[0] <= 'z')
                return text.ToUpperInvariant();
            switch (text)
            {
                case "`": return "~";
                case "1": return "!";
                case "2": return "@";
                case "3": return "#";
                case "4": return "$";
                case "5": return "%";
                case "6": return "^";
                case "7": return "&";
                case "8": return "*";
                case "9": return "(";
                case "0": return ")";
                case "-": return "_";
                case "=": return "+";
                case "[": return "{";
                case "]": return "}";
                case "\\": return "|";
                case ";": return ":";
                case "'": return "\"";
                case ",": return "<";
                case ".": return ">";
                case "/": return "?";
                default: return text;
            }
        }

        private string DisplayLabel(OnScreenKeyboardState.KeyDef key, bool shifted)
        {
            if (key.Kind != OnScreenKeyboardState.KeyKind.Char) return key.Label;
            if (!shifted) return key.Label;
            if (!string.IsNullOrEmpty(key.Text)) return ShiftedChar(key.Text);
            return key.Label;
        }

        private void TapWithModifiers(List<ushort> mods, ushort vk)
        {
            var keys = new List<ushort>(mods);
            keys.Add(vk);
            Mapping.TapMultiKeys(keys, DS4KeyType.None, false);
        }

        private void OnButtonClicked(OnScreenKeyboardState.KeyDef key)
        {
            bool shiftHeld = OnScreenKeyboardState.ShiftHeld(deviceNum);
            bool shiftOnly = shiftHeld || OnScreenKeyboardState.ShiftLatch(deviceNum);
            switch (key.Kind)
            {
                case OnScreenKeyboardState.KeyKind.Hide:
                    // Hide closes unconditionally, no Shift gate, no Move.
                    OnScreenKeyboardState.Close(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Tab:
                    TapWithModifiers(OnScreenKeyboardState.ModifierKeys(deviceNum, shiftHeld, false), 0x09);
                    OnScreenKeyboardState.ClearModifierLatches(deviceNum);
                    OnScreenKeyboardState.ClearSelectMode(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.SymbolsToggle:
                    OnScreenKeyboardState.ToggleSymbolsPanel(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Symbol:
                    OnScreenKeyboardState.SendUnicodeText(key.Text);
                    OnScreenKeyboardState.ToggleSymbolsPanel(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Caps:
                    OnScreenKeyboardState.ToggleCaps(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.ShiftHold:
                    OnScreenKeyboardState.SetShiftLatch(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.CtrlKey:
                    OnScreenKeyboardState.ToggleCtrlLatch(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.AltKey:
                    OnScreenKeyboardState.ToggleAltLatch(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.WinKey:
                    OnScreenKeyboardState.ToggleWinLatch(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.SelectKey:
                    OnScreenKeyboardState.ToggleSelectMode(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.PasteKey:
                    // Paste normally, Copy when Shift held. Normal
                    // SendInput taps, no clipboard tricks.
                    if (shiftOnly)
                        Mapping.TapMultiKeys(new List<ushort> { 0x11, 0x43 }, DS4KeyType.None, false);
                    else
                        Mapping.TapMultiKeys(new List<ushort> { 0x11, 0x56 }, DS4KeyType.None, false);
                    OnScreenKeyboardState.ClearModifierLatches(deviceNum);
                    OnScreenKeyboardState.ClearSelectMode(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.EmojiToggle:
                    OnScreenKeyboardState.ToggleEmojiPanel(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Emoji:
                    OnScreenKeyboardState.SendUnicodeText(key.Text);
                    OnScreenKeyboardState.ToggleEmojiPanel(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Space:
                    TapWithModifiers(OnScreenKeyboardState.ModifierKeys(deviceNum, shiftHeld, false), 0x20);
                    OnScreenKeyboardState.ClearModifierLatches(deviceNum);
                    OnScreenKeyboardState.ClearSelectMode(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Backspace:
                    TapWithModifiers(OnScreenKeyboardState.ModifierKeys(deviceNum, shiftHeld, false), 0x08);
                    OnScreenKeyboardState.ClearModifierLatches(deviceNum);
                    OnScreenKeyboardState.ClearSelectMode(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Enter:
                    TapWithModifiers(OnScreenKeyboardState.ModifierKeys(deviceNum, shiftHeld, false), 0x0D);
                    OnScreenKeyboardState.ClearModifierLatches(deviceNum);
                    OnScreenKeyboardState.ClearSelectMode(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Function:
                    TapWithModifiers(OnScreenKeyboardState.ModifierKeys(deviceNum, shiftHeld, false), key.Vk);
                    OnScreenKeyboardState.ClearModifierLatches(deviceNum);
                    OnScreenKeyboardState.ClearSelectMode(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.ArrowLeft:
                case OnScreenKeyboardState.KeyKind.ArrowDown:
                case OnScreenKeyboardState.KeyKind.ArrowUp:
                case OnScreenKeyboardState.KeyKind.ArrowRight:
                {
                    // Select mode: Shift+Arrow extends a selection.
                    // Arrows do not clear Select mode or other latches,
                    // so repeated arrows keep extending / jumping.
                    var mods = OnScreenKeyboardState.ModifierKeys(deviceNum, shiftHeld, false);
                    if (OnScreenKeyboardState.SelectModeOn(deviceNum) && !mods.Contains(0x10))
                        mods.Add(0x10);
                    TapWithModifiers(mods, key.Vk);
                    break;
                }
                default:
                    if (key.Vk != 0)
                    {
                        TapWithModifiers(OnScreenKeyboardState.ModifierKeys(deviceNum, shiftHeld, true), key.Vk);
                        OnScreenKeyboardState.ClearModifierLatches(deviceNum);
                        OnScreenKeyboardState.ClearSelectMode(deviceNum);
                    }
                    break;
            }
            UpdateSelection();
        }

        private void OnSelectionChanged(int device)
        {
            if (device != deviceNum)
                return;
            Dispatcher.BeginInvoke(new Action(UpdateSelection));
        }

        private bool IsModifierKey(OnScreenKeyboardState.KeyKind kind)
        {
            switch (kind)
            {
                case OnScreenKeyboardState.KeyKind.Caps:
                case OnScreenKeyboardState.KeyKind.ShiftHold:
                case OnScreenKeyboardState.KeyKind.CtrlKey:
                case OnScreenKeyboardState.KeyKind.AltKey:
                case OnScreenKeyboardState.KeyKind.WinKey:
                case OnScreenKeyboardState.KeyKind.SelectKey:
                    return true;
                default:
                    return false;
            }
        }

        private void ApplyButton(Button b, OnScreenKeyboardState.KeyDef key, bool shifted, bool shiftOnly, bool isSelected, bool isPressed, bool isActiveModifier)
        {
            // Normal: dark Gruvbox key, cream text, transparent border.
            // Size never changes here: height and font stay fixed for
            // normal, selected, and pressed alike.
            b.Background = new SolidColorBrush(IsModifierKey(key.Kind) ? ModifierIdle : KeyInactive);
            b.Foreground = new SolidColorBrush(Cream);
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.RenderTransform = null;
            b.Height = 72;
            b.FontSize = key.Kind == OnScreenKeyboardState.KeyKind.Emoji || key.Kind == OnScreenKeyboardState.KeyKind.Symbol ? 20 : 14;
            // Icon keys keep their Path content; badge keys keep their
            // Grid content; only Char labels and the Paste/Copy label
            // change with shift state.
            if (key.Kind == OnScreenKeyboardState.KeyKind.Char)
                b.Content = DisplayLabel(key, shifted);
            else if (key.Kind == OnScreenKeyboardState.KeyKind.PasteKey)
                b.Content = shiftOnly ? "Copy" : "Paste";
            if (isActiveModifier)
            {
                // Active modifier: lighter click shade, still readable.
                b.Background = new SolidColorBrush(PressedShade);
            }
            if (isSelected)
            {
                // Selected: shade change only, no enlargement.
                b.Background = new SolidColorBrush(SelectedShade);
                b.BorderBrush = new SolidColorBrush(Accent);
                b.BorderThickness = new Thickness(1);
            }
            if (isPressed)
            {
                // Pressed: dimmer, #3F3F3F, NOT fully black.
                b.Background = new SolidColorBrush(PressedShade);
                b.Foreground = new SolidColorBrush(Cream);
                b.BorderBrush = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
            }
        }

        private void UpdateSelection()
        {
            if (!OnScreenKeyboardState.IsActive(deviceNum))
                return;
            bool emoji = OnScreenKeyboardState.EmojiPanelActive(deviceNum);
            bool symbols = OnScreenKeyboardState.SymbolsPanelActive(deviceNum);
            mainPanel.Visibility = (!emoji && !symbols) ? Visibility.Visible : Visibility.Collapsed;
            emojiPanel.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
            symbolsPanel.Visibility = symbols ? Visibility.Visible : Visibility.Collapsed;
            // No Move position: the window stays bottom-center, always.
            bool shifted = OnScreenKeyboardState.CapsOn(deviceNum) || OnScreenKeyboardState.ShiftLatch(deviceNum) || OnScreenKeyboardState.ShiftHeld(deviceNum);
            bool shiftOnly = OnScreenKeyboardState.ShiftLatch(deviceNum) || OnScreenKeyboardState.ShiftHeld(deviceNum);
            bool capsOn = OnScreenKeyboardState.CapsOn(deviceNum);
            bool shiftActive = OnScreenKeyboardState.ShiftLatch(deviceNum) || OnScreenKeyboardState.ShiftHeld(deviceNum);
            int selR = OnScreenKeyboardState.SelectedRow(deviceNum);
            int selC = OnScreenKeyboardState.SelectedCol(deviceNum);
            if (symbols)
            {
                for (int ci = 0; ci < symbolsButtons.Count; ci++)
                {
                    var key = OnScreenKeyboardState.SymbolsRow[ci];
                    ApplyButton(symbolsButtons[ci], key, false, false, ci == selC, OnScreenKeyboardState.IsPressed(deviceNum, 0, ci, 2), false);
                }
            }
            else if (emoji)
            {
                for (int ci = 0; ci < emojiButtons.Count; ci++)
                {
                    var key = OnScreenKeyboardState.EmojiRow[ci];
                    ApplyButton(emojiButtons[ci], key, false, false, ci == selC, OnScreenKeyboardState.IsPressed(deviceNum, 0, ci, 1), false);
                }
            }
            else
            {
                for (int ri = 0; ri < rowButtons.Count; ri++)
                {
                    for (int ci = 0; ci < rowButtons[ri].Count; ci++)
                    {
                        var key = OnScreenKeyboardState.Rows[ri][ci];
                        bool isMod = (key.Kind == OnScreenKeyboardState.KeyKind.Caps && capsOn) ||
                                     (key.Kind == OnScreenKeyboardState.KeyKind.ShiftHold && shiftActive) ||
                                     (key.Kind == OnScreenKeyboardState.KeyKind.CtrlKey && OnScreenKeyboardState.CtrlLatch(deviceNum)) ||
                                     (key.Kind == OnScreenKeyboardState.KeyKind.AltKey && OnScreenKeyboardState.AltLatch(deviceNum)) ||
                                     (key.Kind == OnScreenKeyboardState.KeyKind.WinKey && OnScreenKeyboardState.WinLatch(deviceNum)) ||
                                     (key.Kind == OnScreenKeyboardState.KeyKind.SelectKey && OnScreenKeyboardState.SelectModeOn(deviceNum));
                        ApplyButton(rowButtons[ri][ci], key, shifted, shiftOnly, ri == selR && ci == selC, OnScreenKeyboardState.IsPressed(deviceNum, ri, ci, 0), isMod);
                    }
                }
            }
        }
    }

    public static class OnScreenKeyboardWindowHost
    {
        // REAL Python DualTouch keyboard host. The old WPF
        // OnScreenKeyboardWindow is NO LONGER OPENED: ShowForDevice is
        // never called from here anymore, so there is no fallback to
        // the C# port window. Keyboard open starts the real Python
        // process via DualTouchKeyboardRunner; keyboard close stops
        // it. If the runner refuses (prompt unanswered, install
        // running/failed, manual runtime missing), the keyboard state
        // is closed immediately and no window appears at all.
        private static bool initialized;
        public static void Initialize()
        {
            if (initialized)
                return;
            initialized = true;
            DualTouchKeyboardRunner.Initialize();
            DualTouchKeyboardRunner.RuntimeError += message =>
            {
                try
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        DualTouchRuntimePrompt.ShowError(message)));
                }
                catch { }
            };
            DualTouchKeyboardRunner.PromptRequested += () =>
            {
                try
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        DualTouchRuntimePrompt.ShowIfUnasked()));
                }
                catch { }
            };
            OnScreenKeyboardState.KeyboardOpened += (device, touchpad) =>
            {
                DualTouchKeyboardRunner.RequestOpen(device);
                try
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (!DualTouchKeyboardRunner.StartForDevice(device))
                        {
                            // No Python keyboard: close the state so
                            // the old WPF window never appears.
                            OnScreenKeyboardState.Close(device);
                        }
                    }));
                }
                catch
                {
                    OnScreenKeyboardState.Close(device);
                }
            };
            OnScreenKeyboardState.KeyboardClosed += device =>
            {
                try { DualTouchKeyboardRunner.StopForDevice(device); } catch { }
                try
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        OnScreenKeyboardWindow.CloseForDevice(device)));
                }
                catch { }
            };
            // Runtime prompt / auto-install at DS4 start flow (not
            // only on first keyboard open). Auto mode starts the
            // background install; Unasked shows the prompt; Manual
            // installs nothing.
            DualTouchKeyboardRunner.EnsureRuntimeAtStartup();
        }
    }
}
