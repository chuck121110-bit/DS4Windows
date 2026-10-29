/*
DS4Windows
Copyright (C) 2026 hbashton

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace DS4Windows
{
    // 2026-10-29: Every profile switch and every DS4 restart stops and
    // restarts the VIIPER virtual DualSense device. Windows then sees a
    // new virtual USB instance and numbers its new audio endpoints
    // "2- DualSense Wireless Controller", then "3- ...", because the
    // old endpoints remain registered as disconnected.
    //
    // First version (2026-10-29): removed ONLY disconnected DualSense
    // audio endpoints, on startup and on VIIPER detach. That failed on
    // the 2026-10-29 19:51 restart: startup removed 4 disconnected
    // endpoints, detach found none to remove, and connected audio was
    // still "2- DualSense Wireless Controller". Reason: the old 2-
    // endpoints were still connected when DS4 started, because the
    // VIIPER device persisted, so a disconnected-only cleanup skipped
    // them, and at detach time Windows had not yet marked them
    // disconnected, so that cleanup found none.
    //
    // Stronger version (2026-10-29): in addition to the disconnected
    // removal, this helper now also handles still-connected endpoints
    // that belong to a VIIPER device, in two ways, both best effort
    // and never throwing:
    //
    // 1. Removal by VIIPER port, at startup and at detach. At startup
    //    (viiperPort < 0) it removes active VIIPER DualSense endpoints,
    //    because no new VIIPER device exists yet and the old device is
    //    about to be recovered, so those endpoints are stale by
    //    definition. At detach it removes only endpoints whose usbip
    //    port matches the just-detached port, regardless of whether
    //    pnputil already lists them as disconnected, because Windows
    //    may not have updated their state yet. Removal uses the
    //    MMDevice ID to PnP instance ID mapping, so only endpoints
    //    positively identified as VIIPER DualSense endpoints are
    //    touched. Physical USB/Bluetooth DualSense endpoints have no
    //    usbip ancestor and are never removed here, and non-DualSense
    //    endpoints are never touched.
    //
    // 2. Rename of a live numbered endpoint, via the Core Audio
    //    property store. If an active numbered VIIPER endpoint remains
    //    after the removal steps, and no active plain-named endpoint
    //    of the same direction already exists, and no endpoint of any
    //    state already carries the plain name, it is renamed back to
    //    its plain name. Renaming keeps the endpoint ID unchanged, so
    //    an active audio stream is not interrupted, unlike removal.
    //    Rename is never used for the just-detached port, because
    //    renaming that endpoint would leave a disconnected plain entry
    //    behind and the next VIIPER device would be numbered again.
    //    If renaming fails for any reason, it is logged and skipped;
    //    at startup it is never followed by a removal of an active
    //    endpoint that is in use, to avoid dropping user audio.
    //
    // Callers run it synchronously on purpose: on startup it must
    // finish before the first VIIPER device is created, and on VIIPER
    // detach it must finish before the next plug, or the new endpoints
    // would be numbered again before the cleanup ran.
    internal static class DualSenseAudioEndpointCleanup
    {
        public static void RemoveDisconnectedDualSenseAudioEndpoints(string trigger)
        {
            try
            {
                if (!Global.IsAdministrator())
                {
                    AppLogger.LogToGui(
                        $"DualSense audio cleanup ({trigger}) skipped: " +
                        "DS4Windows is not running as administrator.",
                        true);
                    return;
                }

                List<string> instanceIds =
                    EnumerateDisconnectedDualSenseAudioEndpoints(trigger);
                if (instanceIds.Count == 0)
                {
                    AppLogger.LogToGui(
                        $"DualSense audio cleanup ({trigger}): no " +
                        "disconnected DualSense audio endpoints to remove.",
                        false);
                    return;
                }

                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}): removing " +
                    $"{instanceIds.Count} disconnected DualSense audio " +
                    "endpoint(s).",
                    false);
                foreach (string instanceId in instanceIds)
                {
                    RemoveEndpoint(instanceId, trigger);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) failed: " +
                    ex.Message,
                    true);
            }
        }

        // Combined entry point used by startup and VIIPER detach.
        // viiperPort < 0 means startup: handle any stale VIIPER
        // endpoint. viiperPort >= 0 means detach: handle only that
        // port, plus any other live numbered VIIPER endpoint that
        // is safe to rename.
        public static void CleanupDualSenseAudioEndpoints(
            string trigger, int viiperPort = -1)
        {
            try
            {
                if (!Global.IsAdministrator())
                {
                    AppLogger.LogToGui(
                        $"DualSense audio cleanup ({trigger}) skipped: " +
                        "DS4Windows is not running as administrator.",
                        true);
                    return;
                }

                RemoveDisconnectedDualSenseAudioEndpoints(trigger);
                RemoveActiveVIIPERAudioEndpoints(trigger, viiperPort);
                RenameActiveNumberedVIIPERAudioEndpoints(trigger, viiperPort);
                // Detach timing: Windows may not have marked the
                // just-detached endpoints disconnected yet when the
                // first pass ran, which is why the original detach
                // cleanup found none. Give it a short window, then
                // retry the disconnected pass once. This is still
                // synchronous and best effort, and it never touches
                // an active endpoint on retry.
                if (trigger.IndexOf("VIIPER detach",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { Thread.Sleep(750); } catch { }
                    RemoveDisconnectedDualSenseAudioEndpoints(
                        trigger + " retry");
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) failed: " +
                    ex.Message,
                    true);
            }
        }

        private sealed class ActiveEndpointInfo
        {
            public string EndpointId;
            public string FriendlyName;
            public DataFlow Flow;
            public int UsbipPort = -1;
            public bool IsVIIPER;
        }

        private static List<ActiveEndpointInfo> EnumerateActiveVIIPEREndpoints(
            string trigger)
        {
            List<ActiveEndpointInfo> result = new List<ActiveEndpointInfo>();
            try
            {
                using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
                {
                    MMDeviceCollection endpoints;
                    try
                    {
                        endpoints = enumerator.EnumerateAudioEndPoints(
                            flow, DeviceState.Active);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogToGui(
                            $"DualSense audio cleanup ({trigger}) could not " +
                            $"enumerate {flow} endpoints: {ex.Message}",
                            true);
                        continue;
                    }

                    foreach (MMDevice endpoint in endpoints)
                    {
                        try
                        {
                            if (!DualSenseAudioPassthrough.IsDualSenseEndpoint(endpoint))
                            {
                                continue;
                            }
                            bool isVIIPER = DualSenseAudioPassthrough
                                .TryGetEndpointUsbipPort(endpoint, out int port);
                            if (!isVIIPER)
                            {
                                // Physical USB/Bluetooth DualSense: never
                                // touched by the stronger cleanup.
                                continue;
                            }
                            result.Add(new ActiveEndpointInfo
                            {
                                EndpointId = endpoint.ID,
                                FriendlyName = endpoint.FriendlyName,
                                Flow = flow,
                                UsbipPort = port,
                                IsVIIPER = true,
                            });
                        }
                        catch
                        {
                            // One bad endpoint must not stop the rest.
                        }
                        finally
                        {
                            try { endpoint?.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) could not " +
                    $"enumerate active VIIPER endpoints: {ex.Message}",
                    true);
            }
            return result;
        }

        private static void RemoveActiveVIIPERAudioEndpoints(
            string trigger, int viiperPort)
        {
            try
            {
                List<ActiveEndpointInfo> endpoints =
                    EnumerateActiveVIIPEREndpoints(trigger);
                foreach (ActiveEndpointInfo info in endpoints)
                {
                    try
                    {
                        if (viiperPort >= 0 && info.UsbipPort != viiperPort)
                        {
                            continue;
                        }
                        // At startup (viiperPort < 0) every VIIPER
                        // endpoint is stale: no new VIIPER device
                        // exists yet, and the old device is about to
                        // be recovered. At detach, only the detached
                        // port is handled, by the check above.
                        string instanceId = "SWD\\MMDEVAPI\\" + info.EndpointId;
                        AppLogger.LogToGui(
                            $"DualSense audio cleanup ({trigger}): removing " +
                            $"still-connected VIIPER endpoint " +
                            $"'{info.FriendlyName}' (port {info.UsbipPort}, " +
                            $"{instanceId}).",
                            false);
                        RemoveEndpoint(instanceId, trigger, isDisconnected: false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogToGui(
                            $"DualSense audio cleanup ({trigger}) could not " +
                            $"remove still-connected endpoint " +
                            $"'{info.FriendlyName}': {ex.Message}",
                            true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) could not " +
                    $"remove still-connected VIIPER endpoints: {ex.Message}",
                    true);
            }
        }

        private static void RenameActiveNumberedVIIPERAudioEndpoints(
            string trigger, int viiperPort)
        {
            try
            {
                List<ActiveEndpointInfo> endpoints =
                    EnumerateActiveVIIPEREndpoints(trigger);
                if (endpoints.Count == 0)
                {
                    return;
                }

                // Active plain names, by flow, to avoid a collision.
                HashSet<string> activePlainNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                    foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
                    {
                        try
                        {
                            foreach (MMDevice endpoint in enumerator
                                .EnumerateAudioEndPoints(flow, DeviceState.Active))
                            {
                                try
                                {
                                    if (!DualSenseAudioPassthrough
                                        .IsNumberedDuplicateFriendlyName(
                                            endpoint.FriendlyName))
                                    {
                                        activePlainNames.Add(
                                            flow + "|" + endpoint.FriendlyName);
                                    }
                                }
                                finally
                                {
                                    try { endpoint?.Dispose(); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Any endpoint, any state, already carrying the plain
                // name would make a rename a duplicate, so check all
                // states, not just Active.
                HashSet<string> anyPlainNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                    foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
                    {
                        try
                        {
                            foreach (MMDevice endpoint in enumerator
                                .EnumerateAudioEndPoints(flow, DeviceState.All))
                            {
                                try
                                {
                                    if (!DualSenseAudioPassthrough
                                        .IsNumberedDuplicateFriendlyName(
                                            endpoint.FriendlyName))
                                    {
                                        anyPlainNames.Add(
                                            flow + "|" + endpoint.FriendlyName);
                                    }
                                }
                                finally
                                {
                                    try { endpoint?.Dispose(); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                foreach (ActiveEndpointInfo info in endpoints)
                {
                    try
                    {
                        if (viiperPort >= 0 && info.UsbipPort == viiperPort)
                        {
                            // Just-detached port: renaming it would
                            // leave a disconnected plain entry behind
                            // and the next VIIPER device would be
                            // numbered again. Removal above is the
                            // correct handling for that port.
                            continue;
                        }
                        if (!DualSenseAudioPassthrough
                            .IsNumberedDuplicateFriendlyName(info.FriendlyName))
                        {
                            continue;
                        }
                        string plainName = PlainNameFromNumbered(info.FriendlyName);
                        if (string.IsNullOrWhiteSpace(plainName) ||
                            string.Equals(plainName, info.FriendlyName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        string key = info.Flow + "|" + plainName;
                        if (activePlainNames.Contains(key) ||
                            anyPlainNames.Contains(key))
                        {
                            AppLogger.LogToGui(
                                $"DualSense audio cleanup ({trigger}): " +
                                $"skipping rename of '{info.FriendlyName}' " +
                                $"because plain name '{plainName}' is " +
                                "already in use.",
                                false);
                            continue;
                        }

                        if (TryRenameEndpoint(info.EndpointId, plainName, out string error))
                        {
                            AppLogger.LogToGui(
                                $"DualSense audio cleanup ({trigger}): " +
                                $"renamed VIIPER audio endpoint " +
                                $"'{info.FriendlyName}' to '{plainName}'.",
                                false);
                            activePlainNames.Add(key);
                            anyPlainNames.Add(key);
                        }
                        else
                        {
                            AppLogger.LogToGui(
                                $"DualSense audio cleanup ({trigger}): " +
                                $"could not rename '{info.FriendlyName}' " +
                                $"to '{plainName}': {error}",
                                true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogToGui(
                            $"DualSense audio cleanup ({trigger}) could not " +
                            $"rename '{info.FriendlyName}': {ex.Message}",
                            true);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) could not " +
                    $"rename numbered VIIPER endpoints: {ex.Message}",
                    true);
            }
        }

        private static string PlainNameFromNumbered(string friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                return string.Empty;
            }
            // "Speakers (2- DualSense Wireless Controller)" ->
            // "Speakers (DualSense Wireless Controller)".
            Match match = Regex.Match(friendlyName,
                @"^(.*)\(\d+- (.+)\)$");
            if (match.Success)
            {
                return match.Groups[1].Value + "(" + match.Groups[2].Value + ")";
            }
            // Bare "2- DualSense Wireless Controller" ->
            // "DualSense Wireless Controller".
            Match bare = Regex.Match(friendlyName, @"^\d+- (.+)$");
            if (bare.Success)
            {
                return bare.Groups[1].Value;
            }
            return string.Empty;
        }

        // Rename via the Core Audio property store (IPropertyStore),
        // the same mechanism the Sound control panel rename uses.
        // The endpoint ID is unchanged, so an active stream is not
        // interrupted. Returns false on any failure, never throws.
        private static bool TryRenameEndpoint(
            string endpointId, string newName, out string error)
        {
            error = string.Empty;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IPropertyStore store = null;
            IntPtr namePtr = IntPtr.Zero;
            try
            {
                Type enumeratorType = Type.GetTypeFromCLSID(
                    new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType);
                int hr = enumerator.GetDevice(endpointId, out device);
                if (hr != 0 || device == null)
                {
                    error = $"GetDevice failed with HRESULT 0x{hr:X8}.";
                    return false;
                }
                hr = device.OpenPropertyStore(2 /* STGM_READWRITE */, out store);
                if (hr != 0 || store == null)
                {
                    error = $"OpenPropertyStore failed with HRESULT 0x{hr:X8}.";
                    return false;
                }

                AudioPropertyKey key = new AudioPropertyKey(
                    new Guid("b3f8fa53-0004-438e-9002-51a46abfd948"), 2);
                PropVariant variant = new PropVariant
                {
                    vt = 31, // VT_LPWSTR
                    wReserved1 = 0,
                    wReserved2 = 0,
                    wReserved3 = 0,
                    ptr = Marshal.StringToCoTaskMemUni(newName),
                };
                namePtr = variant.ptr;
                hr = store.SetValue(ref key, ref variant);
                if (hr != 0)
                {
                    error = $"SetValue failed with HRESULT 0x{hr:X8}.";
                    return false;
                }
                hr = store.Commit();
                if (hr != 0)
                {
                    error = $"Commit failed with HRESULT 0x{hr:X8}.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (namePtr != IntPtr.Zero)
                {
                    try { Marshal.FreeCoTaskMem(namePtr); } catch { }
                }
                if (store != null)
                {
                    try { Marshal.ReleaseComObject(store); } catch { }
                }
                if (device != null)
                {
                    try { Marshal.ReleaseComObject(device); } catch { }
                }
                if (enumerator != null)
                {
                    try { Marshal.ReleaseComObject(enumerator); } catch { }
                }
            }
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            [PreserveSig]
            int GetDevice(
                [MarshalAs(UnmanagedType.LPWStr)] string pwstrId,
                out IMMDevice ppDevice);
            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(
                ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            [PreserveSig]
            int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            [PreserveSig]
            int GetState(out int pdwState);
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint cProps);
            [PreserveSig]
            int GetAt(uint iProp, out AudioPropertyKey pkey);
            [PreserveSig]
            int GetValue(ref AudioPropertyKey key, out PropVariant pv);
            [PreserveSig]
            int SetValue(ref AudioPropertyKey key, ref PropVariant pv);
            [PreserveSig]
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioPropertyKey
        {
            public Guid fmtid;
            public uint pid;
            public AudioPropertyKey(Guid fmtid, uint pid)
            {
                this.fmtid = fmtid;
                this.pid = pid;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr ptr;
        }

        private static List<string> EnumerateDisconnectedDualSenseAudioEndpoints(
            string trigger)
        {
            List<string> instanceIds = new List<string>();
            try
            {
                string output = RunPnputil(
                    new[] { "/enum-devices", "/disconnected", "/class", "AudioEndpoint" },
                    trigger);
                if (string.IsNullOrWhiteSpace(output))
                {
                    return instanceIds;
                }

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
                        !instanceIds.Contains(currentInstanceId))
                    {
                        instanceIds.Add(currentInstanceId);
                    }
                    currentInstanceId = null;
                    currentDescription = null;
                    currentClass = null;
                }

                foreach (string rawLine in
                    output.Replace("\r\n", "\n").Split('\n'))
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
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) could not list " +
                    $"disconnected audio endpoints: {ex.Message}",
                    true);
            }
            return instanceIds;
        }

        private static void RemoveEndpoint(string instanceId, string trigger)
        {
            RemoveEndpoint(instanceId, trigger, isDisconnected: true);
        }

        private static void RemoveEndpoint(
            string instanceId, string trigger, bool isDisconnected)
        {
            try
            {
                string output = RunPnputil(
                    new[] { "/remove-device", instanceId },
                    trigger);
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}): removed " +
                    $"{(isDisconnected ? "disconnected " : string.Empty)}" +
                    $"endpoint {instanceId}.",
                    false);
                _ = output;
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui(
                    $"DualSense audio cleanup ({trigger}) could not " +
                    $"remove endpoint {instanceId}: {ex.Message}",
                    true);
            }
        }

        private static string ValueAfterColon(string line)
        {
            int index = line.IndexOf(':');
            return index < 0 ? string.Empty : line.Substring(index + 1).Trim();
        }

        private static string RunPnputil(string[] args, string trigger)
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
                throw new InvalidOperationException(
                    "pnputil.exe could not be started.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(15000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"pnputil.exe timed out after 15 seconds ({trigger}).");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ?
                    output : error;
                throw new InvalidOperationException(
                    $"pnputil.exe exited with code {process.ExitCode}: " +
                    detail.Trim());
            }
            return output;
        }
    }
}
