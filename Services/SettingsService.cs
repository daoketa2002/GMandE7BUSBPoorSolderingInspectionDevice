using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;
using GMandE7BUSBPoorSolderingInspectionDevice.Models.DeviceConfigs;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    public class SettingsService : IDeviceSettingsService
    {
        private readonly string _settingsFilePath;

        public SettingsService(IConfiguration configuration)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var settingsFolderPath = Path.Combine(baseDirectory, "设置", "设备设置");
            if (!Directory.Exists(settingsFolderPath))
            {
                Directory.CreateDirectory(settingsFolderPath);
            }

            _settingsFilePath = Path.Combine(settingsFolderPath, "DeviceSettings.json");
        }


        public DeviceSettings LoadSettings()
        {
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    return JsonSerializer.Deserialize<DeviceSettings>(json) ?? new DeviceSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error deserializing settings: {ex.Message}");
                    return new DeviceSettings();
                }
            }

            return new DeviceSettings();
        }

        public void SaveSettings(DeviceSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
    }
}
