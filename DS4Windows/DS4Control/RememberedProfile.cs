// Remembered last profile per controller slot, 2026-10-30.
// DS4 previously reset ProfilePath to OlderProfilePath (the slot default
// from Profiles.xml, Xbox One for slot 1) on every connect, so a manual
// switch to DualSense was lost on disconnect, PC restart, or DS4 restart,
// and the connect notification then correctly reported the profile that
// was actually loaded, Xbox One, which was not the profile Chuck was on.
// This helper persists the last manually switched profile per slot in
// %AppData%\DS4Windows\LastProfilePerSlot.xml, separate from Profiles.xml,
// so the slot default itself is never hardcoded to DualSense. On connect,
// the remembered profile is used only if its .xml still exists; otherwise
// the slot default is used. Auto-profiles are never saved here (see call
// sites: only the manual switch worker and the named regular load save),
// and DualSense mute on/off profiles are skipped, so a temporary mute
// switch does not become the remembered profile.
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace DS4Windows
{
    internal static class RememberedProfile
    {
        private static readonly object fileLock = new object();
        private static readonly string[] lastConnectSource = new string[Global.MAX_DS4_CONTROLLER_COUNT];

        internal static string FilePath()
        {
            try
            {
                string basePath = Global.appdatapath;
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS4Windows");
                return Path.Combine(basePath, "LastProfilePerSlot.xml");
            }
            catch { return null; }
        }

        internal static string ProfilesFolder()
        {
            try
            {
                string basePath = Global.appdatapath;
                if (string.IsNullOrWhiteSpace(basePath))
                    basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DS4Windows");
                return Path.Combine(basePath, "Profiles");
            }
            catch { return null; }
        }

        internal static bool ProfileFileExists(string profileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(profileName)) return false;
                string folder = ProfilesFolder();
                return folder != null && File.Exists(Path.Combine(folder, profileName + ".xml"));
            }
            catch { return false; }
        }

        internal static string Load(int slot)
        {
            try
            {
                if (slot < 0 || slot >= Global.MAX_DS4_CONTROLLER_COUNT) return string.Empty;
                string path = FilePath();
                if (path == null || !File.Exists(path)) return string.Empty;
                var doc = new XmlDocument();
                doc.Load(path);
                var node = doc.SelectSingleNode($"/LastProfiles/Slot[@index='{slot}']");
                string name = node?.InnerText?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || name == "(none)") return string.Empty;
                return ProfileFileExists(name) ? name : string.Empty;
            }
            catch { return string.Empty; }
        }

        internal static void Save(int slot, string profileName)
        {
            try
            {
                if (slot < 0 || slot >= Global.MAX_DS4_CONTROLLER_COUNT) return;
                if (string.IsNullOrWhiteSpace(profileName) || profileName == "(none)") return;
                if (!ProfileFileExists(profileName)) return;
                // Skip DualSense mute profiles: they are temporary mute
                // switches, not a manual profile choice to remember.
                try
                {
                    if (Global.DualSenseMuteOnProfileName != null && slot < Global.DualSenseMuteOnProfileName.Length && string.Equals(Global.DualSenseMuteOnProfileName[slot], profileName, StringComparison.Ordinal)) return;
                    if (Global.DualSenseMuteOffProfileName != null && slot < Global.DualSenseMuteOffProfileName.Length && string.Equals(Global.DualSenseMuteOffProfileName[slot], profileName, StringComparison.Ordinal)) return;
                }
                catch { }
                lock (fileLock)
                {
                    var values = new Dictionary<int, string>();
                    string path = FilePath();
                    if (path != null && File.Exists(path))
                    {
                        try
                        {
                            var existing = new XmlDocument();
                            existing.Load(path);
                            foreach (XmlNode n in existing.SelectNodes("/LastProfiles/Slot"))
                            {
                                if (n.Attributes != null && n.Attributes["index"] != null && int.TryParse(n.Attributes["index"].Value, out int idx))
                                    values[idx] = n.InnerText?.Trim() ?? string.Empty;
                            }
                        }
                        catch { }
                    }
                    string previous = values.TryGetValue(slot, out string prev) ? prev : string.Empty;
                    if (string.Equals(previous, profileName, StringComparison.Ordinal)) return;
                    values[slot] = profileName;
                    var doc = new XmlDocument();
                    var root = doc.CreateElement("LastProfiles");
                    doc.AppendChild(root);
                    foreach (var kv in values)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                        var el = doc.CreateElement("Slot");
                        el.SetAttribute("index", kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        el.InnerText = kv.Value;
                        root.AppendChild(el);
                    }
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                    string tmp = path + ".tmp";
                    doc.Save(tmp);
                    File.Move(tmp, path, true);
                    AppLogger.LogToGui($"Remembered profile saved for controller {slot + 1}: {profileName} (source profile switch)", false);
                }
            }
            catch (Exception ex)
            {
                try { AppLogger.LogToGui($"Remembered profile save failed for controller {slot + 1}: {ex.GetType().Name}", true); } catch { }
            }
        }

        internal static void SetConnectSource(int slot, string source)
        {
            try { if (slot >= 0 && slot < lastConnectSource.Length) lastConnectSource[slot] = source ?? string.Empty; } catch { }
        }

        internal static string GetConnectSource(int slot)
        {
            try { return (slot >= 0 && slot < lastConnectSource.Length) ? (lastConnectSource[slot] ?? string.Empty) : string.Empty; }
            catch { return string.Empty; }
        }

        // Resolve the profile to load on connect. Linked profiles keep
        // precedence (explicit per-device binding). Otherwise the
        // remembered profile wins over the slot default, if it exists.
        internal static string ResolveForConnect(int slot, string slotDefault, bool hasLinkedProfile, string linkedProfile)
        {
            if (hasLinkedProfile && !string.IsNullOrWhiteSpace(linkedProfile))
            {
                SetConnectSource(slot, "linked profile");
                return linkedProfile;
            }
            string remembered = Load(slot);
            if (!string.IsNullOrWhiteSpace(remembered))
            {
                SetConnectSource(slot, "remembered last profile");
                return remembered;
            }
            SetConnectSource(slot, "slot default");
            return slotDefault;
        }
    }
}
