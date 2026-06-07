using MailMopper.Commands;
using MailMopper.Config;
using MailMopper.Services;

namespace MailMopper.Tests.Commands;

public class ResetCommandTests
{
    private readonly AppSettings _settings = new();
    private readonly AppCancellation _cancellation = new();

    [Fact]
    public void Constructor_NullAppSettings_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ResetCommand(null!, _cancellation));
        Assert.Equal("appSettings", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ResetCommand(_settings, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }
}
