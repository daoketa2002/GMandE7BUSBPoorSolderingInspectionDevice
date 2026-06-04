using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GMandE7BUSBPoorSolderingInspectionDevice.设置相关类;
using System.IO;
using GMandE7BUSBPoorSolderingInspectionDevice.Interfaces;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsFilePath;

        public SettingsService(IConfiguration configuration)
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var settingsFolderPath = Path.Combine(baseDirectory, "设置");
            if (!Directory.Exists(settingsFolderPath))
            {
                Directory.CreateDirectory(settingsFolderPath);
            }

            _settingsFilePath = Path.Combine(settingsFolderPath, "settings.json");
        }


        public ApplicationSettings LoadSettings()
        {
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    return JsonSerializer.Deserialize<ApplicationSettings>(json) ?? new ApplicationSettings();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error deserializing settings: {ex.Message}");
                    return new ApplicationSettings();
                }
            }

            return new ApplicationSettings();
        }

        public void SaveSettings(ApplicationSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
    }
}
