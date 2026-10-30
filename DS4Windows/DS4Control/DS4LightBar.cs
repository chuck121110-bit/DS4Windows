/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using static System.Math;
using static DS4Windows.Global;
using System.Diagnostics;
using DS4WinWPF.DS4Control;

namespace DS4Windows
{
    public class DS4LightBar
    {
        private readonly static byte[/* Light On duration */, /* Light Off duration */] BatteryIndicatorDurations =
        {
            { 28, 252 }, // on 10% of the time at 0
            { 28, 252 },
            { 56, 224 },
            { 84, 196 },
            { 112, 168 },
            { 140, 140 },
            { 168, 112 },
            { 196, 84 },
            { 224, 56 }, // on 80% of the time at 80, etc.
            { 252, 28 }, // on 90% of the time at 90
            { 0, 0 }     // use on 100%. 0 is for "charging" OR anything sufficiently-"charged"
        };

        static double[] counters = new double[Global.MAX_DS4_CONTROLLER_COUNT] { 0, 0, 0, 0, 0, 0, 0, 0 };
        public static Stopwatch[] fadewatches = new Stopwatch[Global.MAX_DS4_CONTROLLER_COUNT]
        {
            new Stopwatch(), new Stopwatch(), new Stopwatch(), new Stopwatch(),
            new Stopwatch(), new Stopwatch(), new Stopwatch(), new Stopwatch(),
        };

        static bool[] fadedirection = new bool[Global.MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        static DateTime[] oldnow = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT]
        {
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
            DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
        };

        public static bool[] forcelight = new bool[Global.MAX_DS4_CONTROLLER_COUNT] { false, false, false, false, false, false, false, false };
        // BatteryCheck latch: holds the battery color for at least 3
        // seconds after a trigger, even if the trigger was only a brief
        // pulse, so the color is actually visible instead of flashing
        // for 30-100 ms and restoring immediately on release.
        public static DateTime[] batteryCheckLatchUntil = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT]
        {
            DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue,
            DateTime.MinValue, DateTime.MinValue, DateTime.MinValue, DateTime.MinValue,
        };
        public static DS4Color[] batteryCheckLatchColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        public static readonly TimeSpan BatteryCheckLatchDuration = TimeSpan.FromSeconds(3);
        public static DS4Color[] forcedColor = new DS4Color[Global.MAX_DS4_CONTROLLER_COUNT];
        public static byte[] forcedFlash = new byte[Global.MAX_DS4_CONTROLLER_COUNT];
        internal const int PULSE_FLASH_DURATION = 2000;
        internal const double PULSE_FLASH_SEGMENTS = PULSE_FLASH_DURATION / 40;
        internal const int PULSE_CHARGING_DURATION = 4000;
        internal const double PULSE_CHARGING_SEGMENTS = (PULSE_CHARGING_DURATION / 40) - 2;

        public static void updateLightBar(DS4Device device, int deviceNum)
        {
            // Latch expiry is checked on the regular lightbar tick, so it
            // fires even after the trigger has been released. Expiry
            // restores profile/custom color through the forced resend path.
            // It is checked before the normal routine, and the latch flag
            // is cleared first so the restore itself cannot re-enter here.
            if (deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT &&
                batteryCheckLatchUntil[deviceNum] != DateTime.MinValue &&
                DateTime.UtcNow >= batteryCheckLatchUntil[deviceNum])
            {
                batteryCheckLatchUntil[deviceNum] = DateTime.MinValue;
                try { device?.EndBatteryCheckLatch(); } catch { }
                AppLogger.LogToGui("BatteryCheck latch expired, restoring LED to profile/custom color for controller " + (deviceNum + 1), false);
                if (device != null)
                {
                    try { ForceProfileLightbarColor(device, deviceNum); } catch { }
                }
                return;
            }
            DS4Color color = new DS4Color();
            if (IsBatteryCheckLatchActive(deviceNum))
            {
                // Keep the latch color forced even if something else
                // cleared forcelight, and keep native ownership released.
                forcelight[deviceNum] = true;
                forcedColor[deviceNum] = batteryCheckLatchColor[deviceNum];
                try { device?.BeginBatteryCheckLatch(BatteryCheckLatchDuration); } catch { }
            }
            bool useForceLight = forcelight[deviceNum];
            LightbarSettingInfo lightbarSettingInfo = getLightbarSettingsInfo(deviceNum);
            LightbarDS4WinInfo lightModeInfo = lightbarSettingInfo.ds4winSettings;
            bool useLightRoutine = lightbarSettingInfo.mode == LightbarMode.DS4Win;
            //bool useLightRoutine = false;
            if (!defaultLight && !useForceLight && useLightRoutine)
            {
                if (lightModeInfo.useCustomLed)
                {
                    color = lightModeInfo.m_CustomLed; //getCustomColor(deviceNum);
                }
                else
                {
                    double rainbow = lightModeInfo.rainbow;// getRainbow(deviceNum);
                    if (rainbow > 0)
                    {
                        // Display rainbow
                        DateTime now = DateTime.UtcNow;
                        if (now >= oldnow[deviceNum] + TimeSpan.FromMilliseconds(10)) //update by the millisecond that way it's a smooth transtion
                        {
                            int diffMs = now.Subtract(oldnow[deviceNum]).Milliseconds;
                            oldnow[deviceNum] = now;
                            if (device.isCharging())
                                //counters[deviceNum] -= 1.5 * 3 / rainbow;
                                counters[deviceNum] -= 360.0 * (diffMs / 1000.0 / rainbow);
                            else
                                //counters[deviceNum] += 1.5 * 3 / rainbow;
                                counters[deviceNum] += 360.0 * (diffMs / 1000.0 / rainbow);
                        }

                        if (counters[deviceNum] < 0)
                            counters[deviceNum] = 180000;
                        else if (counters[deviceNum] > 180000)
                            counters[deviceNum] = 0;

                        double maxSat = lightModeInfo.maxRainbowSat; // GetMaxSatRainbow(deviceNum);
                        if (lightModeInfo.ledAsBattery)
                        {
                            byte useSat = (byte)(maxSat == 1.0 ?
                                device.getBattery() * 2.55 :
                                device.getBattery() * 2.55 * maxSat);
                            color = HuetoRGB((float)counters[deviceNum] % 360, useSat);
                        }
                        else
                            color = HuetoRGB((float)counters[deviceNum] % 360,
                                (byte)(maxSat == 1.0 ? 255 : 255 * maxSat));

                    }
                    else if (lightModeInfo.ledAsBattery)
                    {
                        ref DS4Color fullColor = ref lightModeInfo.m_Led; //ref getMainColor(deviceNum);
                        ref DS4Color lowColor = ref lightModeInfo.m_LowLed; //ref getLowColor(deviceNum);
                        color = getTransitionedColor(ref lowColor, ref fullColor, device.getBattery());
                    }
                    else
                    {
                        color = getMainColor(deviceNum);
                    }
                }

                if (device.getBattery() <= lightModeInfo.flashAt && !defaultLight && !device.isCharging())
                {
                    ref DS4Color flashColor = ref lightModeInfo.m_FlashLed; //ref getFlashColor(deviceNum);
                    if (!(flashColor.red == 0 &&
                        flashColor.green == 0 &&
                        flashColor.blue == 0))
                        color = flashColor;

                    if (lightModeInfo.flashType == 1)
                    {
                        double ratio = 0.0;

                        if (!fadewatches[deviceNum].IsRunning)
                        {
                            bool temp = fadedirection[deviceNum];
                            fadedirection[deviceNum] = !temp;
                            fadewatches[deviceNum].Restart();
                            ratio = temp ? 100.0 : 0.0;
                        }
                        else
                        {
                            long elapsed = fadewatches[deviceNum].ElapsedMilliseconds;

                            if (fadedirection[deviceNum])
                            {
                                if (elapsed < PULSE_FLASH_DURATION)
                                {
                                    elapsed = elapsed / 40;
                                    ratio = 100.0 * (elapsed / PULSE_FLASH_SEGMENTS);
                                }
                                else
                                {
                                    ratio = 100.0;
                                    fadewatches[deviceNum].Stop();
                                }
                            }
                            else
                            {
                                if (elapsed < PULSE_FLASH_DURATION)
                                {
                                    elapsed = elapsed / 40;
                                    ratio = (0 - 100.0) * (elapsed / PULSE_FLASH_SEGMENTS) + 100.0;
                                }
                                else
                                {
                                    ratio = 0.0;
                                    fadewatches[deviceNum].Stop();
                                }
                            }
                        }

                        DS4Color tempCol = new DS4Color(0, 0, 0);
                        color = getTransitionedColor(ref color, ref tempCol, ratio);
                    }
                }

                int idleDisconnectTimeout = getIdleDisconnectTimeout(deviceNum);
                if (idleDisconnectTimeout > 0 && lightModeInfo.ledAsBattery &&
                    (!device.isCharging() || device.getBattery() >= 100))
                {
                    // Fade lightbar by idle time
                    TimeSpan timeratio = new TimeSpan(DateTime.UtcNow.Ticks - device.lastActive.Ticks);
                    double botratio = timeratio.TotalMilliseconds;
                    double topratio = TimeSpan.FromSeconds(idleDisconnectTimeout).TotalMilliseconds;
                    double ratio = 100.0 * (botratio / topratio), elapsed = ratio;
                    if (ratio >= 50.0 && ratio < 100.0)
                    {
                        DS4Color emptyCol = new DS4Color(0, 0, 0);
                        color = getTransitionedColor(ref color, ref emptyCol,
                            (uint)(-100.0 * (elapsed = 0.02 * (ratio - 50.0)) * (elapsed - 2.0)));
                    }
                    else if (ratio >= 100.0)
                    {
                        DS4Color emptyCol = new DS4Color(0, 0, 0);
                        color = getTransitionedColor(ref color, ref emptyCol, 100.0);
                    }
                        
                }

                if (device.isCharging() && device.getBattery() < 100)
                {
                    switch (lightModeInfo.chargingType)
                    {
                        case 1:
                        {
                            double ratio = 0.0;

                            if (!fadewatches[deviceNum].IsRunning)
                            {
                                bool temp = fadedirection[deviceNum];
                                fadedirection[deviceNum] = !temp;
                                fadewatches[deviceNum].Restart();
                                ratio = temp ? 100.0 : 0.0;
                            }
                            else
                            {
                                long elapsed = fadewatches[deviceNum].ElapsedMilliseconds;

                                if (fadedirection[deviceNum])
                                {
                                    if (elapsed < PULSE_CHARGING_DURATION)
                                    {
                                        elapsed = elapsed / 40;
                                        if (elapsed > PULSE_CHARGING_SEGMENTS)
                                            elapsed = (long)PULSE_CHARGING_SEGMENTS;
                                        ratio = 100.0 * (elapsed / PULSE_CHARGING_SEGMENTS);
                                    }
                                    else
                                    {
                                        ratio = 100.0;
                                        fadewatches[deviceNum].Stop();
                                    }
                                }
                                else
                                {
                                    if (elapsed < PULSE_CHARGING_DURATION)
                                    {
                                        elapsed = elapsed / 40;
                                        if (elapsed > PULSE_CHARGING_SEGMENTS)
                                            elapsed = (long)PULSE_CHARGING_SEGMENTS;
                                        ratio = (0 - 100.0) * (elapsed / PULSE_CHARGING_SEGMENTS) + 100.0;
                                    }
                                    else
                                    {
                                        ratio = 0.0;
                                        fadewatches[deviceNum].Stop();
                                    }
                                }
                            }

                            DS4Color emptyCol = new DS4Color(0, 0, 0);
                            color = getTransitionedColor(ref color, ref emptyCol, ratio);
                            break;
                        }
                        case 2:
                        {
                            counters[deviceNum] += 0.167;
                            color = HuetoRGB((float)counters[deviceNum] % 360, 255);
                            break;
                        }
                        case 3:
                        {
                            color = lightModeInfo.m_ChargingLed; //getChargingColor(deviceNum);
                            break;
                        }
                        default: break;
                    }
                }
            }
            else if (useForceLight)
            {
                color = forcedColor[deviceNum];
                useLightRoutine = true;
            }
            else if (shuttingdown)
            {
                color = new DS4Color(0, 0, 0);
                useLightRoutine = true;
            }
            else if (useLightRoutine)
            {
                if (device.getConnectionType() == ConnectionType.BT)
                    color = new DS4Color(32, 64, 64);
                else
                    color = new DS4Color(0, 0, 0);
            }

            if (useLightRoutine)
            {
                bool distanceprofile = DistanceProfiles[deviceNum] || tempprofileDistance[deviceNum];
                //distanceprofile = (ProfilePath[deviceNum].ToLower().Contains("distance") || tempprofilename[deviceNum].ToLower().Contains("distance"));
                if (distanceprofile && !defaultLight)
                {
                    // Thing I did for Distance
                    float rumble = device.getLeftHeavySlowRumble() / 2.55f;
                    byte max = Max(color.red, Max(color.green, color.blue));
                    if (device.getLeftHeavySlowRumble() > 100)
                    {
                        DS4Color maxCol = new DS4Color(max, max, 0);
                        DS4Color redCol = new DS4Color(255, 0, 0);
                        color = getTransitionedColor(ref maxCol, ref redCol, rumble);
                    }
                    else
                    {
                        DS4Color maxCol = new DS4Color(max, max, 0);
                        DS4Color redCol = new DS4Color(255, 0, 0);
                        DS4Color tempCol = getTransitionedColor(ref maxCol,
                            ref redCol, 39.6078f);
                        color = getTransitionedColor(ref color, ref tempCol,
                            device.getLeftHeavySlowRumble());
                    }
                }

                /*DS4HapticState haptics = new DS4HapticState
                {
                    LightBarColor = color
                };
                */
                if (Global.openRGBSyncEnabled &&
                    OpenRGBServer.Instance.TryGetColor(deviceNum, out DS4Color openRGBColor))
                {
                    color = openRGBColor;
                }

                DS4LightbarState lightState = new DS4LightbarState
                {
                    LightBarColor = color,
                };

                if (lightState.IsLightBarSet())
                {
                    if (useForceLight && forcedFlash[deviceNum] > 0)
                    {
                        lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = (byte)(25 - forcedFlash[deviceNum]);
                        lightState.LightBarExplicitlyOff = true;
                    }
                    else if (device.getBattery() <= lightModeInfo.flashAt && lightModeInfo.flashType == 0 && !defaultLight && !device.isCharging())
                    {
                        int level = device.getBattery() / 10;
                        if (level >= 10)
                            level = 10; // all values of >~100% are rendered the same

                        lightState.LightBarFlashDurationOn = BatteryIndicatorDurations[level, 0];
                        lightState.LightBarFlashDurationOff = BatteryIndicatorDurations[level, 1];
                    }
                    else if (distanceprofile && device.getLeftHeavySlowRumble() > 155) //also part of Distance
                    {
                        lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = (byte)((-device.getLeftHeavySlowRumble() + 265));
                        lightState.LightBarExplicitlyOff = true;
                    }
                    else
                    {
                        //haptics.LightBarFlashDurationOff = haptics.LightBarFlashDurationOn = 1;
                        lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = 0;
                        lightState.LightBarExplicitlyOff = true;
                    }
                }
                else
                {
                    lightState.LightBarExplicitlyOff = true;
                }

                byte tempLightBarOnDuration = device.getLightBarOnDuration();
                if (tempLightBarOnDuration != lightState.LightBarFlashDurationOn && tempLightBarOnDuration != 1 && lightState.LightBarFlashDurationOn == 0)
                    lightState.LightBarFlashDurationOff = lightState.LightBarFlashDurationOn = 1;

                device.SetLightbarState(ref lightState);
                //device.SetHapticState(ref haptics);
                //device.pushHapticState(ref haptics);
            }
        }

        // Dedup for the two profile-switch hooks below (the
        // serialized switch worker and CheckProfileOptionsAfterLoad
        // can both fire for one switch). The same slot and profile
        // name within 3 seconds is one switch, not two bursts.
        private static readonly string[] lastProfileSwitchResetName =
            new string[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly DateTime[] lastProfileSwitchResetAt =
            new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];

        // Forced LED reset on profile switch. Custom LED is per
        // profile, not one global color: if the newly selected
        // profile has useCustomLed on, use that profile's
        // m_CustomLed; otherwise use that profile's m_Led. This
        // uses the forced resend path, not the normal deduplicated
        // PublishProfileLightbar path, so it works even when DS4
        // already thinks the LED is that color, and it holds native
        // game lightbar ownership released for the burst so a
        // running game cannot immediately overwrite it with black.
        public static void ForceProfileSwitchLedReset(DS4Device device, int deviceNum, string profileName)
        {
            if (device == null || deviceNum < 0 || deviceNum >= Global.MAX_DS4_CONTROLLER_COUNT) return;
            string name = string.IsNullOrEmpty(profileName) ? ProfilePath[deviceNum] : profileName;
            if (string.Equals(lastProfileSwitchResetName[deviceNum], name, StringComparison.Ordinal) &&
                (DateTime.UtcNow - lastProfileSwitchResetAt[deviceNum]).TotalSeconds < 3)
            {
                return;
            }
            lastProfileSwitchResetName[deviceNum] = name;
            lastProfileSwitchResetAt[deviceNum] = DateTime.UtcNow;
            LightbarSettingInfo info;
            try { info = getLightbarSettingsInfo(deviceNum); }
            catch { return; }
            bool useCustom = info.ds4winSettings.useCustomLed;
            DS4Color color = useCustom ? info.ds4winSettings.m_CustomLed : info.ds4winSettings.m_Led;
            AppLogger.LogToGui("Profile switch: forcing LED to " + (useCustom ? "custom" : "profile") +
                " color " + color.red + "," + color.green + "," + color.blue +
                " for controller " + (deviceNum + 1) + " (profile \"" + name + "\")", false);
            _ = Task.Run(() =>
            {
                try
                {
                    try { device.BeginProfileSwitchLedHold(TimeSpan.FromSeconds(2)); } catch { }
                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        try { ForceLightbarColor(device, deviceNum, color); } catch { }
                        Thread.Sleep(250);
                    }
                }
                catch { }
                try { device.EndProfileSwitchLedHold(); } catch { }
                try { ForceProfileLightbarColor(device, deviceNum); } catch { }
                AppLogger.LogToGui("Profile switch: LED reset forced to " + (useCustom ? "custom" : "profile") +
                    " color " + color.red + "," + color.green + "," + color.blue +
                    " for controller " + (deviceNum + 1) + " (profile \"" + name + "\")", false);
            });
        }

        public static DS4Color GetProfileOrCustomColor(int deviceNum)
        {
            LightbarSettingInfo info = getLightbarSettingsInfo(deviceNum);
            return info.ds4winSettings.useCustomLed ?
                info.ds4winSettings.m_CustomLed : info.ds4winSettings.m_Led;
        }

        public static bool IsBatteryCheckLatchActive(int deviceNum)
        {
            return deviceNum >= 0 && deviceNum < Global.MAX_DS4_CONTROLLER_COUNT &&
                batteryCheckLatchUntil[deviceNum] != DateTime.MinValue &&
                DateTime.UtcNow < batteryCheckLatchUntil[deviceNum];
        }

        // Start or extend the latch to 3 seconds from now, force the
        // battery color, and release native game lightbar ownership for
        // the latch window so a game like REPO cannot immediately
        // overwrite it with its own (often black) color.
        public static void RefreshBatteryCheckLatch(DS4Device device, int deviceNum, DS4Color color)
        {
            if (device == null || deviceNum < 0 || deviceNum >= Global.MAX_DS4_CONTROLLER_COUNT) return;
            bool first = !IsBatteryCheckLatchActive(deviceNum);
            batteryCheckLatchUntil[deviceNum] = DateTime.UtcNow + BatteryCheckLatchDuration;
            batteryCheckLatchColor[deviceNum] = color;
            try { device.BeginBatteryCheckLatch(BatteryCheckLatchDuration); } catch { }
            try { ForceLightbarColor(device, deviceNum, color); } catch { }
            if (first)
            {
                AppLogger.LogToGui("BatteryCheck latch started, holding battery color for 3 seconds for controller " + (deviceNum + 1), false);
            }
        }

        public static void ForceLightbarColor(DS4Device device, int deviceNum, DS4Color color)
        {
            if (device == null) return;
            forcedColor[deviceNum] = color;
            forcelight[deviceNum] = true;
            updateLightBar(device, deviceNum);
            try { device.ForceLightbarColor(color); } catch { }
        }

        public static void ForceProfileLightbarColor(DS4Device device, int deviceNum)
        {
            if (device == null) return;
            DS4Color color = GetProfileOrCustomColor(deviceNum);
            forcelight[deviceNum] = false;
            updateLightBar(device, deviceNum);
            try { device.ForceLightbarColor(color); } catch { }
        }

        public static bool defaultLight = false, shuttingdown = false;
      
        public static DS4Color HuetoRGB(float hue, byte sat)
        {
            byte C = sat;
            int X = (int)((C * (float)(1 - Math.Abs((hue / 60) % 2 - 1))));
            if (0 <= hue && hue < 60)
                return new DS4Color(C, (byte)X, 0);
            else if (60 <= hue && hue < 120)
                return new DS4Color((byte)X, C, 0);
            else if (120 <= hue && hue < 180)
                return new DS4Color(0, C, (byte)X);
            else if (180 <= hue && hue < 240)
                return new DS4Color(0, (byte)X, C);
            else if (240 <= hue && hue < 300)
                return new DS4Color((byte)X, 0, C);
            else if (300 <= hue && hue < 360)
                return new DS4Color(C, 0, (byte)X);
            else
                return new DS4Color(Color.Red);
        }
    }
}
