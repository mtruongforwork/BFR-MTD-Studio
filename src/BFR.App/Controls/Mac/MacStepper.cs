namespace BFR.App.Controls.Mac;

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

/// <summary>Stepper kiểu AppKit: hai nút tăng/giảm. Bàn phím: ↑ ↓ Home End. Bước nhảy là SmallChange (mặc định 1).</summary>
public sealed class MacStepper : RangeBase
{
    public static RoutedCommand IncreaseCommand { get; } = new(nameof(IncreaseCommand), typeof(MacStepper));

    public static RoutedCommand DecreaseCommand { get; } = new(nameof(DecreaseCommand), typeof(MacStepper));

    static MacStepper()
    {
        SmallChangeProperty.OverrideMetadata(typeof(MacStepper), new FrameworkPropertyMetadata(1d));
        MaximumProperty.OverrideMetadata(typeof(MacStepper), new FrameworkPropertyMetadata(100d));
        FocusableProperty.OverrideMetadata(typeof(MacStepper), new FrameworkPropertyMetadata(true));
        CommandManager.RegisterClassCommandBinding(typeof(MacStepper),
            new CommandBinding(IncreaseCommand, (s, _) => ((MacStepper)s).Step(1)));
        CommandManager.RegisterClassCommandBinding(typeof(MacStepper),
            new CommandBinding(DecreaseCommand, (s, _) => ((MacStepper)s).Step(-1)));
    }

    private void Step(int direction) => SetCurrentValue(ValueProperty, Math.Clamp(Value + (direction * SmallChange), Minimum, Maximum));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: Step(1); break;
            case Key.Down: Step(-1); break;
            case Key.Home: SetCurrentValue(ValueProperty, Minimum); break;
            case Key.End: SetCurrentValue(ValueProperty, Maximum); break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        public Peer(MacStepper owner)
            : base(owner)
        {
        }

        private MacStepper Stepper => (MacStepper)Owner;

        public bool IsReadOnly => !Stepper.IsEnabled;

        public double LargeChange => Stepper.LargeChange;

        public double SmallChange => Stepper.SmallChange;

        public double Maximum => Stepper.Maximum;

        public double Minimum => Stepper.Minimum;

        public double Value => Stepper.Value;

        public void SetValue(double value)
        {
            if (!Stepper.IsEnabled) throw new ElementNotEnabledException();
            Stepper.SetCurrentValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
        }

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;

        protected override string GetClassNameCore() => nameof(MacStepper);
    }
}
