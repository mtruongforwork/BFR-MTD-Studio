namespace BFR.App;

using BFR.App.Controls.Mac;
using System.Windows;
using System.Windows.Input;

using BFR.Presentation;

/// <summary>
/// "Trình thử Regex" (spec 7.3): thử mẫu biểu thức trên tên tệp trước khi áp dụng cho cả lô.
/// </summary>
public partial class RegexTesterWindow : Window
{
    private readonly RegexTesterViewModel _vm;
    private readonly Func<string?> _selectedNameProvider;

    public RegexTesterWindow(
        RegexTesterViewModel vm,
        Func<string?> selectedNameProvider,
        Window? owner = null)
    {
        _vm = vm;
        _selectedNameProvider = selectedNameProvider;

        InitializeComponent();
        DataContext = vm;

        if (owner is not null) Owner = owner;

        InputBindings.Add(new KeyBinding(new RelayCommand(() => Close()), Key.Escape, ModifierKeys.None));
        Loaded += (_, _) =>
        {
            if (Content is FrameworkElement card) BFR.App.Infrastructure.Motion.ScaleIn(card, new Point(0.5, 0.4), 0.96);
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UseSelected_Click(object sender, RoutedEventArgs e)
    {
        var name = _selectedNameProvider();
        if (!string.IsNullOrWhiteSpace(name))
        {
            _vm.UseSelectedName(name);
        }
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ApplyToAllFiles())
        {
            Close();
            return;
        }

        MacAlert.Show(
            this,
            _vm.ApplyFailedMessage,
            _vm.Title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
