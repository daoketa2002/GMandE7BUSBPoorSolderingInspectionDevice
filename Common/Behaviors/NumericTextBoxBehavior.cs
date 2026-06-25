// ============================================================
// 文件: Common/Behaviors/NumericTextBoxBehavior.cs
// 描述: WPF 附加行为 —— 数字输入拦截
//       附加到任意 TextBox，自动拦截非数字字符输入。
//       支持配置：小数模式、负数模式、最小值/最大值。
//       拦截路径：键盘输入（PreviewTextInput）+ 粘贴（DataObject.Pasting）。
//       用法示例：
//         <TextBox local:NumericTextBoxBehavior.IsEnabled="True"
//                  local:NumericTextBoxBehavior.AllowDecimal="True"/>
// ============================================================

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Common.Behaviors
{
    /// <summary>
    /// 数字输入拦截行为
    /// 通过 WPF 附加属性（Attached Property）模式挂载到 TextBox，
    /// 在 PreviewTextInput / PreviewKeyDown / DataObject.Pasting 三个通道
    /// 拦截非数字字符，确保输入框中只能输入合法的数字文本。
    ///
    /// 使用方式（XAML）：
    ///   xmlns:behaviors="clr-namespace:GMandE7BUSBPoorSolderingInspectionDevice.Common.Behaviors"
    ///   <TextBox behaviors:NumericTextBoxBehavior.IsEnabled="True"
    ///            behaviors:NumericTextBoxBehavior.AllowDecimal="True"/>
    /// </summary>
    public static class NumericTextBoxBehavior
    {
        #region 附加属性：IsEnabled

        /// <summary>
        /// 是否启用数字输入拦截
        /// </summary>
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(NumericTextBoxBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        /// <summary>
        /// 获取 IsEnabled 属性值
        /// </summary>
        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        /// <summary>
        /// 设置 IsEnabled 属性值
        /// </summary>
        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        #endregion

        #region 附加属性：AllowDecimal

        /// <summary>
        /// 是否允许输入小数点（默认 true，即允许小数）
        /// 设为 false 时仅允许整数输入
        /// </summary>
        public static readonly DependencyProperty AllowDecimalProperty =
            DependencyProperty.RegisterAttached(
                "AllowDecimal",
                typeof(bool),
                typeof(NumericTextBoxBehavior),
                new PropertyMetadata(true));

        public static bool GetAllowDecimal(DependencyObject obj)
        {
            return (bool)obj.GetValue(AllowDecimalProperty);
        }

        public static void SetAllowDecimal(DependencyObject obj, bool value)
        {
            obj.SetValue(AllowDecimalProperty, value);
        }

        #endregion

        #region 附加属性：AllowNegative

        /// <summary>
        /// 是否允许输入负号（默认 false）
        /// 设为 true 时允许在首字符位置输入 "-"
        /// </summary>
        public static readonly DependencyProperty AllowNegativeProperty =
            DependencyProperty.RegisterAttached(
                "AllowNegative",
                typeof(bool),
                typeof(NumericTextBoxBehavior),
                new PropertyMetadata(false));

        public static bool GetAllowNegative(DependencyObject obj)
        {
            return (bool)obj.GetValue(AllowNegativeProperty);
        }

        public static void SetAllowNegative(DependencyObject obj, bool value)
        {
            obj.SetValue(AllowNegativeProperty, value);
        }

        #endregion

        #region 附加属性：MinValue / MaxValue

        /// <summary>
        /// 数值最小值（仅对粘贴内容做范围修正，键盘逐字输入时不校验）
        /// </summary>
        public static readonly DependencyProperty MinValueProperty =
            DependencyProperty.RegisterAttached(
                "MinValue",
                typeof(double?),
                typeof(NumericTextBoxBehavior),
                new PropertyMetadata(null));

        public static double? GetMinValue(DependencyObject obj)
        {
            return (double?)obj.GetValue(MinValueProperty);
        }

        public static void SetMinValue(DependencyObject obj, double? value)
        {
            obj.SetValue(MinValueProperty, value);
        }

        /// <summary>
        /// 数值最大值（仅对粘贴内容做范围修正）
        /// </summary>
        public static readonly DependencyProperty MaxValueProperty =
            DependencyProperty.RegisterAttached(
                "MaxValue",
                typeof(double?),
                typeof(NumericTextBoxBehavior),
                new PropertyMetadata(null));

        public static double? GetMaxValue(DependencyObject obj)
        {
            return (double?)obj.GetValue(MaxValueProperty);
        }

        public static void SetMaxValue(DependencyObject obj, double? value)
        {
            obj.SetValue(MaxValueProperty, value);
        }

        #endregion

        #region 事件订阅 / 取消订阅

        /// <summary>
        /// IsEnabled 属性变更时，订阅或取消订阅 TextBox 的输入拦截事件
        /// </summary>
        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBox textBox)
                return;

            bool newValue = (bool)e.NewValue;

            if (newValue)
            {
                // 启用：订阅三个输入拦截事件
                textBox.PreviewTextInput += OnPreviewTextInput;
                textBox.PreviewKeyDown += OnPreviewKeyDown;
                DataObject.AddPastingHandler(textBox, OnPaste);
            }
            else
            {
                // 禁用：取消订阅
                textBox.PreviewTextInput -= OnPreviewTextInput;
                textBox.PreviewKeyDown -= OnPreviewKeyDown;
                DataObject.RemovePastingHandler(textBox, OnPaste);
            }
        }

        #endregion

        #region 输入拦截处理

        /// <summary>
        /// 键盘输入拦截
        /// 根据 AllowDecimal / AllowNegative 设置过滤非数字字符
        /// </summary>
        private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox textBox)
                return;

            string newText = e.Text; // 本次即将插入的字符串
            string currentText = textBox.Text;
            int caretIndex = textBox.CaretIndex;
            int selectionLength = textBox.SelectionLength;

            // 计算替换后的完整文本
            string resultText = currentText.Remove(caretIndex, selectionLength).Insert(caretIndex, newText);

            // 校验完整文本是否为合法数字格式
            if (!IsValidNumericText(resultText, textBox))
            {
                e.Handled = true; // 拦截：不合法
            }
        }

        /// <summary>
        /// 特殊按键拦截
        /// 放行：Backspace / Delete / 方向键 / Home / End / Tab / Enter
        /// 拦截：Space（空格）
        /// </summary>
        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                // 数字输入框不允许空格
                e.Handled = true;
            }

            // 以下按键始终放行：
            //   Back, Delete, Left, Right, Up, Down, Home, End, Tab, Enter, Escape
            // PreviewTextInput 不会对这些按键触发，因此无需特殊处理
        }

        /// <summary>
        /// 粘贴拦截
        /// 对粘贴内容逐字符过滤，移除非法字符后重新设置文本
        /// </summary>
        private static void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is not TextBox textBox)
                return;

            // 获取粘贴文本
            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand(); // 无法读取文本，取消粘贴
                return;
            }

            string pasteText = (string)e.DataObject.GetData(typeof(string))!;

            // 过滤粘贴内容中的非法字符
            string cleaned = FilterNumericString(pasteText, textBox);

            if (string.IsNullOrEmpty(cleaned))
            {
                e.CancelCommand(); // 过滤后无有效内容
                return;
            }

            // 计算替换后的完整文本
            string currentText = textBox.Text;
            int caretIndex = textBox.CaretIndex;
            int selectionLength = textBox.SelectionLength;
            string resultText = currentText.Remove(caretIndex, selectionLength).Insert(caretIndex, cleaned);

            // 范围校验（超出范围则修正）
            resultText = ClampToRange(resultText, textBox);

            // 手动设置文本并取消默认粘贴
            e.CancelCommand();
            textBox.Text = resultText;
            textBox.CaretIndex = Math.Min(caretIndex + cleaned.Length, resultText.Length);
        }

        #endregion

        #region 数字文本校验逻辑

        /// <summary>
        /// 校验文本是否可被解析为合法的数字格式
        /// 处于编辑中间态的不完整文本（如 "-"、"1."、"1e"）也被放行，
        /// 以便用户可以继续编辑到完整的数字。
        /// </summary>
        /// <param name="text">待校验的完整文本</param>
        /// <param name="textBox">目标 TextBox（用于读取行为配置）</param>
        /// <returns>合法返回 true</returns>
        private static bool IsValidNumericText(string text, TextBox textBox)
        {
            // 空文本视为合法（清空输入框）
            if (string.IsNullOrEmpty(text))
                return true;

            bool allowDecimal = GetAllowDecimal(textBox);
            bool allowNegative = GetAllowNegative(textBox);

            // 仅负号：允许（用户可能正在输入 "-5"）
            if (text == "-" && allowNegative)
                return true;

            // 仅小数点：允许（用户可能正在输入 ".5"）
            if (text == "." && allowDecimal)
                return true;

            // 以小数点结尾：允许（用户可能正在输入 "1." 再输入"5"）
            if (text.EndsWith(".") && allowDecimal)
            {
                string withoutTrailingDot = text.TrimEnd('.');
                return double.TryParse(withoutTrailingDot, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }

            // 尝试解析完整数字
            NumberStyles styles = NumberStyles.Float;
            if (!allowNegative)
                styles &= ~NumberStyles.AllowLeadingSign;

            if (double.TryParse(text, styles, CultureInfo.InvariantCulture, out double parsedValue))
            {
                // 科学计数法（如 "1e5"）拒绝
                if (text.Contains('e', StringComparison.OrdinalIgnoreCase) ||
                    text.Contains('E', StringComparison.OrdinalIgnoreCase))
                    return false;

                // NaN / Infinity 拒绝
                if (double.IsNaN(parsedValue) || double.IsInfinity(parsedValue))
                    return false;

                return true;
            }

            return false;
        }

        /// <summary>
        /// 过滤字符串中的非法数字字符
        /// 用于粘贴内容预处理
        /// </summary>
        /// <param name="input">原始粘贴文本</param>
        /// <param name="textBox">目标 TextBox</param>
        /// <returns>过滤后的合法数字字符串</returns>
        private static string FilterNumericString(string input, TextBox textBox)
        {
            bool allowDecimal = GetAllowDecimal(textBox);
            bool allowNegative = GetAllowNegative(textBox);
            bool hasDecimal = false;
            bool hasMinus = false;

            var chars = input.Where(ch =>
            {
                // 数字始终允许
                if (char.IsDigit(ch)) return true;

                // 小数点：每个数最多一个
                if (ch == '.' && allowDecimal && !hasDecimal)
                {
                    hasDecimal = true;
                    return true;
                }

                // 负号：仅在字符串开头（且为首字符）
                if (ch == '-' && allowNegative && !hasMinus)
                {
                    hasMinus = true;
                    return true;
                }

                return false;
            }).ToArray();

            return new string(chars);
        }

        /// <summary>
        /// 将文本表示的数值限制在 Min/Max 范围内
        /// （仅粘贴后调用）
        /// </summary>
        private static string ClampToRange(string text, TextBox textBox)
        {
            double? min = GetMinValue(textBox);
            double? max = GetMaxValue(textBox);

            if (min == null && max == null)
                return text; // 无范围限制

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return text; // 非完整数字（如 "1."），不做修正

            bool clamped = false;
            if (min.HasValue && value < min.Value)
            {
                value = min.Value;
                clamped = true;
            }
            if (max.HasValue && value > max.Value)
            {
                value = max.Value;
                clamped = true;
            }

            // 整数模式下去掉小数部分
            bool allowDecimal = GetAllowDecimal(textBox);
            if (!allowDecimal)
                value = Math.Round(value, 0);

            return clamped ? value.ToString(CultureInfo.InvariantCulture) : text;
        }

        #endregion
    }
}
