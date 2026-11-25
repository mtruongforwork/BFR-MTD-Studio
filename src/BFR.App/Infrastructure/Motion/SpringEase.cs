namespace BFR.App.Infrastructure;

using System.Windows;
using System.Windows.Media.Animation;
using BFR.Presentation.Design;

/// <summary>
/// Easing theo dao động lò xo tắt dần. <see cref="Seconds"/> phải bằng thời lượng của animation.
/// Toán nằm ở <see cref="Spring"/>. Dùng EasingMode = EaseIn (mặc định của lớp này) để đường cong không bị WPF lật ngược.
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public static readonly DependencyProperty StiffnessProperty =
        DependencyProperty.Register(nameof(Stiffness), typeof(double), typeof(SpringEase), new PropertyMetadata(300d));

    public static readonly DependencyProperty DampingProperty =
        DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringEase), new PropertyMetadata(30d));

    public static readonly DependencyProperty SecondsProperty =
        DependencyProperty.Register(nameof(Seconds), typeof(double), typeof(SpringEase), new PropertyMetadata(0.25));

    public SpringEase()
    {
        EasingMode = EasingMode.EaseIn;
    }

    public SpringEase(double stiffness, double damping, double seconds)
        : this()
    {
        Stiffness = stiffness;
        Damping = damping;
        Seconds = seconds;
    }

    // Là DependencyProperty để bản sao do Freezable tạo ra khi animate giữ nguyên tham số.
    public double Stiffness { get => (double)GetValue(StiffnessProperty); set => SetValue(StiffnessProperty, value); }

    public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }

    public double Seconds { get => (double)GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }

    protected override double EaseInCore(double normalizedTime) => Spring.Eased(Stiffness, Damping, Seconds, normalizedTime);

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
