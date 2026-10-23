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

    private bool _reserveSpace;
    private BarPlacement _placement = new();

    public bool ReserveSpace
    {
        get => _reserveSpace;
        set
        {
            if (_reserveSpace != value)
            {
                _reserveSpace = value;
                Save();
                SettingsChanged?.Invoke();
            }
        }
    }

    /// <summary>Where the bar sits; saved when it changes (after a drag).</summary>
    public BarPlacement Placement
    {
        get => _placement;
        set
        {
            if (_placement != value)
            {
                _placement = value;
                Save();
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
                {
                    _hideOnMaximized = data.HideOnMaximized;
                    _reserveSpace = data.ReserveSpace;
                    _placement = new BarPlacement(
                        Enum.TryParse(data.BarEdge, out BarEdge edge) ? edge : BarEdge.Top,
                        double.IsFinite(data.BarX) ? Math.Clamp(data.BarX, 0, 1) : 0.5,
                        string.IsNullOrEmpty(data.BarMonitor) ? null : data.BarMonitor);
                }
            }
        }
        catch (Exception ex)
        {
            // Use defaults on failure
            Log.Error("Loading settings failed", ex);
        }
    }

    public void Save()
    {
        try
        {
            var data = new SettingsData
            {
                HideOnMaximized = _hideOnMaximized,
                ReserveSpace = _reserveSpace,
                BarEdge = _placement.Edge.ToString(),
                BarX = _placement.X,
                BarMonitor = _placement.Monitor,
            };
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            AtomicFile.WriteAllText(SettingsFile, json);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
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
        catch (Exception ex)
        {
            Log.Error("Updating the startup entry failed", ex);
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
        public bool ReserveSpace { get; set; }
        public string BarEdge { get; set; } = nameof(Services.BarEdge.Top);
        public double BarX { get; set; } = 0.5;
        public string? BarMonitor { get; set; }
    }
}
