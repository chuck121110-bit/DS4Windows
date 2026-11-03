using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    // Plain placeholder layout for the on-screen keyboard logic build.
    // Layout follows Chuck's pick: default variant, keys a little less
    // wide than default, backspace / enter a little smaller (not super
    // thin, text still fits), emojis / arrows / Hide at the bottom,
    // caps / shift / space row not squished. White/black styling is
    // NOT finalized here.
    // The window never takes focus (WS_EX_NOACTIVATE), so keys typed
    // with the controller go to the text field that was focused before
    // the keyboard opened, not to this window. Mouse clicks on the
    // buttons are for layout checking only; typing is tested with the
    // controller, not by clicking here, because a mouse click does
    // activate the window.
    public partial class OnScreenKeyboardWindow : Window
    {
        private static readonly Dictionary<int, OnScreenKeyboardWindow> openWindows =
            new Dictionary<int, OnScreenKeyboardWindow>();
        private readonly int deviceNum;
        private readonly List<List<Button>> rowButtons = new List<List<Button>>();
        private List<Button> emojiButtons = new List<Button>();
        private StackPanel mainPanel;
        private StackPanel emojiPanel;

        public OnScreenKeyboardWindow(int device)
        {
            InitializeComponent();
            deviceNum = device;
            BuildKeys();
            OnScreenKeyboardState.SelectionChanged += OnSelectionChanged;
            Closed += (s, e) =>
            {
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

        private static double WidthForKind(OnScreenKeyboardState.KeyKind kind)
        {
            switch (kind)
            {
                case OnScreenKeyboardState.KeyKind.Backspace:
                case OnScreenKeyboardState.KeyKind.Enter:
                    return 68;
                case OnScreenKeyboardState.KeyKind.Caps:
                case OnScreenKeyboardState.KeyKind.ShiftHold:
                    return 90;
                case OnScreenKeyboardState.KeyKind.Space:
                    return 260;
                case OnScreenKeyboardState.KeyKind.EmojiToggle:
                case OnScreenKeyboardState.KeyKind.Hide:
                    return 68;
                case OnScreenKeyboardState.KeyKind.Emoji:
                    return 56;
                default:
                    return 52;
            }
        }

        private void BuildKeys()
        {
            mainPanel = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            keysPanel.Children.Add(mainPanel);
            foreach (var row in OnScreenKeyboardState.Rows)
            {
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var buttons = new List<Button>();
                foreach (var key in row)
                {
                    var btn = MakeButton(key, WidthForKind(key.Kind));
                    buttons.Add(btn);
                    rowPanel.Children.Add(btn);
                }
                rowButtons.Add(buttons);
                mainPanel.Children.Add(rowPanel);
            }
            emojiPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
            foreach (var key in OnScreenKeyboardState.EmojiRow)
            {
                var btn = MakeButton(key, WidthForKind(key.Kind));
                btn.FontSize = 20;
                // Emoji buttons show the emoji itself, not the word.
                btn.Content = key.Text;
                emojiButtons.Add(btn);
                emojiPanel.Children.Add(btn);
            }
            keysPanel.Children.Add(emojiPanel);
        }

        private Button MakeButton(OnScreenKeyboardState.KeyDef key, double width)
        {
            var btn = new Button
            {
                Content = key.Label,
                Width = width,
                Height = 44,
                Margin = new Thickness(2),
                FontSize = 13,
                Tag = key,
            };
            btn.Click += (s, e) => OnButtonClicked(key);
            return btn;
        }

        private void OnButtonClicked(OnScreenKeyboardState.KeyDef key)
        {
            // Mouse click only: controller input goes through Mapping.
            // Hide by mouse click closes outright (an explicit click);
            // controller X on Hide still needs Shift held, per Mapping.
            switch (key.Kind)
            {
                case OnScreenKeyboardState.KeyKind.Hide:
                    OnScreenKeyboardState.Close(deviceNum);
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

        private void UpdateSelection()
        {
            if (!OnScreenKeyboardState.IsActive(deviceNum))
                return;
            bool emoji = OnScreenKeyboardState.EmojiPanelActive(deviceNum);
            mainPanel.Visibility = emoji ? Visibility.Collapsed : Visibility.Visible;
            emojiPanel.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
            // Clear all highlights, then highlight the current key.
            foreach (var row in rowButtons)
                foreach (var b in row)
                    b.Background = SystemColors.ControlBrush;
            foreach (var b in emojiButtons)
                b.Background = SystemColors.ControlBrush;
            if (emoji)
            {
                int c = OnScreenKeyboardState.SelectedCol(deviceNum);
                if (c >= 0 && c < emojiButtons.Count)
                    emojiButtons[c].Background = SystemColors.HighlightBrush;
                statusText.Text = "Emoji panel: move with joystick / D-pad, X to type an emoji, Circle to close.";
            }
            else
            {
                int r = OnScreenKeyboardState.SelectedRow(deviceNum);
                int c = OnScreenKeyboardState.SelectedCol(deviceNum);
                if (r >= 0 && r < rowButtons.Count && c >= 0 && c < rowButtons[r].Count)
                    rowButtons[r][c].Background = SystemColors.HighlightBrush;
                var cur = OnScreenKeyboardState.CurrentKey(deviceNum);
                statusText.Text = $"Selected: {cur.Label} | Caps: {(OnScreenKeyboardState.CapsOn(deviceNum) ? "On" : "Off")} | Shift: hold L2, or select Shift for one key | X select, Circle close, Square backspace, L3 caps.";
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
