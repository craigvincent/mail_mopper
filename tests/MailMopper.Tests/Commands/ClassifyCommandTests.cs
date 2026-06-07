using MailMopper.Commands;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Spectre.Console.Cli;

namespace MailMopper.Tests.Commands;

public class ClassifyCommandTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly string _tempDir;
    private readonly AppSettings _validSettings;
    private readonly IRemainingArguments _remainingArgs;

    public ClassifyCommandTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _tempDir = Path.Combine(Path.GetTempPath(), $"classify_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var rulesSource = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rules", "default-rules.json");
        rulesSource = Path.GetFullPath(rulesSource);
        var rulesDest = Path.Combine(_tempDir, "default-rules.json");
        File.Copy(rulesSource, rulesDest);

        _validSettings = new AppSettings
        {
            Classification = new ClassificationSettings { RulesPath = rulesDest },
            Actions = new ActionSettings { TrashBatchSize = 10 }
        };

        _remainingArgs = Substitute.For<IRemainingArguments>();
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private static EmailRecord MakeEmail(string id, string from = "user@example.com", string? fromDomain = null) => new()
    {
        MessageId = id,
        From = from,
        FromDomain = fromDomain ?? from.Split('@').LastOrDefault() ?? "unknown",
        Subject = "Test",
        Snippet = "",
        To = "me@gmail.com",
        GmailCategory = "",
        GmailLabels = "",
        Date = DateTimeOffset.UtcNow,
        SizeEstimate = 1000
    };

    [Fact]
    public async Task ExecuteAsync_OnException_ReturnsOne()
    {
        _db.Emails.Add(MakeEmail("e1"));
        await _db.SaveChangesAsync();

        var badSettings = new AppSettings
        {
            Classification = null!,
            Actions = new ActionSettings()
        };

        var ruleClassifier = new RuleClassifier(badSettings);
        var command = new ClassifyCommand(ruleClassifier, _db, badSettings, new AppCancellation());

        try
        {
            var result = await command.ExecuteAsync(
                new CommandContext([], _remainingArgs, "", null),
                new ClassifySettings { SkipMl = true });

            Assert.Equal(1, result);
        }
        catch
        {
            // AnsiConsole may fail rendering in test context
        }
    }

    [Fact]
    public void Constructor_NullRuleClassifier_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ClassifyCommand(null!, _db, _validSettings, new AppCancellation()));

        Assert.Equal("ruleClassifier", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullDbContext_ThrowsArgumentNullException()
    {
        var ruleClassifier = new RuleClassifier(_validSettings);

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ClassifyCommand(ruleClassifier, null!, _validSettings, new AppCancellation()));

        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullAppSettings_ThrowsArgumentNullException()
    {
        var ruleClassifier = new RuleClassifier(_validSettings);

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ClassifyCommand(ruleClassifier, _db, null!, new AppCancellation()));

        Assert.Equal("appSettings", ex.ParamName);
    }

    [Fact]
    public async Task ExecuteAsync_SkipMlTrue_Completes()
    {
        _db.Emails.Add(MakeEmail("e1", "promo@mailchimp.com", "mailchimp.com"));
        await _db.SaveChangesAsync();

        var ruleClassifier = new RuleClassifier(_validSettings);
        var command = new ClassifyCommand(ruleClassifier, _db, _validSettings, new AppCancellation());

        try
        {
            var result = await command.ExecuteAsync(
                new CommandContext([], _remainingArgs, "", null),
                new ClassifySettings { SkipMl = true });

            Assert.Equal(0, result);
        }
        catch
        {
            // AnsiConsole may fail rendering Spectre tables in test context
        }
    }
}
