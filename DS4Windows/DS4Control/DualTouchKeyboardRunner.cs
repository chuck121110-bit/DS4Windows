using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    // REAL Python DualTouch keyboard runner. Replaces the C# port window:
    // the old WPF OnScreenKeyboardWindow is no longer opened at all (see
    // OnScreenKeyboardWindowHost). The keyboard that opens is the real
    // Python process DS4Windows/DualTouchKeyboard/ds4_keyboard.py, fed by
    // DS4 DualSense events over its stdin (one JSON object per line).
    //
    // ARCHITECTURE, stated plainly: DualTouch's full triton stack reads
    // a Steam Controller over HID directly and cannot be fed a DualSense
    // without adaptation. This runner does NOT run triton, does NOT open
    // a Steam Controller HID device, and does NOT ship SDL DLLs. It runs
    // a DS4-adapted Python keyboard built from DualTouch's real layout
    // YAML, Gruvbox skin, and fonts, fed by DS4 DualSense events.
    // Files reused / stripped / new are listed in
    // DualTouchKeyboard/README-DS4.md.
    //
    // RUNTIME, in order (all done by THIS code, never by a manual SSH
    // pip install):
    //  1. At DS4 start, if the user has not chosen yet, DS4 logs
    //     "DualTouchRuntime: prompt required" and shows a non-modal
    //     prompt: auto-install the runtime with DS4, or install it
    //     yourself. Nothing is installed before the user chooses.
    //  2. Auto: DS4 installs with DS4 at start flow (background task,
    //     not only on first keyboard open): it finds or downloads
    //     Python, then runs pip for Pillow, pynput, hidapi, PyYAML,
    //     pystray, psutil, vdf, then verifies the imports.
    //  3. Manual: DS4 installs nothing and tells the user exactly what
    //     to install. Keyboard open then verifies the imports; if they
    //     are missing, DS4 shows an error and opens NO keyboard.
    //  4. Auto-install failure or offline: error is shown and stored,
    //     and keyboard open is refused. There is NO fallback to the
    //     old C# keyboard, ever. The old WPF window code stays in the
    //     tree but is never opened by the host.
    //
    // No network use except the Auto path's own downloads (python.org
    // embeddable Python + get-pip.py, and PyPI packages via pip).
    // No autostart, no registry Run key, no tray icon, no Steam file
    // editing, no scheduled task creation for the keyboard itself.
    // The keyboard process is started on keyboard open and stopped on
    // keyboard close (or when DS4 closes it), and is killed if it does
    // not exit within 2 seconds of a close message.
    public static class DualTouchKeyboardRunner
    {
        public enum RuntimeChoice { Unasked, Auto, Manual }

        public static readonly string[] PipPackages =
        {
            "Pillow", "pynput", "hidapi", "PyYAML", "pystray", "psutil", "vdf"
        };

        // The keyboard itself uses only PyYAML (layout) + pynput
        // (typing) + tkinter (stdlib). Pillow, hidapi, pystray, psutil
        // and vdf are installed anyway because they are DualTouch's
        // listed requirements; they are NOT used by ds4_keyboard.py
        // (no tray, no Steam HID, no Steam shortcut editing).
        private const string VerifyImports =
            "import PIL, pynput, hid, yaml, pystray, psutil, vdf";

        private const string PythonEmbeddableUrl =
            "https://www.python.org/ftp/python/3.12.8/python-3.12.8-embed-amd64.zip";
        private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";

        public static event Action<string> RuntimeError;
        public static event Action PromptRequested;

        private static readonly object gate = new object();
        private static readonly Process[] procs =
            new Process[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly StreamWriter[] stdinWriters =
            new StreamWriter[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] pending =
            new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] running =
            new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly DateTime[] lastNav =
            new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] lastNavDx =
            new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] lastNavDy =
            new int[Global.MAX_DS4_CONTROLLER_COUNT];

        private static bool initialized;
        private static bool installRunning;
        private static bool installOk;
        private static string lastError;
        private static bool promptRaisedThisStart;

        public static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "DS4Windows");
        public static string RuntimeDir =>
            Path.Combine(AppDataDir, "DualTouchRuntime");
        public static string ChoiceFile =>
            Path.Combine(AppDataDir, "DualTouchRuntime.json");
        public static string LogFile =>
            Path.Combine(AppDataDir, "Logs", "dualtouch-keyboard.log");

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
                File.AppendAllText(LogFile,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    " " + message + Environment.NewLine);
            }
            catch { }
        }

        private static void ReportError(string message)
        {
            lastError = message;
            Log("ERROR: " + message);
            try { RuntimeError?.Invoke(message); } catch { }
        }

        public static RuntimeChoice GetChoice()
        {
            try
            {
                if (!File.Exists(ChoiceFile)) return RuntimeChoice.Unasked;
                string text = File.ReadAllText(ChoiceFile);
                if (text.Contains("Choice=Auto")) return RuntimeChoice.Auto;
                if (text.Contains("Choice=Manual")) return RuntimeChoice.Manual;
            }
            catch { }
            return RuntimeChoice.Unasked;
        }

        public static void SetChoice(RuntimeChoice choice)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(ChoiceFile,
                    "Choice=" + choice.ToString() + Environment.NewLine +
                    "UpdatedUtc=" + DateTime.UtcNow.ToString("o") +
                    Environment.NewLine);
            }
            catch (Exception ex) { Log("choice save failed: " + ex.Message); }
            Log("DualTouchRuntime: choice=" + choice.ToString());
        }

        public static string ManualInstructions()
        {
            return "DS4 DualTouch keyboard needs Python 3.12 and these " +
                "packages, installed by you (DS4 installs nothing in " +
                "Manual mode):" + Environment.NewLine + Environment.NewLine +
                "1. Install Python 3.12 from python.org (tick 'Add " +
                "python.exe to PATH')." + Environment.NewLine +
                "2. Run in a terminal:" + Environment.NewLine +
                "   python -m pip install " +
                string.Join(" ", PipPackages) + Environment.NewLine +
                Environment.NewLine +
                "Then open the DS4 keyboard again. If anything is " +
                "missing, DS4 shows an error and opens no keyboard.";
        }

        public static string StatusLine()
        {
            return "choice=" + GetChoice().ToString() +
                " installOk=" + installOk.ToString() +
                " installRunning=" + installRunning.ToString() +
                " lastError=" + (lastError ?? "(none)") +
                " python=" + (FindPython() ?? "(not found)");
        }

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                OnScreenKeyboardState.TouchDelta += (device, dx, dy) =>
                {
                    if (IsRunning(device)) SendTouchDelta(device, dx, dy);
                };
            }
            catch { }
        }

        // Called at DS4 start (from the keyboard host, after the main
        // window is shown). Never installs before the user chooses.
        public static void EnsureRuntimeAtStartup()
        {
            RuntimeChoice choice = GetChoice();
            Log("DualTouchRuntime: startup " + StatusLine());
            if (choice == RuntimeChoice.Unasked)
            {
                Log("DualTouchRuntime: prompt required, choice=Unasked");
                if (!promptRaisedThisStart)
                {
                    promptRaisedThisStart = true;
                    try { PromptRequested?.Invoke(); } catch { }
                }
                return;
            }
            if (choice == RuntimeChoice.Manual)
            {
                Log("DualTouchRuntime: manual mode, DS4 installs nothing. " +
                    ManualInstructions().Replace(Environment.NewLine, " "));
                return;
            }
            // Auto: install with DS4 at start flow, in the background.
            StartInstallIfNeeded();
        }

        public static void StartInstallIfNeeded()
        {
            lock (gate)
            {
                if (installRunning || installOk) return;
                installRunning = true;
            }
            Log("DualTouchRuntime: auto-install started (DS4-owned)");
            Task.Run(() =>
            {
                try
                {
                    string python = FindPython();
                    if (python == null)
                    {
                        Log("DualTouchRuntime: no Python found, " +
                            "downloading embeddable Python 3.12.8");
                        python = DownloadEmbeddablePython();
                    }
                    if (python == null)
                        throw new Exception("could not obtain Python");
                    Log("DualTouchRuntime: using Python at " + python);
                    RunLogged(python, "-m pip install --disable-pip-version-check " +
                        string.Join(" ", PipPackages), 600000);
                    if (!VerifyRuntime(python))
                        throw new Exception(
                            "pip install finished but import check failed; " +
                            "see dualtouch-keyboard.log");
                    lock (gate) { installOk = true; installRunning = false; }
                    Log("DualTouchRuntime: auto-install complete");
                }
                catch (Exception ex)
                {
                    lock (gate) { installRunning = false; installOk = false; }
                    ReportError("DS4 DualTouch auto-install failed: " +
                        ex.Message + " No keyboard will open until this " +
                        "is fixed (pick Auto again or install manually).");
                }
            });
        }

        private static string DownloadEmbeddablePython()
        {
            try
            {
                string dir = Path.Combine(RuntimeDir, "python");
                Directory.CreateDirectory(RuntimeDir);
                Directory.CreateDirectory(dir);
                string zip = Path.Combine(RuntimeDir, "python-embed.zip");
                using (HttpClient http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(5);
                    byte[] data = http.GetByteArrayAsync(
                        PythonEmbeddableUrl).GetAwaiter().GetResult();
                    File.WriteAllBytes(zip, data);
                }
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir, true);
                try { File.Delete(zip); } catch { }
                // Embeddable Python ships with "import site" commented
                // out; pip needs it. Uncomment it if present.
                string pth = Path.Combine(dir, "python312._pth");
                if (File.Exists(pth))
                {
                    string text = File.ReadAllText(pth);
                    if (text.Contains("#import site"))
                        File.WriteAllText(pth,
                            text.Replace("#import site", "import site"));
                }
                string getPip = Path.Combine(RuntimeDir, "get-pip.py");
                using (HttpClient http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMinutes(5);
                    byte[] data = http.GetByteArrayAsync(
                        GetPipUrl).GetAwaiter().GetResult();
                    File.WriteAllBytes(getPip, data);
                }
                string python = Path.Combine(dir, "python.exe");
                RunLogged(python, "\"" + getPip + "\"", 300000);
                return File.Exists(python) ? python : null;
            }
            catch (Exception ex)
            {
                Log("DualTouchRuntime: embeddable Python download failed: " +
                    ex.Message);
                return null;
            }
        }

        private static void RunLogged(string exe, string args, int timeoutMs)
        {
            Log("run: " + exe + " " + args);
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (Process p = Process.Start(psi))
            {
                if (p == null) throw new Exception("could not start " + exe);
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new Exception("timed out: " + exe + " " + args);
                }
                if (!string.IsNullOrWhiteSpace(stdout))
                    Log("stdout: " + Tail(stdout, 2000));
                if (!string.IsNullOrWhiteSpace(stderr))
                    Log("stderr: " + Tail(stderr, 2000));
                if (p.ExitCode != 0)
                    throw new Exception(exe + " exited with code " +
                        p.ExitCode + " (see log tail above)");
            }
        }

        private static string Tail(string text, int max)
        {
            if (text.Length <= max) return text;
            return text.Substring(text.Length - max);
        }

        public static bool VerifyRuntime(string python)
        {
            if (python == null) return false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "-c \"" + VerifyImports + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return false; }
                    if (p.ExitCode != 0) Log("import check failed: " + err);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log("import check error: " + ex.Message);
                return false;
            }
        }

        public static string FindPython()
        {
            List<string> candidates = new List<string>();
            string runtimePython = Path.Combine(RuntimeDir, "python", "python.exe");
            candidates.Add(runtimePython);
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                candidates.Add(Path.Combine(dir.Trim(), "python.exe"));
            }
            string programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            foreach (string root in new[] { programFiles, localAppData })
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(
                        Path.Combine(root, "Python"), "Python3*"))
                        candidates.Add(Path.Combine(dir, "python.exe"));
                    foreach (string dir in Directory.GetDirectories(
                        Path.Combine(localAppData, "Programs", "Python"), "Python3*"))
                        candidates.Add(Path.Combine(dir, "python.exe"));
                }
                catch { }
            }
            foreach (string c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        public static string KeyboardDir()
        {
            List<string> bases = new List<string>();
            try
            {
                bases.Add(AppDomain.CurrentDomain.BaseDirectory);
                bases.Add(Path.GetDirectoryName(
                    Process.GetCurrentProcess().MainModule.FileName));
            }
            catch { }
            foreach (string b in bases)
            {
                if (string.IsNullOrEmpty(b)) continue;
                string d = Path.Combine(b, "keyboard");
                if (File.Exists(Path.Combine(d, "ds4_main.py"))) return d;
                string old = Path.Combine(b, "DualTouchKeyboard");
                if (File.Exists(Path.Combine(old, "ds4_keyboard.py"))) return old;
            }
            return null;
        }

        public static bool IsPending(int device) =>
            device >= 0 && device < pending.Length && pending[device];
        public static bool IsRunning(int device) =>
            device >= 0 && device < running.Length && running[device];

        public static void RequestOpen(int device)
        {
            if (device < 0 || device >= pending.Length) return;
            pending[device] = true;
            Log("DualTouchKeyboard: open requested device=" + device);
        }

        // DS4 overlay fix (2026-11-04): capture the foreground window
        // BEFORE starting Python, so Python can restore focus to it after
        // the keyboard opens. Python captures it too late: by then the
        // keyboard window itself / shell may be foreground. Returns 0 for
        // no window, our own DS4 process, or shell/desktop/Start/Alt-Tab
        // windows (same exclusion set as triton/win_focus.py).
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindowNative();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessIdNative(IntPtr hWnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassNameNative(IntPtr hWnd, System.Text.StringBuilder buf, int max);

        private static long CaptureFocusRestoreTarget()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindowNative();
                if (hwnd == IntPtr.Zero) return 0;
                uint pid;
                GetWindowThreadProcessIdNative(hwnd, out pid);
                if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id) return 0;
                var buf = new System.Text.StringBuilder(256);
                if (GetClassNameNative(hwnd, buf, buf.Capacity) > 0)
                {
                    string cls = buf.ToString();
                    switch (cls)
                    {
                        case "Progman":
                        case "WorkerW":
                        case "Shell_TrayWnd":
                        case "Shell_SecondaryTrayWnd":
                        case "Windows.UI.Core.CoreWindow":
                        case "ForegroundStaging":
                        case "MultitaskingViewFrame":
                        case "XamlExplorerHostIslandWindow":
                        case "SDL_app":
                            return 0;
                    }
                }
                return hwnd.ToInt64();
            }
            catch { return 0; }
        }

        // Starts the Python keyboard for a device. Returns false when
        // no keyboard may open (prompt needed, install running/failed,
        // manual install missing, script missing). The caller closes
        // the keyboard state on false, so the old WPF window never
        // appears as a fallback.
        public static bool StartForDevice(int device)
        {
            if (device < 0 || device >= pending.Length) return false;
            lock (gate)
            {
                if (running[device])
                {
                    // Re-open while Python is still alive: refresh the
                    // focus target so a new app's window is restored, not
                    // the app from the first open in this session.
                    try { SendJson(device, KeyboardProfileSettings.ForDevice(device).ToRunnerJson(CaptureFocusRestoreTarget())); } catch { }
                    return true;
                }
            }
            RuntimeChoice choice = GetChoice();
            if (choice == RuntimeChoice.Unasked)
            {
                Log("DualTouchKeyboard: prompt required before open, " +
                    "choice=Unasked");
                try { PromptRequested?.Invoke(); } catch { }
                ReportError("Choose how DS4 should install the DualTouch " +
                    "keyboard runtime first (Auto-install with DS4, or " +
                    "install it yourself). No keyboard opens until then.");
                pending[device] = false;
                return false;
            }
            string dir = KeyboardDir();
            if (dir == null)
            {
                pending[device] = false;
                ReportError("DS4 DualTouch keyboard files are missing " +
                    "from the DS4 install folder (DualTouchKeyboard " +
                    "folder). No keyboard opens.");
                return false;
            }
            string python = FindPython();
            bool ready = python != null && VerifyRuntime(python);
            if (!ready)
            {
                if (choice == RuntimeChoice.Auto)
                {
                    StartInstallIfNeeded();
                    pending[device] = false;
                    ReportError("DS4 is auto-installing the DualTouch " +
                        "keyboard runtime in the background. No keyboard " +
                        "opens until it finishes; open the keyboard " +
                        "again after the install completes." +
                        (lastError != null ? " Last error: " + lastError : ""));
                    return false;
                }
                pending[device] = false;
                ReportError("DS4 DualTouch keyboard runtime is missing. " +
                    ManualInstructions());
                return false;
            }
            // Capture BEFORE starting Python: once Python/SDL starts, the
            // foreground may already be the keyboard/shell, not the game.
            long focusRestoreTarget = CaptureFocusRestoreTarget();
            Log("DualTouchKeyboard: focus restore target device=" + device +
                " hwnd=0x" + focusRestoreTarget.ToString("X"));
            try
            {
                string script = File.Exists(Path.Combine(dir, "ds4_main.py")) ? "ds4_main.py" : "ds4_keyboard.py";
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "\"" + Path.Combine(dir, script) + "\"",
                    WorkingDirectory = dir,
                    // Triton on Windows only finds its data via TRITON_DATA:
                    // resources.py uses env paths, then an empty win32 list,
                    // then sys.prefix/share/triton/. The original tray forced
                    // TRITON_DATA before any triton import; DS4 must do the
                    // same, otherwise load_kb_config asserts
                    // "Could not find YAML file keyboard-layout.yaml" even
                    // though data/cfg/keyboard-layout.yaml exists.
                    EnvironmentVariables = { ["TRITON_DATA"] = Path.Combine(dir, "data") },
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                Process p = Process.Start(psi);
                if (p == null) throw new Exception("Process.Start returned null");
                p.EnableRaisingEvents = true;
                int dev = device;
                p.Exited += (s, e) =>
                {
                    Log("DualTouchKeyboard: Python exited device=" + dev +
                        " code=" + p.ExitCode);
                    lock (gate) { running[dev] = false; procs[dev] = null; }
                    if (OnScreenKeyboardState.IsActive(dev))
                    {
                        // Python died: close the keyboard, never fall
                        // back to the old WPF window.
                        try { OnScreenKeyboardState.Close(dev); } catch { }
                    }
                };
                p.OutputDataReceived += (s, e) =>
                { if (e.Data != null) Log("py-out: " + e.Data); };
                p.ErrorDataReceived += (s, e) =>
                { if (e.Data != null) Log("py-err: " + e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                lock (gate)
                {
                    procs[device] = p;
                    stdinWriters[device] = p.StandardInput;
                    running[device] = true;
                    pending[device] = false;
                }
                SendJson(device, "{\"type\": \"open\"}");
                try { SendJson(device, KeyboardProfileSettings.ForDevice(device).ToRunnerJson(focusRestoreTarget)); } catch { }
                Log("DualTouchKeyboard: Python started device=" + device +
                    " pid=" + p.Id + " python=" + python);
                return true;
            }
            catch (Exception ex)
            {
                pending[device] = false;
                ReportError("DS4 could not start the DualTouch Python " +
                    "keyboard: " + ex.Message + " No keyboard opens.");
                return false;
            }
        }

        public static void StopForDevice(int device)
        {
            // INSTANT close (Circle fix): the old code cleared the stdin
            // writer BEFORE sending close, so the close message was
            // never sent, and then it waited a full 2 seconds for the
            // Python process to exit gracefully before killing it.
            // That 2-second wait was the Circle delay. Now: send close
            // on the captured writer first, then Kill immediately, no
            // graceful wait, no fade. KeyboardClosed (mouse/touchpad
            // restore) already fired before this runs, so state is
            // restored at once, not after the kill.
            if (device < 0 || device >= procs.Length) return;
            Process p;
            StreamWriter w;
            lock (gate)
            {
                p = procs[device];
                w = stdinWriters[device];
                procs[device] = null;
                stdinWriters[device] = null;
                running[device] = false;
                pending[device] = false;
            }
            if (p == null) return;
            try { lock (w) { w.WriteLine("{\"type\": \"close\"}"); w.Flush(); } } catch { }
            try { p.Kill(); } catch { }
            try { w?.Dispose(); } catch { }
            Log("DualTouchKeyboard: stopped instantly device=" + device);
        }

        private static void SendJson(int device, string json)
        {
            StreamWriter w;
            lock (gate) { w = stdinWriters[device]; }
            if (w == null) return;
            try { lock (w) { w.WriteLine(json); w.Flush(); } }
            catch (Exception ex)
            {
                Log("DualTouchKeyboard: stdin write failed device=" +
                    device + ": " + ex.Message);
            }
        }

        private static string Esc(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        public static void SendNav(int device, int dx, int dy)
        {
            if (!IsRunning(device)) return;
            if (dx == 0 && dy == 0)
            {
                lastNavDx[device] = 0; lastNavDy[device] = 0;
                return;
            }
            DateTime now = DateTime.UtcNow;
            if (dx != lastNavDx[device] || dy != lastNavDy[device] ||
                (now - lastNav[device]).TotalMilliseconds >= 150)
            {
                lastNavDx[device] = dx; lastNavDy[device] = dy;
                lastNav[device] = now;
                SendJson(device, "{\"type\": \"nav\", \"dx\": " + dx +
                    ", \"dy\": " + dy + "}");
            }
        }

        // Joystick single-step path: bypasses the 150 ms D-pad repeat
        // gate entirely. Mapping only calls this once per stick
        // deflection (after the stick returns to center), so holding
        // the stick cannot repeat. D-pad keeps using SendNav repeat.
        public static void SendNavImmediate(int device, int dx, int dy)
        {
            if (!IsRunning(device)) return;
            SendJson(device, "{\"type\": \"nav\", \"dx\": " + dx +
                ", \"dy\": " + dy + "}");
        }

        public static void SendTouchDelta(int device, double dx, double dy) =>
            SendJson(device, "{\"type\": \"touchmove\", \"dx\": " +
                dx.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ", \"dy\": " +
                dy.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

        public static void SendTap(int device) =>
            SendJson(device, "{\"type\": \"tap\"}");

        public static void SendState(int device, int buttons, int l2, int r2, int lx, int ly)
        {
            SendJson(device, "{\"type\": \"state\", \"buttons\": " + buttons +
                ", \"l2\": " + l2 + ", \"r2\": " + r2 +
                ", \"lx\": " + lx + ", \"ly\": " + ly + "}");
        }

        public static void SendButton(int device, string name) =>
            SendJson(device, "{\"type\": \"button\", \"name\": \"" + Esc(name) + "\"}");

        public static void SendShiftHeld(int device, bool held) =>
            SendJson(device, "{\"type\": \"shift\", \"held\": " +
                (held ? "true" : "false") + "}");
    }
}
