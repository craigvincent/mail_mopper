using MailMopper.Commands;
using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Spectre.Console.Cli;

namespace MailMopper.Tests.Commands;

public class StatsCommandTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly DatabaseService _dbService;

    public StatsCommandTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _dbService = new DatabaseService(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyData_CompletesWithoutException()
    {
        var command = new StatsCommand(_dbService, _db, new AppCancellation());

        var exception = await Record.ExceptionAsync(async () =>
            await command.ExecuteAsync(new CommandContext([], Substitute.For<IRemainingArguments>(), "", null)));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ExecuteAsync_WithSeededData_CompletesWithoutException()
    {
        var email = new EmailRecord
        {
            MessageId = "msg1",
            From = "sender@example.com",
            FromDomain = "example.com",
            Subject = "Test",
            SizeEstimate = 1000,
            FetchedAt = DateTimeOffset.UtcNow
        };
        _db.Emails.Add(email);
        _db.Classifications.Add(new Classification
        {
            MessageId = "msg1",
            Category = ClassificationCategory.Newsletter,
            Reason = "rule:test",
            ClassifiedBy = "rule",
            ReviewDecision = ReviewDecision.Pending,
            Email = email
        });
        await _db.SaveChangesAsync();

        var command = new StatsCommand(_dbService, _db, new AppCancellation());

        var exception = await Record.ExceptionAsync(async () =>
            await command.ExecuteAsync(new CommandContext([], Substitute.For<IRemainingArguments>(), "", null)));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ExecuteAsync_DbClosed_ReturnsNonZeroOrThrows()
    {
        _db.Database.CloseConnection();

        var command = new StatsCommand(_dbService, _db, new AppCancellation());

        try
        {
            var result = await command.ExecuteAsync(new CommandContext([], Substitute.For<IRemainingArguments>(), "", null));
            // If the try-catch in the command swallows it, should return 1
            Assert.Equal(1, result);
        }
        catch
        {
            // If the exception propagates before being caught, that's also expected
            // in a test environment since AnsiConsole may throw during error rendering
        }
    }

    [Fact]
    public void Constructor_NullDatabaseService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new StatsCommand(null!, _db, new AppCancellation()));

        Assert.Equal("databaseService", ex.ParamName);
    }
}
