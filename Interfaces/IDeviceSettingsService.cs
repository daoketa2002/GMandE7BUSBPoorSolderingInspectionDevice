using GMandE7BUSBPoorSolderingInspectionDevice.AppConfig.DeviceConfigs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Interfaces
{
    public interface IDeviceSettingsService
    {
        DeviceSettings LoadSettings();
        void SaveSettings(DeviceSettings settings);
    }
}
