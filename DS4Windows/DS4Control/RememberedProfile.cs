using System;
using System.IO;
using System.Xml.Linq;

namespace DS4Windows
{
    // Remembered profile, 2026-10-30: last manually selected profile per
    // slot, persisted across DS4 restart, PC restart, and controller
    // disconnect/reconnect. Linked profiles still win on connect.
    internal static class RememberedProfile
    {
        private static readonly object LockObj = new object();

        private static string FilePath
        {
            get
            {
                try { return Path.Combine(Global.appdatapath, "LastProfilePerSlot.xml"); }
                catch { return null; }
            }
        }

        public static string Load(int device)
        {
            try
            {
                string path = FilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var doc = XDocument.Load(path);
                foreach (var slot in doc.Root.Elements("Slot"))
                {
                    var idx = slot.Attribute("index");
                    if (idx != null && int.TryParse(idx.Value, out int i) && i == device)
                    {
                        string name = slot.Value?.Trim();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void Save(int device, string profileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(profileName)) return;
                lock (LockObj)
                {
                    string path = FilePath;
                    if (string.IsNullOrEmpty(path)) return;
                    string dir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var doc = new XDocument(new XElement("LastProfilePerSlot"));
                    // Preserve other slots
                    if (File.Exists(path))
                    {
                        try
                        {
                            var old = XDocument.Load(path);
                            foreach (var slot in old.Root.Elements("Slot"))
                            {
                                var idx = slot.Attribute("index");
                                if (idx != null && int.TryParse(idx.Value, out int i) && i != device)
                                    doc.Root.Add(new XElement("Slot", new XAttribute("index", i), slot.Value));
                            }
                        }
                        catch { }
                    }
                    doc.Root.Add(new XElement("Slot", new XAttribute("index", device), profileName));
                    string tmp = path + ".tmp";
                    doc.Save(tmp);
                    File.Move(tmp, path, true);
                }
            }
            catch { }
        }
    }
}
