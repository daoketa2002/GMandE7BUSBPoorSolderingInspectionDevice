using CommunityToolkit.Mvvm.ComponentModel;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models
{
    /// <summary>
    /// 作业员数据模型
    /// </summary>
    public partial class OperatorModel : ObservableObject
    {
        /// <summary>
        /// 唯一标识
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// 作业员名称
        /// </summary>
        [ObservableProperty]
        private string _name = string.Empty;

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public override string ToString() => Name;

        public override bool Equals(object? obj)
        {
            if (obj is OperatorModel other)
                return Id == other.Id && Name == other.Name;
            if (obj is string name)
                return string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        public override int GetHashCode() => Name.ToLowerInvariant().GetHashCode();
    }
}