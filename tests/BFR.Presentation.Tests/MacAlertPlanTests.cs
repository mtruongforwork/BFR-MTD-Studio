namespace BFR.Presentation.Tests;

using System.Linq;
using BFR.Presentation.Design;
using Xunit;

public class MacAlertPlanTests
{
    [Theory]
    [InlineData(AlertButtons.Ok, "Ok")]
    [InlineData(AlertButtons.OkCancel, "Cancel,Ok")]
    [InlineData(AlertButtons.YesNo, "No,Yes")]
    [InlineData(AlertButtons.YesNoCancel, "Cancel,No,Yes")]
    public void AffirmativeButton_IsRightmost_AndDefaultByDefault(AlertButtons buttons, string order)
    {
        var plan = MacAlertPlan.Buttons(buttons, AlertChoice.None, isWarning: false);

        Assert.Equal(order, string.Join(",", plan.Select(b => b.Choice)));
        Assert.Equal(AlertButtonLook.Default, plan[^1].Look);
        Assert.Single(plan, b => b.Look == AlertButtonLook.Default);
        Assert.Single(plan, b => b.IsCancel);
    }

    [Fact]
    public void WarningThatDefaultsToNo_MakesYesDestructive_AndNeverTheDefault()
    {
        var plan = MacAlertPlan.Buttons(AlertButtons.YesNo, AlertChoice.No, isWarning: true);

        Assert.Equal(AlertButtonLook.Default, plan.Single(b => b.Choice == AlertChoice.No).Look);
        Assert.Equal(AlertButtonLook.Destructive, plan.Single(b => b.Choice == AlertChoice.Yes).Look);
    }

    [Fact]
    public void WarningThatDefaultsToYes_StaysAnOrdinaryDefault()
    {
        var plan = MacAlertPlan.Buttons(AlertButtons.YesNo, AlertChoice.None, isWarning: true);

        Assert.Equal(AlertButtonLook.Default, plan.Single(b => b.Choice == AlertChoice.Yes).Look);
        Assert.DoesNotContain(plan, b => b.Look == AlertButtonLook.Destructive);
    }

    [Theory]
    [InlineData(AlertButtons.Ok, AlertChoice.Ok)]
    [InlineData(AlertButtons.OkCancel, AlertChoice.Cancel)]
    [InlineData(AlertButtons.YesNo, AlertChoice.No)]
    [InlineData(AlertButtons.YesNoCancel, AlertChoice.Cancel)]
    public void Escape_NeverConfirmsAnything(AlertButtons buttons, AlertChoice expected)
    {
        Assert.Equal(expected, MacAlertPlan.EscapeResult(buttons));
        Assert.Equal(expected, MacAlertPlan.Buttons(buttons, AlertChoice.None, false).Single(b => b.IsCancel).Choice);
    }

    [Fact]
    public void DefaultThatIsNotInTheSet_FallsBackToTheAffirmativeButton()
    {
        var plan = MacAlertPlan.Buttons(AlertButtons.YesNo, AlertChoice.Cancel, isWarning: false);

        Assert.Equal(AlertButtonLook.Default, plan.Single(b => b.Choice == AlertChoice.Yes).Look);
    }
}
