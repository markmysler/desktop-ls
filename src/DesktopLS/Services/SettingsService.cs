using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopLS.Services;

/// <summary>
/// Manages persistent user settings: startup registry key and hide-on-maximized preference.
/// </summary>
public sealed class SettingsService : ISettingsService
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
                Save(); // persist right away; a crash used to lose the change
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

                // If the exe was moved (or the entry predates quoting), point
                // it at the current, quoted location
                if (_startupEnabled && value != StartupCommand())
                    ApplyStartupSetting();
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
            AtomicFile.WriteAllText(SettingsFile, json);
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
                    string? command = StartupCommand();
                    if (command != null)
                        key.SetValue(RegistryValueName, command, RegistryValueKind.String);
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

    /// <summary>
    /// The Run-key command for this exe. Quoted, so a path with spaces can't
    /// be misread as a different program plus arguments.
    /// </summary>
    internal static string? StartupCommand()
    {
        // Environment.ProcessPath is correct for single-file apps too
        string? exePath = Environment.ProcessPath;
        return string.IsNullOrEmpty(exePath) ? null : $"\"{exePath}\"";
    }

    private class SettingsData
    {
        public bool HideOnMaximized { get; set; } = true;
    }
}
