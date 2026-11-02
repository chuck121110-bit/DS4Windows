/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    /// <summary>
    /// Fixes the "2 - DualSense Wireless Controller" audio name problem.
    ///
    /// Root cause, verified on Chuck's PC 2026-11-01:
    /// Every VIIPER virtual DualSense import creates a new USB audio
    /// device instance, for example
    ///   USB\VID_054C&PID_0CE6&MI_00\3&D7A266E&0&0000  (current, OK)
    /// while the previous instance stays registered as not present, for
    /// example
    ///   USB\VID_054C&PID_0CE6&MI_00\3&53F8A37&0&0000  (stale, Unknown)
    /// The stale instance's audio endpoints also stay registered as
    /// not present:
    ///   Speakers (DualSense Wireless Controller) - Unknown
    ///   Headset Microphone (DualSense Wireless Controller) - Unknown
    /// Windows will not reuse a friendly name that is still reserved
    /// by a registered endpoint, so the new endpoints are named
    ///   Speakers (2 - DualSense Wireless Controller) - OK
    ///   Headset Microphone (2 - DualSense Wireless Controller) - OK
    /// Detaching the usbip port alone does not remove the Windows
    /// device or its endpoints, which is why usbip-only cleanup never
    /// fixed the name. An earlier 2026-10-29 cleanup removed only the
    /// SWD\MMDEVAPI endpoint devices via pnputil, and was lost in the
    /// 2026-10-30 stock revert, and it also left the underlying USB
    /// MEDIA device registered, so the name reservation could return.
    /// This cleanup removes the stale underlying USB device with the
    /// official SetupAPI remove, DIF_REMOVE, which is the same
    /// mechanism Device Manager uses for "Uninstall device", and which
    /// notifies the audio stack to drop its endpoints.
    ///
    /// Timing matters: this runs BEFORE a new virtual device is
    /// imported, in ViiperUsbipPortManager.DetachStaleLocalViiperPorts,
    /// which is called at DS4 startup and at the start of
    /// ViiperOutDevice.CreateDeviceAndOpenStreamCore. Cleaning up
    /// after the import would be too late, the new endpoints would
    /// already have been named "2 -".
    ///
    /// Safety, in order:
    /// 1. Only USB VID_054C PID_0CE6 MI_00 devices, the DualSense
    ///    audio function. Bluetooth DualSense audio is BTHENUM, not
    ///    USB, and is never touched. HID, composite, and other
    ///    functions are never touched.
    /// 2. Only devices that are registered but NOT present. The
    ///    present set is enumerated separately, and a device is
    ///    stale only if it is in the all-devices set and not in the
    ///    present set. The current, working device is never removed.
    ///    A physical DualSense connected by USB cable and currently
    ///    unplugged would also be cleaned this way, which is safe:
    ///    removing a non-present PnP device deletes no user data,
    ///    Windows recreates it, with the plain name, on the next
    ///    plug, which is exactly the outcome wanted here.
    /// 3. Cleanup never blocks a controller attach. Any failure is
    ///    logged and the attach continues, so a cleanup bug cannot
    ///    break input.
    /// </summary>
    internal static class DualSenseAudioEndpointCleanup
    {
        private const string StaleDevicePrefix =
            @"USB\VID_054C&PID_0CE6&MI_00\";
        private const string DualSenseEndpointName =
            "DualSense Wireless Controller";

        public static void CleanupStaleDualSenseAudioDevices()
        {
            try
            {
                List<string> stale = FindStaleDualSenseAudioDeviceIds();
                int removed = 0;
                foreach (string instanceId in stale)
                {
                    if (RemoveDevice(instanceId))
                    {
                        removed++;
                        Log($"Removed stale DualSense audio device {instanceId}.");
                    }
                    else
                    {
                        Log($"Could not remove stale DualSense audio device {instanceId}, leaving it in place.");
                    }
                }

                if (removed > 0)
                {
                    // PnP removal is asynchronous. Give the audio stack
                    // a bounded window to drop the stale endpoints
                    // before the new virtual device is imported.
                    Thread.Sleep(500);
                }

                // Fallback, always, not only when a PnP device was
                // removed: remove disconnected DualSense AudioEndpoint
                // devices via pnputil, which goes through the PnP
                // manager and is allowed to drop the endpoint.
                // 2026-11-01 registry error fix: the previous
                // fallback tried to delete MMDevices registry keys
                // directly, which Windows blocks even for admin
                // ("Requested registry access is not allowed"),
                // and enumerating non-active endpoints via NAudio
                // threw 0xE000020B for a single bad endpoint and
                // aborted the whole cleanup. Direct registry
                // deletion is removed entirely. Pnputil removal
                // was verified live on 2026-11-01: it removed
                // Speakers (2- DualSense) from the PnP list, while
                // the active plain Speakers and Headset Microphone
                // stayed OK. Each endpoint is handled separately,
                // so one failure never aborts the others.
                RemoveDisconnectedDualSenseAudioEndpoints();
            }
            catch (Exception ex)
            {
                // Never break a controller attach for cleanup.
                // Log the type as well as the message, so the next
                // failure is diagnosable instead of a bare HRESULT.
                Log($"DualSense audio endpoint cleanup failed, continuing without it: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static List<string> FindStaleDualSenseAudioDeviceIds()
        {
            HashSet<string> all = EnumerateUsbDualSenseAudioDeviceIds(
                NativeMethods.DIGCF_ALLCLASSES);
            HashSet<string> present = EnumerateUsbDualSenseAudioDeviceIds(
                NativeMethods.DIGCF_ALLCLASSES | NativeMethods.DIGCF_PRESENT);
            List<string> stale = all
                .Where(id => !present.Contains(id))
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return stale;
        }

        private static HashSet<string> EnumerateUsbDualSenseAudioDeviceIds(int flags)
        {
            HashSet<string> result = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
                IntPtr.Zero, "USB", 0, flags);
            if (deviceInfoSet.ToInt64() == NativeMethods.INVALID_HANDLE_VALUE)
            {
                return result;
            }

            try
            {
                for (int index = 0; ; index++)
                {
                    NativeMethods.SP_DEVINFO_DATA deviceInfoData =
                        new NativeMethods.SP_DEVINFO_DATA
                        {
                            cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>(),
                        };
                    if (!NativeMethods.SetupDiEnumDeviceInfo(
                        deviceInfoSet, index, ref deviceInfoData))
                    {
                        break;
                    }

                    char[] instanceIdBuffer = new char[NativeMethods.MAX_DEV_LEN];
                    int requiredSize = 0;
                    if (!NativeMethods.SetupDiGetDeviceInstanceId(
                        deviceInfoSet, ref deviceInfoData, instanceIdBuffer,
                        instanceIdBuffer.Length, ref requiredSize))
                    {
                        continue;
                    }

                    string instanceId = new string(instanceIdBuffer)
                        .TrimEnd('\0');
                    if (instanceId.StartsWith(StaleDevicePrefix,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(instanceId);
                    }
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return result;
        }

        private static bool RemoveDevice(string instanceId)
        {
            IntPtr deviceInfoSet = NativeMethods.SetupDiCreateDeviceInfoList(
                IntPtr.Zero, 0);
            if (deviceInfoSet.ToInt64() == NativeMethods.INVALID_HANDLE_VALUE)
            {
                return false;
            }

            try
            {
                NativeMethods.SP_DEVINFO_DATA deviceInfoData =
                    new NativeMethods.SP_DEVINFO_DATA
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.SP_DEVINFO_DATA>(),
                    };
                if (!NativeMethods.SetupDiOpenDeviceInfo(
                    deviceInfoSet, instanceId, IntPtr.Zero, 0,
                    ref deviceInfoData))
                {
                    return false;
                }

                // DIF_REMOVE, the official SetupAPI remove, same as
                // Device Manager "Uninstall device". No class install
                // parameters are needed for a remove.
                return NativeMethods.SetupDiCallClassInstaller(
                    NativeMethods.DIF_REMOVE, deviceInfoSet,
                    ref deviceInfoData);
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        private static void RemoveDisconnectedDualSenseAudioEndpoints()
        {
            try
            {
                if (!Global.IsAdministrator())
                {
                    Log("DualSense audio endpoint cleanup skipped: DS4Windows is not running as administrator.");
                    return;
                }

                List<string> instanceIds = EnumerateDisconnectedDualSenseAudioEndpoints();
                foreach (string instanceId in instanceIds)
                {
                    try
                    {
                        RunPnputil(new[] { "/remove-device", instanceId });
                        Log($"Removed disconnected DualSense audio endpoint {instanceId}.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not remove disconnected DualSense audio endpoint {instanceId}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DualSense disconnected endpoint cleanup skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static List<string> EnumerateDisconnectedDualSenseAudioEndpoints()
        {
            List<string> instanceIds = new List<string>();
            string output = RunPnputil(new[] { "/enum-devices", "/disconnected", "/class", "AudioEndpoint" });
            if (string.IsNullOrWhiteSpace(output))
            {
                return instanceIds;
            }

            // pnputil prints one block per device, separated by
            // blank lines:
            //   Instance ID:                SWD\MMDEVAPI\...
            //   Device Description:         Speakers (2- DualSense ...)
            //   Class Name:                 AudioEndpoint
            // Only the /disconnected enumeration is used, so every
            // block here is already disconnected. Active endpoints
            // are never listed, and non-DualSense blocks are ignored.
            string currentInstanceId = null;
            string currentDescription = null;
            string currentClass = null;
            void FlushBlock()
            {
                if (!string.IsNullOrWhiteSpace(currentInstanceId) &&
                    !string.IsNullOrWhiteSpace(currentDescription) &&
                    string.Equals(currentClass, "AudioEndpoint",
                        StringComparison.OrdinalIgnoreCase) &&
                    currentDescription.IndexOf("DualSense",
                        StringComparison.OrdinalIgnoreCase) >= 0 &&
                    currentInstanceId.StartsWith(@"SWD\MMDEVAPI\",
                        StringComparison.OrdinalIgnoreCase) &&
                    !instanceIds.Contains(currentInstanceId))
                {
                    instanceIds.Add(currentInstanceId);
                }
                currentInstanceId = null;
                currentDescription = null;
                currentClass = null;
            }

            foreach (string rawLine in output.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushBlock();
                    continue;
                }
                if (line.StartsWith("Instance ID:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    currentInstanceId = ValueAfterColon(line);
                }
                else if (line.StartsWith("Device Description:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    currentDescription = ValueAfterColon(line);
                }
                else if (line.StartsWith("Class Name:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    currentClass = ValueAfterColon(line);
                }
            }
            FlushBlock();
            return instanceIds;
        }

        private static string ValueAfterColon(string line)
        {
            int index = line.IndexOf(':');
            return index < 0 ? string.Empty : line.Substring(index + 1).Trim();
        }

        private static string RunPnputil(string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory,
                    "pnputil.exe"),
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("pnputil.exe could not be started.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(15000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("pnputil.exe timed out after 15 seconds.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ?
                    output : error;
                throw new InvalidOperationException(
                    $"pnputil.exe exited with code {process.ExitCode}: {detail.Trim()}");
            }
            return output;
        }

        private static void Log(string message)
        {
            try
            {
                AppLogger.LogToGui(message, false);
            }
            catch
            {
                // Logging must never break cleanup or attach.
            }
        }
    }
}
