/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See
the GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels.Util;

namespace DS4WinWPF.DS4Forms.ViewModels.SpecialActions
{
    public class PressKeyViewModel : NotifyDataErrorBase
    {
        private string describeText;
        private DS4KeyType keyType;
        private int value;
        private int pressReleaseIndex = 0;
        private bool normalTrigger = true;
        // Multi-key: the full selected list, in picker order, no limit.
        // Single-key actions keep using Value; Keys is the source of
        // truth whenever it is non-empty.
        private List<int> keys = new List<int>();
        // Sequence: press the keys one after another instead of all
        // at once. Only meaningful for toggle actions with more than
        // one key; held actions always press all at once.
        private bool sequence;
        public bool IsToggle => (keyType & DS4KeyType.Toggle) != 0;
        public event EventHandler IsToggleChanged;

        public Visibility ShowToggleControls
        {
            get
            {
                return ((keyType & DS4KeyType.Toggle) != 0) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        public event EventHandler ShowToggleControlsChanged;

        public string DescribeText
        {
            get
            {
                string result = "Select a Key";
                if (!string.IsNullOrEmpty(describeText))
                {
                    result = describeText;
                };

                return result;
            }
        }
        public event EventHandler DescribeTextChanged;
        public DS4KeyType KeyType { get => keyType; set => keyType = value; }
        public int Value { get => value; set => this.value = value; }
        public int PressReleaseIndex { get => pressReleaseIndex; set => pressReleaseIndex = value; }
        public bool NormalTrigger { get => normalTrigger; set => normalTrigger = value; }
        public bool Sequence { get => sequence; set => sequence = value; }
        public bool IsMultiKey => GetKeys().Count > 1;

        public List<int> GetKeys()
        {
            if (keys != null && keys.Count > 0)
                return new List<int>(keys);
            if (value != 0)
                return new List<int> { value };
            return new List<int>();
        }

        public void SetKeys(List<int> newKeys)
        {
            keys = new List<int>();
            if (newKeys != null)
            {
                foreach (int k in newKeys)
                {
                    if (k != 0 && !keys.Contains(k))
                        keys.Add(k);
                }
            }
            value = keys.Count > 0 ? keys[0] : 0;
            UpdateDescribeText();
        }

        public bool UnloadError
        {
            get => errors.TryGetValue("UnloadError", out _);
        }

        public void LoadAction(SpecialAction action)
        {
            keyType = action.keyType;
            keys = new List<int>();
            sequence = false;
            value = 0;
            string d = action.details ?? string.Empty;
            // Sequence suffix, written by SaveAction as ";Sequence".
            if (d.Contains(";"))
            {
                string[] parts = d.Split(';');
                d = parts[0];
                if (parts.Length > 1 && parts[1].Trim().StartsWith("Sequence", StringComparison.Ordinal))
                    sequence = true;
            }
            if (!string.IsNullOrEmpty(d))
            {
                foreach (string token in d.Split(','))
                {
                    if (int.TryParse(token.Trim(), out int k) && k != 0 && !keys.Contains(k))
                        keys.Add(k);
                }
            }
            if (keys.Count > 0)
                value = keys[0];
            else
                int.TryParse(d, out value);

            if (action.pressRelease)
            {
                pressReleaseIndex = 1;
            }

            UpdateDescribeText();
            UpdateToggleControls();
        }

        public void UpdateDescribeText()
        {
            List<int> ks = GetKeys();
            if (ks.Count == 0)
            {
                describeText = string.Empty;
            }
            else if (ks.Count == 1)
            {
                value = ks[0];
                describeText = KeyInterop.KeyFromVirtualKey(value).ToString() +
                    (keyType.HasFlag(DS4KeyType.ScanCode) ? " (SC)" : "") +
                    (keyType.HasFlag(DS4KeyType.Toggle) ? " (Toggle)" : "");
            }
            else
            {
                value = ks[0];
                describeText = string.Join(" + ", ks.Select(k => KeyInterop.KeyFromVirtualKey(k).ToString())) +
                    (keyType.HasFlag(DS4KeyType.ScanCode) ? " (SC)" : "") +
                    (keyType.HasFlag(DS4KeyType.Toggle) ? " (Toggle)" : "") +
                    (sequence ? " (Sequence)" : "");
            }

            DescribeTextChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateToggleControls()
        {
            IsToggleChanged?.Invoke(this, EventArgs.Empty);
            ShowToggleControlsChanged?.Invoke(this, EventArgs.Empty);
        }

        public DS4ControlSettings PrepareSettings()
        {
            DS4ControlSettings settings = new DS4ControlSettings(DS4Controls.None);
            settings.action.actionKey = value;
            settings.keyType = keyType;
            settings.actionType = DS4ControlSettings.ActionType.Key;
            return settings;
        }

        public void ReadSettings(DS4ControlSettings settings)
        {
            // Single-key path only. The multi-key picker path sets Keys
            // directly via SetKeys after the dialog closes, and must not
            // be overwritten here with just the first key.
            if (keys == null || keys.Count <= 1)
            {
                value = (int)settings.action.actionKey;
                if (value != 0)
                    keys = new List<int> { value };
            }
            keyType = settings.keyType;
        }

        public void SaveAction(SpecialAction action, bool edit = false)
        {
            string uaction = null;
            if (keyType.HasFlag(DS4KeyType.Toggle))
            {
                uaction = "Press";
                if (pressReleaseIndex == 1)
                {
                    uaction = "Release";
                }
            }

            List<int> ks = GetKeys();
            string detailsStr;
            if (ks.Count <= 1)
            {
                detailsStr = $"{value}{(keyType.HasFlag(DS4KeyType.ScanCode) ? " Scan Code" : "")}";
            }
            else
            {
                detailsStr = string.Join(",", ks);
                if (sequence)
                    detailsStr += ";Sequence";
                if (keyType.HasFlag(DS4KeyType.ScanCode))
                    detailsStr += " Scan Code";
            }

            Global.SaveAction(action.name, action.controls, 4,
                detailsStr, edit,
                extras: !string.IsNullOrEmpty(uaction) ? $"{uaction}\n{action.ucontrols}" : "");
        }

        public override bool IsValid(SpecialAction action)
        {
            ClearOldErrors();

            bool valid = true;
            List<string> valueErrors = new List<string>();
            List<string> toggleErrors = new List<string>();

            if (GetKeys().Count == 0)
            {
                valid = false;
                valueErrors.Add("No key defined");
                errors["Value"] = valueErrors;
                RaiseErrorsChanged("Value");
            }
            if (keyType.HasFlag(DS4KeyType.Toggle) && string.IsNullOrEmpty(action.ucontrols))
            {
                valid = false;
                toggleErrors.Add("No unload triggers specified");
                errors["UnloadError"] = toggleErrors;
                RaiseErrorsChanged("UnloadError");
            }

            return valid;
        }

        public override void ClearOldErrors()
        {
            if (errors.Count > 0)
            {
                errors.Clear();
                RaiseErrorsChanged("Value");
                RaiseErrorsChanged("UnloadError");
            }
        }
    }
}
