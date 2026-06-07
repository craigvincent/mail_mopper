using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Tests.Services;

public class DatabaseServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly DatabaseService _service;

    public DatabaseServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _service = new DatabaseService(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private static EmailRecord MakeEmail(string id, string from = "user@example.com", string domain = "example.com", long size = 1000) => new()
    {
        MessageId = id,
        From = from,
        FromDomain = domain,
        Subject = "Test",
        Snippet = "",
        Date = DateTimeOffset.UtcNow,
        SizeEstimate = size,
        GmailCategory = "",
        GmailLabels = ""
    };

    [Fact]
    public async Task GetStatsReturnsCorrectCounts()
    {
        // Arrange: 3 emails, 2 classified, 1 approved for trash, 1 trashed
        _db.Emails.AddRange(
            MakeEmail("m1"),
            MakeEmail("m2"),
            MakeEmail("m3"));

        _db.Classifications.AddRange(
            new Classification { MessageId = "m1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash },
            new Classification { MessageId = "m2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.Pending });

        _db.Actions.Add(new ActionRecord { MessageId = "m1", Action = "trash", SessionId = "s1" });
        await _db.SaveChangesAsync();

        // Act
        var stats = await _service.GetStatsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(3, stats.TotalEmails);
        Assert.Equal(2, stats.Classified);
        Assert.Equal(1, stats.Unclassified);
        Assert.Equal(1, stats.ApprovedForTrash);
        Assert.Equal(1, stats.Trashed);
        Assert.Equal(3000, stats.TotalSize);
    }

    [Fact]
    public async Task GetCategorySummaryGroupsCorrectly()
    {
        // Arrange: 2 marketing, 1 newsletter
        _db.Emails.AddRange(
            MakeEmail("m1", "promo@shop.com", "shop.com", 500),
            MakeEmail("m2", "promo@shop.com", "shop.com", 700),
            MakeEmail("m3", "news@blog.com", "blog.com", 300));

        _db.Classifications.AddRange(
            new Classification { MessageId = "m1", Category = ClassificationCategory.Marketing },
            new Classification { MessageId = "m2", Category = ClassificationCategory.Marketing },
            new Classification { MessageId = "m3", Category = ClassificationCategory.Newsletter });
        await _db.SaveChangesAsync();

        // Act
        var summaries = await _service.GetCategorySummaryAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, summaries.Count);

        var marketing = summaries.Single(s => s.Category == ClassificationCategory.Marketing);
        Assert.Equal(2, marketing.Count);
        Assert.Equal(1200, marketing.TotalSize);

        var newsletter = summaries.Single(s => s.Category == ClassificationCategory.Newsletter);
        Assert.Equal(1, newsletter.Count);
        Assert.Equal(300, newsletter.TotalSize);
    }

    [Fact]
    public async Task WhitelistWorksCorrectly()
    {
        // Arrange & Act: add a domain whitelist entry
        await _service.AddWhitelistAsync("trusted.com", "domain", CancellationToken.None);

        // Assert
        var isWhitelisted = await _service.IsWhitelistedAsync("trusted.com", "someone@trusted.com", CancellationToken.None);
        Assert.True(isWhitelisted);

        var isNotWhitelisted = await _service.IsWhitelistedAsync("other.com", "someone@other.com", CancellationToken.None);
        Assert.False(isNotWhitelisted);
    }

    [Fact]
    public async Task GetTopSendersOrdersByCount()
    {
        // Arrange: sender A has 3 emails, sender B has 1
        _db.Emails.AddRange(
            MakeEmail("m1", "a@foo.com", "foo.com"),
            MakeEmail("m2", "a@foo.com", "foo.com"),
            MakeEmail("m3", "a@foo.com", "foo.com"),
            MakeEmail("m4", "b@bar.com", "bar.com"));

        _db.Classifications.AddRange(
            new Classification { MessageId = "m1", Category = ClassificationCategory.Marketing },
            new Classification { MessageId = "m2", Category = ClassificationCategory.Marketing },
            new Classification { MessageId = "m3", Category = ClassificationCategory.Marketing },
            new Classification { MessageId = "m4", Category = ClassificationCategory.Marketing });
        await _db.SaveChangesAsync();

        // Act
        var senders = await _service.GetTopSendersAsync(null, 10, CancellationToken.None);

        // Assert
        Assert.Equal(2, senders.Count);
        Assert.Equal("a@foo.com", senders[0].Sender);
        Assert.Equal(3, senders[0].Count);
        Assert.Equal("b@bar.com", senders[1].Sender);
        Assert.Equal(1, senders[1].Count);
    }
}
