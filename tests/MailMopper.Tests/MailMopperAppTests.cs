using MailMopper.Tui;

namespace MailMopper.Tests;

public class MailMopperAppTests
{
    [Fact]
    public void NextTab_FromHome_ReturnsFetch()
    {
        var result = MailMopperApp.NextTab(AppTab.Home);
        Assert.Equal(AppTab.Fetch, result);
    }

    [Fact]
    public void NextTab_FromFetch_ReturnsClassify()
    {
        var result = MailMopperApp.NextTab(AppTab.Fetch);
        Assert.Equal(AppTab.Classify, result);
    }

    [Fact]
    public void NextTab_FromUndo_WrapsToHome()
    {
        var result = MailMopperApp.NextTab(AppTab.Undo);
        Assert.Equal(AppTab.Home, result);
    }

    [Fact]
    public void PreviousTab_FromHome_WrapsToUndo()
    {
        var result = MailMopperApp.PreviousTab(AppTab.Home);
        Assert.Equal(AppTab.Undo, result);
    }

    [Fact]
    public void PreviousTab_FromFetch_ReturnsHome()
    {
        var result = MailMopperApp.PreviousTab(AppTab.Fetch);
        Assert.Equal(AppTab.Home, result);
    }

    [Fact]
    public void PreviousTab_FromUndo_ReturnsExecute()
    {
        var result = MailMopperApp.PreviousTab(AppTab.Undo);
        Assert.Equal(AppTab.Execute, result);
    }

    [Fact]
    public void NextTab_PreviousTab_RoundTrip_ReturnsOriginal()
    {
        foreach (AppTab tab in Enum.GetValues<AppTab>())
        {
            var next = MailMopperApp.NextTab(tab);
            var back = MailMopperApp.PreviousTab(next);
            Assert.Equal(tab, back);
        }
    }

    [Fact]
    public void NextTab_AllTabs_ProducesDistinctValues()
    {
        var seen = new HashSet<AppTab>();
        var current = AppTab.Home;
        for (var i = 0; i < 6; i++)
        {
            seen.Add(current);
            current = MailMopperApp.NextTab(current);
        }

        Assert.Equal(6, seen.Count);
    }
}
