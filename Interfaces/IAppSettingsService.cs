using System;
using System.Collections.Generic;
using System.Text;

namespace WPFStandardFramework.Interfaces
{
    // Interfaces\IAppSettingsService.cs
    public interface IAppSettingsService
    {
        bool IsAutoSaveEnabled { get; set; }
        int VolumeLevel { get; set; }
        string Theme { get; set; }
        bool IsNotificationsEnabled { get; set; }
        Task LoadAsync();
        Task SaveAsync();
    }
}
