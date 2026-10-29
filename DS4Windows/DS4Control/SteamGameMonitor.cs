using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// Monitors Steam game overlay process to detect when a Steam game closes.
    /// When detected and the profile setting is enabled, resets the DualSense
    /// light bar back to the configured color.
    /// </summary>
    public static class SteamGameMonitor
    {
        private static Thread monitorThread;
        private static volatile bool running = false;
        private static bool wasOverlayRunning = false;
        private static ControlService controlService;

        private const int PollIntervalMs = 5000;

        public static void Start(ControlService service)
        {
            if (running) return;
            controlService = service;
            running = true;
            wasOverlayRunning = IsSteamOverlayRunning();
            AppLogger.LogToGui("SteamGameMonitor: Started (overlay running: " + wasOverlayRunning + ")", false);
            monitorThread = new Thread(MonitorLoop)
            {
                IsBackground = true,
                Name = "SteamGameMonitor"
            };
            monitorThread.Start();
        }

        public static void Stop()
        {
            running = false;
            if (monitorThread != null && monitorThread.IsAlive)
            {
                monitorThread.Join(1000);
            }
            monitorThread = null;
            controlService = null;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle,
            int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        private static Process GetParentProcess(Process process)
        {
            try
            {
                PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
                int returnLength;
                int status = NtQueryInformationProcess(process.Handle, 0,
                    ref pbi, Marshal.SizeOf(pbi), out returnLength);
                if (status != 0)
                    return null;
                int parentPid = pbi.InheritedFromUniqueProcessId.ToInt32();
                return Process.GetProcessById(parentPid);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSteamOverlayRunning()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("GameOverlayUI");
                if (procs.Length == 0)
                    return false;

                // Verify Steam is running (GameOverlayUI only exists for Steam games)
                Process[] steamProcs = Process.GetProcessesByName("steam");
                if (steamProcs.Length == 0)
                    return false;

                // Try to verify parent is steam.exe, but don't fail if lookup doesn't work
                // GameOverlayUI.exe is Steam-specific, so its presence + steam.exe running
                // is sufficient evidence of a Steam game
                foreach (Process p in procs)
                {
                    try
                    {
                        Process parent = GetParentProcess(p);
                        if (parent != null &&
                            parent.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                // Fallback: GameOverlayUI running + steam.exe running = Steam game
                // (parent lookup may fail due to permissions, but the combination is Steam-specific)
                return true;
            }
            catch
            {
                return false;
            }
        }



        private static void MonitorLoop()
        {
            while (running)
            {
                try
                {
                    bool isRunning = IsSteamOverlayRunning();
                    
                    // Detect transition: was running, now not running = game closed
                    if (wasOverlayRunning && !isRunning)
                    {
                        AppLogger.LogToGui("SteamGameMonitor: Steam game closed, resetting lightbar", false);
                        OnSteamGameClosed();
                    }
                    else if (!wasOverlayRunning && isRunning)
                    {
                        AppLogger.LogToGui("SteamGameMonitor: Steam game started", false);
                    }
                    
                    wasOverlayRunning = isRunning;
                }
                catch
                {
                    // Ignore errors in monitor loop
                }
                
                Thread.Sleep(PollIntervalMs);
            }
        }

        private static void OnSteamGameClosed()
        {
            try
            {
                if (controlService == null) return;

                // Check if any connected device has the setting enabled
                bool shouldReset = false;
                for (int i = 0; i < Global.MAX_DS4_CONTROLLER_COUNT; i++)
                {
                    if (i < Global.ResetLightbarOnGameClose.Length && 
                        Global.ResetLightbarOnGameClose[i])
                    {
                        shouldReset = true;
                        break;
                    }
                }

                if (!shouldReset)
                {
                    AppLogger.LogToGui("SteamGameMonitor: WARNING Steam game closed, but ResetLightbarOnGameClose is not enabled for any controller slot, lightbar not reset", false);
                    return;
                }

                // Reset lightbar for each connected DualSense/DualShock.
                // Hold the forced color for about 1.5 seconds with
                // repeated forced resends, so a single update that Steam
                // overwrites (or that DS4 deduplicates) cannot leave the
                // physical LED on Steam's color, then release to the
                // normal profile routine with a final forced resend.
                for (int i = 0; i < controlService.DS4Controllers.Length; i++)
                {
                    DS4Device device = controlService.DS4Controllers[i];
                    if (device != null && device.IsAlive())
                    {
                        try
                        {
                            LightbarSettingInfo lbInfo = Global.getLightbarSettingsInfo(i);
                            DS4Color profileColor = lbInfo.ds4winSettings.useCustomLed ?
                                lbInfo.ds4winSettings.m_CustomLed : lbInfo.ds4winSettings.m_Led;
                            AppLogger.LogToGui("SteamGameMonitor: forcing lightbar reset to profile/custom color for controller " + (i + 1) + " after Steam game closed", false);
                            for (int attempt = 0; attempt < 6; attempt++)
                            {
                                DS4LightBar.ForceLightbarColor(device, i, profileColor);
                                Thread.Sleep(250);
                            }
                            DS4LightBar.ForceProfileLightbarColor(device, i);
                            AppLogger.LogToGui("SteamGameMonitor: lightbar reset forced for controller " + (i + 1), false);
                        }
                        catch
                        {
                            // Ignore per-device errors
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}
