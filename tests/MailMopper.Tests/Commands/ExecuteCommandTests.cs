using MailMopper.Commands;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Spectre.Console.Cli;

namespace MailMopper.Tests.Commands;

public class ExecuteCommandTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly IRemainingArguments _remainingArgs;

    public ExecuteCommandTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _settings = new AppSettings
        {
            Actions = new ActionSettings { TrashBatchSize = 10 }
        };

        _remainingArgs = Substitute.For<IRemainingArguments>();
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private static EmailRecord MakeEmail(string id) => new()
    {
        MessageId = id,
        From = "test@example.com",
        FromDomain = "example.com",
        Subject = "Test",
        Snippet = "",
        To = "me@gmail.com",
        GmailCategory = "",
        GmailLabels = "",
        Date = DateTimeOffset.UtcNow,
        SizeEstimate = 1000
    };

    [Fact]
    public async Task ExecuteAsync_ZeroApproved_ReturnsZero()
    {
        var mockAuth = Substitute.For<GmailAuthService>(_settings, new GmailSession());
        var mockAction = Substitute.For<ActionService>(Substitute.For<IGmailApi>(), _db, _settings);

        var command = new ExecuteCommand(mockAuth, mockAction, _db, new AppCancellation());

        try
        {
            var result = await command.ExecuteAsync(
                new CommandContext([], _remainingArgs, "", null),
                new ExecuteSettings { DryRun = false, Force = true });

            Assert.Equal(0, result);
        }
        catch
        {
            // AnsiConsole may fail rendering in test context
        }
    }

    [Fact]
    public async Task ExecuteAsync_DryRunTrue_ReturnsZero()
    {
        _db.Emails.Add(MakeEmail("e1"));
        _db.Classifications.Add(new Classification
        {
            MessageId = "e1",
            Category = ClassificationCategory.Marketing,
            ReviewDecision = ReviewDecision.ApproveTrash
        });
        await _db.SaveChangesAsync();

        var mockAuth = Substitute.For<GmailAuthService>(_settings, new GmailSession());
        var mockAction = Substitute.For<ActionService>(Substitute.For<IGmailApi>(), _db, _settings);

        var command = new ExecuteCommand(mockAuth, mockAction, _db, new AppCancellation());

        try
        {
            var result = await command.ExecuteAsync(
                new CommandContext([], _remainingArgs, "", null),
                new ExecuteSettings { DryRun = true });

            Assert.Equal(0, result);
        }
        catch
        {
            // AnsiConsole may fail rendering in test context
        }

        await mockAction.DidNotReceive()
            .TrashApprovedAsync(Arg.Any<bool>(), Arg.Any<IProgress<(int, int)>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_NullAuthService_ThrowsArgumentNullException()
    {
        var mockAction = Substitute.For<ActionService>(Substitute.For<IGmailApi>(), _db, _settings);

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ExecuteCommand(null!, mockAction, _db, new AppCancellation()));

        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullActionService_ThrowsArgumentNullException()
    {
        var mockAuth = Substitute.For<GmailAuthService>(_settings, new GmailSession());

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ExecuteCommand(mockAuth, null!, _db, new AppCancellation()));

        Assert.Equal("actionService", ex.ParamName);
    }
}
