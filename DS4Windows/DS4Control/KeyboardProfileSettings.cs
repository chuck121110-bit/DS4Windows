using System;
using System.IO;
using System.Text.Json;

namespace DS4Windows
{
    // Per-profile DualTouch keyboard settings, stored in a sidecar JSON
    // next to the profile XML, never inside the profile XML itself, so
    // profile files are never edited by keyboard settings. File name:
    // <ProfileName>.keyboard.json under %APPDATA%\DS4Windows\Profiles\.
    // Defaults match DualTouch's own defaults (appsettings.DEFAULT_SETTINGS
    // plus the DS4 touchpad sensitivity Chuck confirmed at 140).
    public class KeyboardProfileSettings
    {
        public string Skin { get; set; } = "Gruvbox";
        public string Layout { get; set; } = "QWERTY";
        public string Size { get; set; } = "medium";
        public string Transparency { get; set; } = "off";
        public bool Split { get; set; } = false;
        public bool Diacritics { get; set; } = true;
        public bool Liftoff { get; set; } = false;
        public bool KeySound { get; set; } = true;
        public bool Rumble { get; set; } = true;
        public double TouchSensitivity { get; set; } = 140.0;

        public static string SidecarPath(string profileName)
        {
            string dir = Path.Combine(Global.appdatapath, "Profiles");
            return Path.Combine(dir, profileName + ".keyboard.json");
        }

        public static KeyboardProfileSettings Load(string profileName)
        {
            var s = new KeyboardProfileSettings();
            if (string.IsNullOrEmpty(profileName)) return s;
            try
            {
                string path = SidecarPath(profileName);
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<KeyboardProfileSettings>(json);
                    if (loaded != null) s = loaded;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(s.Skin)) s.Skin = "Gruvbox";
            if (string.IsNullOrEmpty(s.Layout)) s.Layout = "QWERTY";
            if (string.IsNullOrEmpty(s.Size)) s.Size = "medium";
            if (string.IsNullOrEmpty(s.Transparency)) s.Transparency = "off";
            if (s.TouchSensitivity <= 0) s.TouchSensitivity = 140.0;
            return s;
        }

        public static KeyboardProfileSettings ForDevice(int device)
        {
            string name = null;
            try { name = Global.ProfilePath[device]; } catch { }
            return Load(name);
        }

        public void Save(string profileName)
        {
            if (string.IsNullOrEmpty(profileName)) return;
            try
            {
                string path = SidecarPath(profileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string json = JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        // One-line JSON sent to the Python keyboard on stdin at open.
        // DS4 overlay fix (2026-11-04): always_visible=True tells Python
        // to create the SDL window visible but parked off-screen, so
        // open/close are only SetWindowPos moves with SWP_NOACTIVATE,
        // never a ShowWindow that steals focus / deselects a text box
        // or closes an overlay. focus_restore_target is the foreground
        // HWND DS4 captured before starting Python (0 = none).
        public string ToRunnerJson()
        {
            return ToRunnerJson(0);
        }

        public string ToRunnerJson(long focusRestoreTarget)
        {
            return "{" +
                "\"type\": \"settings\", " +
                "\"always_visible\": true, " +
                "\"focus_restore_target\": " + focusRestoreTarget + ", " +
                "\"skin\": " + JsonSerializer.Serialize(Skin) + ", " +
                "\"layout\": " + JsonSerializer.Serialize(Layout) + ", " +
                "\"size\": " + JsonSerializer.Serialize(Size) + ", " +
                "\"transparency\": " + JsonSerializer.Serialize(Transparency) + ", " +
                "\"split\": " + (Split ? "true" : "false") + ", " +
                "\"diacritics\": " + (Diacritics ? "true" : "false") + ", " +
                "\"liftoff\": " + (Liftoff ? "true" : "false") + ", " +
                "\"key_sound\": " + (KeySound ? "true" : "false") + ", " +
                "\"rumble\": " + (Rumble ? "true" : "false") + ", " +
                "\"touch_sensitivity\": " + TouchSensitivity.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) +
                "}";
        }
    }
}
