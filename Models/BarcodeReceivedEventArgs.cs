using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 条码扫描事件参数，包含扫描结果和时间戳等信息。
    /// </summary>
    public class BarcodeReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// 扫描到的条码内容
        /// </summary>
        public string Barcode { get; }

        /// <summary>
        /// 条码接收的时间戳（UTC）
        /// </summary>
        public DateTime TimestampUtc { get; }

        /// <summary>
        /// 原始响应数据（未处理的完整字符串）
        /// </summary>
        public string RawResponse { get; }

        public BarcodeReceivedEventArgs(string barcode, string rawResponse)
        {
            Barcode = barcode?.Trim() ?? "";
            RawResponse = rawResponse;
            TimestampUtc = DateTime.UtcNow;
        }
    }
}
