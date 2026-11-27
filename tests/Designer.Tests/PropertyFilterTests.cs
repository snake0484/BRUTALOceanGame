using BrutalGame.DesignKit;

namespace BrutalGame.Designer.Tests;

public class PropertyFilterTests
{
    [Theory]
    [InlineData("", "Width", true)]
    [InlineData("wid", "Width", true)]
    [InlineData("WDT", "Width", true)]
    [InlineData("wdt", "Depth", false)]
    [InlineData("th ick", "Thickness", true)]
    [InlineData("seet", "See-through", true)]
    [InlineData("widths", "Width", false)]
    public void Letters_in_order_any_case(string filter, string name, bool matches) =>
        Assert.Equal(matches, PropertyFilter.Matches(System.Text.Encoding.UTF8.GetBytes(filter), System.Text.Encoding.UTF8.GetBytes(name)));
}
