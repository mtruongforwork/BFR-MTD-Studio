namespace BFR.Presentation.Design;

using System.Collections.Generic;
using System.Linq;

/// <summary>Bộ nút của một alert (trùng ý nghĩa với MessageBoxButton của Windows).</summary>
public enum AlertButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel
}

public enum AlertChoice
{
    None,
    Ok,
    Cancel,
    Yes,
    No
}

public enum AlertButtonLook
{
    Normal,

    /// <summary>Nút mặc định: nền màu nhấn, Enter kích hoạt.</summary>
    Default,

    /// <summary>Hành động mất dữ liệu: chữ đỏ, không bao giờ là nút mặc định.</summary>
    Destructive
}

public sealed record AlertButton(AlertChoice Choice, AlertButtonLook Look, bool IsCancel);

/// <summary>
/// Quy tắc bố trí nút của alert kiểu macOS, tách khỏi WPF để test được. Thứ tự trả về là từ trái sang phải:
/// nút khẳng định ở ngoài cùng bên phải.
/// </summary>
public static class MacAlertPlan
{
    /// <param name="defaultChoice">Nút mặc định nơi gọi yêu cầu; <see cref="AlertChoice.None"/> = nút khẳng định (như MessageBox).</param>
    /// <param name="isWarning">Alert cảnh báo: nếu mặc định an toàn là nút phủ định thì nút khẳng định là hành động nguy hiểm.</param>
    public static IReadOnlyList<AlertButton> Buttons(AlertButtons buttons, AlertChoice defaultChoice, bool isWarning)
    {
        var order = buttons switch
        {
            AlertButtons.OkCancel => new[] { AlertChoice.Cancel, AlertChoice.Ok },
            AlertButtons.YesNo => new[] { AlertChoice.No, AlertChoice.Yes },
            AlertButtons.YesNoCancel => new[] { AlertChoice.Cancel, AlertChoice.No, AlertChoice.Yes },
            _ => new[] { AlertChoice.Ok }
        };

        var affirmative = order[^1];
        var chosen = order.Contains(defaultChoice) ? defaultChoice : affirmative;
        var escape = EscapeResult(buttons);

        return order.Select(choice =>
        {
            var look = choice == chosen ? AlertButtonLook.Default
                : isWarning && choice == affirmative ? AlertButtonLook.Destructive
                : AlertButtonLook.Normal;
            return new AlertButton(choice, look, choice == escape);
        }).ToList();
    }

    /// <summary>Kết quả khi bấm Esc hoặc đóng cửa sổ: luôn là lựa chọn không làm gì thêm.</summary>
    public static AlertChoice EscapeResult(AlertButtons buttons) => buttons switch
    {
        AlertButtons.Ok => AlertChoice.Ok,
        AlertButtons.YesNo => AlertChoice.No,
        _ => AlertChoice.Cancel
    };
}
