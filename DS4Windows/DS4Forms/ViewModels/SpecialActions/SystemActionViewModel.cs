using System;
using System.Collections.Generic;
using System.Linq;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels.Util;

namespace DS4WinWPF.DS4Forms.ViewModels.SpecialActions
{
    public class SystemActionViewModel : NotifyDataErrorBase
    {
        public static readonly string[] ActionValues = new string[]
        {
            "VolumeUp", "VolumeDown", "VolumeMute",
            "Copy", "Paste", "Cut", "Undo", "Redo", "SelectAll", "Save", "CloseWindow",
            "Custom",
        };
        public static readonly string[] ActionLabels = new string[]
        {
            "Volume Up", "Volume Down", "Volume Mute",
            "Copy", "Paste", "Cut", "Undo", "Redo", "Select All", "Save", "Close Window",
            "Custom Keys (pick in the key picker)",
        };
        public const int CustomIndex = 11;

        private int systemActionIndex = 0;
        private List<int> customKeys = new List<int>();
        private string describeText;

        public int SystemActionIndex
        {
            get => systemActionIndex;
            set
            {
                systemActionIndex = value;
                UpdateDescribeText();
            }
        }
        public List<int> CustomKeys => new List<int>(customKeys);
        public string DescribeText => string.IsNullOrEmpty(describeText) ? "Select a System Action" : describeText;
        public event EventHandler DescribeTextChanged;

        public void LoadAction(SpecialAction action)
        {
            string d = action.details ?? string.Empty;
            customKeys = new List<int>();
            systemActionIndex = 0;
            if (d.StartsWith("Custom:", StringComparison.Ordinal))
            {
                systemActionIndex = CustomIndex;
                bool seq;
                List<ushort> ks = Mapping.ParseMultiKeys(d.Substring("Custom:".Length), out seq);
                customKeys = ks.Select(k => (int)k).ToList();
            }
            else
            {
                for (int i = 0; i < ActionValues.Length; i++)
                {
                    if (ActionValues[i] == d)
                    {
                        systemActionIndex = i;
                        break;
                    }
                }
            }
            UpdateDescribeText();
        }

        public void SetCustomKeys(List<int> keys)
        {
            customKeys = new List<int>();
            if (keys != null)
            {
                foreach (int k in keys)
                {
                    if (k != 0 && !customKeys.Contains(k))
                        customKeys.Add(k);
                }
            }
            systemActionIndex = CustomIndex;
            UpdateDescribeText();
        }

        public void UpdateDescribeText()
        {
            if (systemActionIndex == CustomIndex)
            {
                if (customKeys.Count == 0)
                    describeText = "Custom Keys: none selected yet";
                else
                    describeText = "Custom Keys: " + string.Join(" + ",
                        customKeys.Select(k => System.Windows.Input.KeyInterop.KeyFromVirtualKey(k).ToString()));
            }
            else if (systemActionIndex >= 0 && systemActionIndex < ActionLabels.Length)
            {
                describeText = ActionLabels[systemActionIndex];
            }
            else
            {
                describeText = string.Empty;
            }
            DescribeTextChanged?.Invoke(this, EventArgs.Empty);
        }

        public DS4ControlSettings PrepareSettings()
        {
            DS4ControlSettings settings = new DS4ControlSettings(DS4Controls.None);
            settings.action.actionKey = customKeys.Count > 0 ? customKeys[0] : 0;
            settings.keyType = DS4KeyType.None;
            settings.actionType = DS4ControlSettings.ActionType.Key;
            return settings;
        }

        public void SaveAction(SpecialAction action, bool edit = false)
        {
            string details;
            if (systemActionIndex == CustomIndex)
                details = "Custom:" + string.Join(",", customKeys);
            else if (systemActionIndex >= 0 && systemActionIndex < ActionValues.Length)
                details = ActionValues[systemActionIndex];
            else
                details = "VolumeUp";
            Global.SaveAction(action.name, action.controls, 10, details, edit);
        }

        public override bool IsValid(SpecialAction action)
        {
            ClearOldErrors();
            bool valid = true;
            if (systemActionIndex == CustomIndex && customKeys.Count == 0)
            {
                valid = false;
                errors["Value"] = new List<string> { "No custom keys selected" };
                RaiseErrorsChanged("Value");
            }
            return valid;
        }

        public override void ClearOldErrors()
        {
            if (errors.Count > 0)
            {
                errors.Clear();
                RaiseErrorsChanged("Value");
            }
        }
    }
}
