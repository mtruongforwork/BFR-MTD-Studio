namespace BFR.App.Infrastructure;

using System;
using System.Windows;
using System.Windows.Automation;

/// <summary>Đặt tên cho trình đọc màn hình ở những control dựng bằng mã mà nhãn nhìn thấy nằm ở phần tử khác.</summary>
public static class AccessibleName
{
    public static T Named<T>(this T element, string name) where T : DependencyObject
    {
        AutomationProperties.SetName(element, name);
        return element;
    }

    /// <summary>Cho các trang soạn chữ trực tiếp bằng hai thứ tiếng: tiếng Việt khi giao diện là tiếng Việt, còn lại tiếng Anh.</summary>
    public static T Named<T>(this T element, string vietnamese, string english) where T : DependencyObject =>
        element.Named(string.Equals(Loc.Current, "vi", StringComparison.OrdinalIgnoreCase) ? vietnamese : english);
}
