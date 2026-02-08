using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopLS.Services;

/// <summary>
/// Manages persistent user settings: startup registry key and hide-on-maximized preference.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopLS", "settings.json");

    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "DesktopLS";

    private bool _startupEnabled;
    private bool _hideOnMaximized = true; // default: on

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (_startupEnabled != value)
            {
                _startupEnabled = value;
                ApplyStartupSetting();
            }
        }
    }

    public bool HideOnMaximized
    {
        get => _hideOnMaximized;
        set
        {
            if (_hideOnMaximized != value)
            {
                _hideOnMaximized = value;
                SettingsChanged?.Invoke();
            }
        }
    }

    public event Action? SettingsChanged;

    public void Load()
    {
        try
        {
            // Load startup setting from registry
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
            {
                string? value = key?.GetValue(RegistryValueName) as string;
                _startupEnabled = !string.IsNullOrEmpty(value);
            }

            // Load hide-on-maximized from JSON
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                var data = JsonSerializer.Deserialize<SettingsData>(json);
                if (data != null)
                    _hideOnMaximized = data.HideOnMaximized;
            }
        }
        catch
        {
            // Best-effort load; use defaults on failure
        }
    }

    public void Save()
    {
        try
        {
            var data = new SettingsData { HideOnMaximized = _hideOnMaximized };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Best-effort save
        }
    }

    private void ApplyStartupSetting()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true))
            {
                if (key == null) return;

                if (_startupEnabled)
                {
                    string exePath = Assembly.GetExecutingAssembly().Location.Replace(".dll", ".exe");
                    key.SetValue(RegistryValueName, exePath, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Best-effort registry write
        }
    }

    private class SettingsData
    {
        public bool HideOnMaximized { get; set; } = true;
    }
}
