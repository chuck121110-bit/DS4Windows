using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    // Steam one-to-one overlay layout, bigger and full-rectangle.
    // No Selected text at the top at all (Chuck 2026-11-03).
    // No white border: window background and outer border are
    // transparent, normal key borders are transparent too.
    // Selected key: bigger (scale 1.18, larger font) on the light
    // selected background, black text.
    // Pressed key: dimmer, darker shade, black text still readable
    // (Chuck's correction 2026-11-03: dimmer, NOT fully black).
    // Shift / Caps: key labels themselves change, capitals instead
    // of lowercase, shifted symbols instead of numbers, like Steam.
    // Full rectangle: each row is a Grid with star columns, so every
    // row fills the full window width. Space and Shift are wide
    // enough that the bottom rows do not taper in.
    // Actual DS4Windows palette, from DefaultTheme.xaml:
    // Accent #FF0066CC, Border #FFC8C8C8, Surface #FFF5F7FA,
    // Card white, Selected #FFDCEBFF. All normal text is black.
    // Accent is only the selected key border, never a background
    // under black text. Pressed dimmer is #FF8A95A5, black text
    // on it stays readable.
    public partial class OnScreenKeyboardWindow : Window
    {
        private static readonly Dictionary<int, OnScreenKeyboardWindow> openWindows =
            new Dictionary<int, OnScreenKeyboardWindow>();
        private readonly int deviceNum;
        private readonly List<List<Button>> rowButtons = new List<List<Button>>();
        private List<Button> emojiButtons = new List<Button>();
        private StackPanel mainPanel;
        private StackPanel emojiPanel;
        private StackPanel symbolsPanel;
        private List<Button> symbolsButtons = new List<Button>();
        private int lastMovePos = -1;
        private DispatcherTimer pressedTimer;

        public OnScreenKeyboardWindow(int device)
        {
            InitializeComponent();
            deviceNum = device;
            BuildKeys();
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Height - Height - 20;
            lastMovePos = 0;
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

        // Star units per row, so each row Grid fills the full width.
        private static double UnitsForKind(OnScreenKeyboardState.KeyKind kind)
        {
            switch (kind)
            {
                case OnScreenKeyboardState.KeyKind.Backspace:
                case OnScreenKeyboardState.KeyKind.Enter:
                    return 1.6;
                case OnScreenKeyboardState.KeyKind.Tab:
                    return 1.4;
                case OnScreenKeyboardState.KeyKind.Caps:
                    return 1.7;
                case OnScreenKeyboardState.KeyKind.ShiftHold:
                    return 1.9;
                case OnScreenKeyboardState.KeyKind.Space:
                    return 5.0;
                case OnScreenKeyboardState.KeyKind.Move:
                    return 2.2;
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

        private Button MakeButton(OnScreenKeyboardState.KeyDef key, int row, int col, int panel)
        {
            var btn = new Button
            {
                Content = key.Label,
                Height = 56,
                Margin = new Thickness(2),
                FontSize = 14,
                Tag = key,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
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

        private void OnButtonClicked(OnScreenKeyboardState.KeyDef key)
        {
            switch (key.Kind)
            {
                case OnScreenKeyboardState.KeyKind.Hide:
                    OnScreenKeyboardState.Close(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Move:
                    OnScreenKeyboardState.CycleMovePosition(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Tab:
                    Mapping.TapMultiKeys(new List<ushort> { 0x09 }, DS4KeyType.None, false);
                    OnScreenKeyboardState.ClearShiftLatch(deviceNum);
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
                case OnScreenKeyboardState.KeyKind.EmojiToggle:
                    OnScreenKeyboardState.ToggleEmojiPanel(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Emoji:
                    OnScreenKeyboardState.SendUnicodeText(key.Text);
                    OnScreenKeyboardState.ToggleEmojiPanel(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Space:
                    Mapping.TapMultiKeys(new List<ushort> { 0x20 }, DS4KeyType.None, false);
                    OnScreenKeyboardState.ClearShiftLatch(deviceNum);
                    break;
                case OnScreenKeyboardState.KeyKind.Backspace:
                    Mapping.TapMultiKeys(new List<ushort> { 0x08 }, DS4KeyType.None, false);
                    break;
                case OnScreenKeyboardState.KeyKind.Enter:
                    Mapping.TapMultiKeys(new List<ushort> { 0x0D }, DS4KeyType.None, false);
                    break;
                default:
                    if (key.Vk != 0)
                    {
                        bool shifted = OnScreenKeyboardState.CapsOn(deviceNum) || OnScreenKeyboardState.ShiftLatch(deviceNum);
                        if (shifted && key.Kind == OnScreenKeyboardState.KeyKind.Char)
                            Mapping.TapMultiKeys(new List<ushort> { 0x10, key.Vk }, DS4KeyType.None, false);
                        else
                            Mapping.TapMultiKeys(new List<ushort> { key.Vk }, DS4KeyType.None, false);
                        OnScreenKeyboardState.ClearShiftLatch(deviceNum);
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

        private void ApplyButton(Button b, OnScreenKeyboardState.KeyDef key, bool shifted, bool isSelected, bool isPressed, bool isActiveModifier)
        {
            // Normal: white key, black text, transparent border.
            b.Background = Brushes.White;
            b.Foreground = Brushes.Black;
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.RenderTransform = null;
            b.Height = 56;
            b.FontSize = key.Kind == OnScreenKeyboardState.KeyKind.Emoji || key.Kind == OnScreenKeyboardState.KeyKind.Symbol ? 20 : 14;
            if (key.Kind == OnScreenKeyboardState.KeyKind.Char)
                b.Content = DisplayLabel(key, shifted);
            else if (key.Kind != OnScreenKeyboardState.KeyKind.Emoji && key.Kind != OnScreenKeyboardState.KeyKind.Symbol)
                b.Content = key.Label;
            if (isActiveModifier)
            {
                // Caps / Shift active: dimmer, darker shade, black text.
                b.Background = new SolidColorBrush(Color.FromRgb(0xB8, 0xC6, 0xD6));
            }
            if (isSelected)
            {
                // Selected key gets bigger: scale up, larger font, taller.
                b.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xEB, 0xFF));
                b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xCC));
                b.BorderThickness = new Thickness(2);
                b.RenderTransform = new ScaleTransform(1.18, 1.18);
                b.RenderTransformOrigin = new Point(0.5, 0.5);
                b.Height = 68;
                b.FontSize = key.Kind == OnScreenKeyboardState.KeyKind.Emoji || key.Kind == OnScreenKeyboardState.KeyKind.Symbol ? 23 : 17;
            }
            if (isPressed)
            {
                // Pressed key: dimmer, darker shade, black text readable.
                // NOT fully black, per Chuck's correction.
                b.Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x95, 0xA5));
                b.Foreground = Brushes.Black;
                b.BorderBrush = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
                b.RenderTransform = new ScaleTransform(1.18, 1.18);
                b.RenderTransformOrigin = new Point(0.5, 0.5);
                b.Height = 68;
                b.FontSize = key.Kind == OnScreenKeyboardState.KeyKind.Emoji || key.Kind == OnScreenKeyboardState.KeyKind.Symbol ? 23 : 17;
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
            int mp = OnScreenKeyboardState.MovePosition(deviceNum);
            if (mp != lastMovePos)
            {
                lastMovePos = mp;
                double waW = SystemParameters.WorkArea.Width;
                double waH = SystemParameters.WorkArea.Height;
                Left = (waW - Width) / 2;
                Top = mp == 1 ? (waH - Height) / 2 : (mp == 2 ? 20 : waH - Height - 20);
            }
            bool shifted = OnScreenKeyboardState.CapsOn(deviceNum) || OnScreenKeyboardState.ShiftLatch(deviceNum) || OnScreenKeyboardState.ShiftHeld(deviceNum);
            bool capsOn = OnScreenKeyboardState.CapsOn(deviceNum);
            bool shiftActive = OnScreenKeyboardState.ShiftLatch(deviceNum) || OnScreenKeyboardState.ShiftHeld(deviceNum);
            int selR = OnScreenKeyboardState.SelectedRow(deviceNum);
            int selC = OnScreenKeyboardState.SelectedCol(deviceNum);
            if (symbols)
            {
                for (int ci = 0; ci < symbolsButtons.Count; ci++)
                {
                    var key = OnScreenKeyboardState.SymbolsRow[ci];
                    ApplyButton(symbolsButtons[ci], key, false, ci == selC, OnScreenKeyboardState.IsPressed(deviceNum, 0, ci, 2), false);
                }
            }
            else if (emoji)
            {
                for (int ci = 0; ci < emojiButtons.Count; ci++)
                {
                    var key = OnScreenKeyboardState.EmojiRow[ci];
                    ApplyButton(emojiButtons[ci], key, false, ci == selC, OnScreenKeyboardState.IsPressed(deviceNum, 0, ci, 1), false);
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
                                     (key.Kind == OnScreenKeyboardState.KeyKind.ShiftHold && shiftActive);
                        ApplyButton(rowButtons[ri][ci], key, shifted, ri == selR && ci == selC, OnScreenKeyboardState.IsPressed(deviceNum, ri, ci, 0), isMod);
                    }
                }
            }
        }
    }

    public static class OnScreenKeyboardWindowHost
    {
        private static bool initialized;
        public static void Initialize()
        {
            if (initialized)
                return;
            initialized = true;
            OnScreenKeyboardState.KeyboardOpened += (device, touchpad) =>
            {
                try
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        OnScreenKeyboardWindow.ShowForDevice(device)));
                }
                catch { }
            };
            OnScreenKeyboardState.KeyboardClosed += device =>
            {
                try
                {
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        OnScreenKeyboardWindow.CloseForDevice(device)));
                }
                catch { }
            };
        }
    }
}
