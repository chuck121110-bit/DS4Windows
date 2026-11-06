using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DS4Windows;

namespace DS4WinWPF.DS4Forms
{
    // Non-modal runtime prompt for the real Python DualTouch keyboard.
    // Shown at DS4 start when the user has not chosen yet, and again on
    // keyboard open if still unanswered. It never blocks DS4: the user
    // can answer it whenever, and the keyboard simply does not open
    // until a choice exists. "Ask later" leaves the choice Unasked.
    //
    // STYLING FIX (2026-11-03, after Chuck's photo): the first version
    // of this dialog set NO Background and NO Foreground at all, so it
    // inherited DS4Windows' near-white theme text
    // (DarkTheme ForegroundColor #F4F7FB) on top of the default white
    // WPF window background: white text on a white background,
    // unreadable. That was the bug, stated plainly.
    // This version sets EVERY colour explicitly, so no theme
    // inheritance can break it again:
    //   Window / panel background: black #000000 (Chuck's ask)
    //   All text: white #FFFFFF (never black, never theme-inherited)
    //   DS4 blue accents, taken verbatim from DS4Forms/Themes/
    //   DarkTheme.xaml: AccentColor #2C7DF7, SecondaryColor /
    //   NavigationSelectionColor #153B6E, BorderColor #223041.
    //   Auto button: accent blue #2C7DF7 background, white text.
    //   Manual and Ask-later buttons: secondary blue #153B6E
    //   background, accent blue #2C7DF7 border, white text.
    // No behaviour changed here: same three options, same choice
    // handling, same auto-install / manual paths as before.
    public static class DualTouchRuntimePrompt
    {
        private static readonly SolidColorBrush Black =
            new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
        private static readonly SolidColorBrush White =
            new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush Ds4Accent =
            new SolidColorBrush(Color.FromRgb(0x2C, 0x7D, 0xF7));
        private static readonly SolidColorBrush Ds4SecondaryBlue =
            new SolidColorBrush(Color.FromRgb(0x15, 0x3B, 0x6E));
        private static readonly SolidColorBrush Ds4Border =
            new SolidColorBrush(Color.FromRgb(0x22, 0x30, 0x41));

        private static Window openPrompt;

        private static Button BlueButton(string content, bool primary)
        {
            return new Button
            {
                Content = content,
                Height = 32,
                Background = primary ? Ds4Accent : Ds4SecondaryBlue,
                Foreground = White,
                BorderBrush = primary ? Ds4Accent : Ds4Accent,
                BorderThickness = new Thickness(1),
            };
        }

        public static void ShowIfUnasked()
        {
            try
            {
                if (DualTouchKeyboardRunner.GetChoice() !=
                    DualTouchKeyboardRunner.RuntimeChoice.Unasked)
                    return;
                if (openPrompt != null) { openPrompt.Activate(); return; }
                var win = new Window
                {
                    Title = "DS4 DualTouch keyboard runtime",
                    Width = 520,
                    Height = 320,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize,
                    Background = Black,
                    Foreground = White,
                };
                // Plain StackPanel on purpose: no DockPanel attached
                // properties, no theme resources, no XAML. Every
                // element below gets an explicit black/blue/white
                // brush, so no white body area can survive.
                var outer = new StackPanel { Background = Black };
                outer.Children.Add(new Border
                {
                    Height = 4,
                    Background = Ds4Accent,
                });
                var panel = new StackPanel
                {
                    Margin = new Thickness(16),
                    Background = Black,
                };
                panel.Children.Add(new TextBlock
                {
                    Text = "DS4 DualTouch keyboard needs a Python runtime.",
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = White,
                    Background = Black,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "The keyboard is the real Python DualTouch keyboard. " +
                        "DS4 can auto-install Python and its packages with DS4 " +
                        "at start, or you can install them yourself and DS4 " +
                        "will install nothing. Nothing is installed until you choose.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 16),
                    Foreground = White,
                    Background = Black,
                });
                var autoBtn = BlueButton("Auto-install with DS4", true);
                autoBtn.Margin = new Thickness(0, 0, 0, 8);
                autoBtn.Click += (s, e) =>
                {
                    DualTouchKeyboardRunner.SetChoice(
                        DualTouchKeyboardRunner.RuntimeChoice.Auto);
                    DualTouchKeyboardRunner.EnsureRuntimeAtStartup();
                    win.Close();
                };
                var manualBtn = BlueButton("I'll install it myself", false);
                manualBtn.Margin = new Thickness(0, 0, 0, 8);
                manualBtn.Click += (s, e) =>
                {
                    DualTouchKeyboardRunner.SetChoice(
                        DualTouchKeyboardRunner.RuntimeChoice.Manual);
                    MessageBox.Show(win,
                        DualTouchKeyboardRunner.ManualInstructions(),
                        "DS4 DualTouch keyboard: manual install",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    win.Close();
                };
                var laterBtn = BlueButton("Ask later", false);
                laterBtn.Click += (s, e) => win.Close();
                panel.Children.Add(autoBtn);
                panel.Children.Add(manualBtn);
                panel.Children.Add(laterBtn);
                outer.Children.Add(panel);
                win.Content = outer;
                win.Closed += (s, e) => { if (openPrompt == win) openPrompt = null; };
                openPrompt = win;
                win.Show();
            }
            catch { }
        }

        public static void ShowError(string message)
        {
            try
            {
                MessageBox.Show(message, "DS4 DualTouch keyboard",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }
    }
}
