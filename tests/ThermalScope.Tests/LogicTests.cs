using ThermalScope.Services;
using ThermalScope.ViewModels;
using Xunit;

namespace ThermalScope.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F10")]
    [InlineData("Ctrl+Alt+PgUp")]
    [InlineData("Ctrl+Shift+T")]
    [InlineData("Alt+5")]
    public void ParseThenFormatRoundTrips(string text)
    {
        var binding = HotkeyBinding.TryParse(HotkeyAction.ToggleLimiter, text);

        Assert.NotNull(binding);
        Assert.Equal(text, binding!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F10")]            // a modifier is required
    [InlineData("Ctrl+")]          // no key
    [InlineData("Ctrl+NotAKey")]
    public void InvalidShortcutsAreRejected(string text) =>
        Assert.Null(HotkeyBinding.TryParse(HotkeyAction.ShowWindow, text));

    [Fact]
    public void UnassignedHasNoText()
    {
        var none = HotkeyBinding.Unassigned(HotkeyAction.ToggleOverlay);

        Assert.False(none.IsAssigned);
        Assert.Equal(string.Empty, none.Text);
    }

    [Fact]
    public void SameKeysIgnoresTheAction()
    {
        var a = HotkeyBinding.TryParse(HotkeyAction.IncreaseLimit, "Ctrl+Alt+PgUp")!;
        var b = HotkeyBinding.TryParse(HotkeyAction.DecreaseLimit, "Ctrl+Alt+PgUp")!;

        Assert.True(a.SameKeys(b));
    }
}

public class HeatTests
{
    [Theory]
    [InlineData(30, "Cool")]
    [InlineData(55, "Normal")]
    [InlineData(75, "Warm")]
    [InlineData(88, "Hot")]
    [InlineData(95, "Critical")]
    public void StateWordsFollowTheThresholds(double celsius, string expected) =>
        Assert.Equal(expected, Heat.StateFor(celsius));

    [Fact]
    public void FractionIsClampedToTheGaugeRange()
    {
        Assert.Equal(0, Heat.Fraction(-40));
        Assert.Equal(1, Heat.Fraction(500));
        Assert.Equal(0.5, Heat.Fraction((Heat.GaugeMinimum + Heat.GaugeMaximum) / 2), 3);
    }
}
