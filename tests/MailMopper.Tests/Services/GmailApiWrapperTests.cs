using MailMopper.Services;

namespace MailMopper.Tests.Services;

public class GmailApiWrapperTests
{
    [Fact]
    public void Constructor_NullSession_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new GmailApiWrapper(null!));
        Assert.Equal("session", ex.ParamName);
    }

    [Fact]
    public async Task BatchModifyAsync_NullGmailService_ThrowsInvalidOperationException()
    {
        var session = new GmailSession { Service = null };
        var wrapper = new GmailApiWrapper(session);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wrapper.BatchModifyAsync(["msg1"], addLabelIds: null, removeLabelIds: null, CancellationToken.None));

        Assert.Contains("not authenticated", ex.Message);
    }

    [Fact]
    public void Implements_IGmailApi_Interface()
    {
        var wrapper = new GmailApiWrapper(new GmailSession());
        var asInterface = wrapper as IGmailApi;

        Assert.NotNull(asInterface);
    }
}
