using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace WPFStandardFramework.Common.Converters
{
        /// <summary>
        /// 应用程序设置
        /// </summary>
        public class ApplicationSettings
        {
            public string AppName { get; set; } = "Gb2Gb3TraceSystem";
            public string Version { get; set; } = "1.0.0";
            public string Theme { get; set; } = "Light"; // Light/Dark/System
                                                         // 添加其他应用设置...
        }
}

