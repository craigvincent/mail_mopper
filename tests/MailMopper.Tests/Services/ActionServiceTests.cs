using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MailMopper.Tests.Services;

public class ActionServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IGmailApi _gmailApi;
    private readonly AppSettings _settings;

    public ActionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _gmailApi = Substitute.For<IGmailApi>();
        _settings = new AppSettings
        {
            Actions = new ActionSettings { TrashBatchSize = 10 }
        };
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private static EmailRecord MakeEmail(string id, string from = "test@example.com", string fromDomain = "example.com", long size = 1000) => new()
    {
        MessageId = id,
        From = from,
        FromDomain = fromDomain,
        Subject = "Test",
        Date = DateTimeOffset.UtcNow,
        SizeEstimate = size,
        GmailCategory = "",
        GmailLabels = "",
        FetchedAt = DateTimeOffset.UtcNow
    };

    private ActionService CreateService(AppSettings? settings = null) =>
        new(_gmailApi, _db, settings ?? _settings);

    [Fact]
    public async Task TrashApprovedAsync_DryRunTrue_DoesNotCallGmailApi()
    {
        _db.Emails.AddRange(
            MakeEmail("dr1", size: 500),
            MakeEmail("dr2", size: 1000));
        _db.Classifications.AddRange(
            new Classification { MessageId = "dr1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" },
            new Classification { MessageId = "dr2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var service = CreateService();
        await service.TrashApprovedAsync(dryRun: true, progress: null, CancellationToken.None);

        await _gmailApi.DidNotReceive().BatchModifyAsync(
            Arg.Any<IList<string>>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrashApprovedAsync_DryRunTrue_ReturnsCorrectCounts()
    {
        _db.Emails.AddRange(
            MakeEmail("c1", size: 100),
            MakeEmail("c2", size: 200),
            MakeEmail("c3", size: 300));
        _db.Classifications.AddRange(
            new Classification { MessageId = "c1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" },
            new Classification { MessageId = "c2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" },
            new Classification { MessageId = "c3", Category = ClassificationCategory.Social, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var service = CreateService();
        var result = await service.TrashApprovedAsync(dryRun: true, progress: null, CancellationToken.None);

        Assert.Equal(3, result.EmailsTrashed);
        Assert.Equal(0, result.Errors);
        Assert.Equal(600, result.EstimatedSpaceFreed);
    }

    [Fact]
    public async Task TrashApprovedAsync_DryRunTrue_SessionIdIsNonNull()
    {
        _db.Emails.Add(MakeEmail("s1", size: 500));
        _db.Classifications.Add(
            new Classification { MessageId = "s1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var service = CreateService();
        var result = await service.TrashApprovedAsync(dryRun: true, progress: null, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.SessionId));
    }

    [Fact]
    public async Task TrashApprovedAsync_RealRun_CallsBatchModifyWithTrashLabel()
    {
        _db.Emails.Add(MakeEmail("r1", size: 1000));
        _db.Classifications.Add(
            new Classification { MessageId = "r1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var service = CreateService();
        await service.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        await _gmailApi.Received(1).BatchModifyAsync(
            Arg.Is<IList<string>>(ids => ids != null && ids.Contains("r1")),
            Arg.Is<IList<string>?>(labels => labels != null && labels.Contains("TRASH")),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrashApprovedAsync_RealRun_CreatesActionRecords()
    {
        _db.Emails.Add(MakeEmail("ar1", size: 1000));
        _db.Classifications.Add(
            new Classification { MessageId = "ar1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var service = CreateService();
        await service.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        var actions = await _db.Actions.ToListAsync();
        Assert.Single(actions);
        Assert.Equal("trash", actions[0].Action);
        Assert.Equal("ar1", actions[0].MessageId);
        Assert.Equal("rule", actions[0].Reason);
    }

    [Fact]
    public async Task TrashApprovedAsync_RealRun_MarksClassificationsAsExecuted()
    {
        _db.Emails.AddRange(
            MakeEmail("e1", size: 1000),
            MakeEmail("e2", size: 2000));
        _db.Classifications.AddRange(
            new Classification { MessageId = "e1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" },
            new Classification { MessageId = "e2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var service = CreateService();
        await service.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        var classifications = await _db.Classifications.ToListAsync();
        Assert.All(classifications, c => Assert.Equal(ReviewDecision.Executed, c.ReviewDecision));
    }

    [Fact]
    public async Task TrashApprovedAsync_NoApprovedEmails_ReturnsZeroCounts()
    {
        var service = CreateService();
        var result = await service.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        Assert.Equal(0, result.EmailsTrashed);
        Assert.Equal(0, result.Errors);
        Assert.Equal(0, result.EstimatedSpaceFreed);
    }

    [Fact]
    public async Task TrashApprovedAsync_ApiFailure_CountsAsErrors()
    {
        _gmailApi.BatchModifyAsync(
            Arg.Any<IList<string>>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("API error"));

        _db.Emails.AddRange(
            MakeEmail("fail1", size: 500),
            MakeEmail("fail2", size: 500));
        _db.Classifications.AddRange(
            new Classification { MessageId = "fail1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" },
            new Classification { MessageId = "fail2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var service = CreateService();
        var result = await service.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        Assert.Equal(0, result.EmailsTrashed);
        Assert.Equal(2, result.Errors);
        Assert.Equal(0, result.EstimatedSpaceFreed);
    }

    [Fact]
    public async Task TrashApprovedAsync_BatchSplit_BeyondBatchSize()
    {
        var smallBatchSettings = new AppSettings { Actions = new ActionSettings { TrashBatchSize = 2 } };

        _db.Emails.AddRange(
            MakeEmail("b1", size: 100),
            MakeEmail("b2", size: 200),
            MakeEmail("b3", size: 300));
        _db.Classifications.AddRange(
            new Classification { MessageId = "b1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" },
            new Classification { MessageId = "b2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" },
            new Classification { MessageId = "b3", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash, Reason = "rule" });
        await _db.SaveChangesAsync();

        var receivedIds = new List<IList<string>>();
        _gmailApi.BatchModifyAsync(
            Arg.Do<IList<string>>(ids => receivedIds.Add(ids)),
            Arg.Any<IList<string>?>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var service = CreateService(smallBatchSettings);
        await service.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        // Should be 2 calls: one with 2 IDs, one with 1 ID
        Assert.Equal(2, receivedIds.Count);
        Assert.Equal(2, receivedIds[0].Count);
        Assert.Single(receivedIds[1]);
    }

    [Fact]
    public async Task UndoSessionAsync_EmptySessionId_ThrowsArgumentException()
    {
        var service = CreateService();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UndoSessionAsync("", progress: null, CancellationToken.None));

        Assert.Contains("Session ID", ex.Message);
        Assert.Equal("sessionId", ex.ParamName);
    }

    [Fact]
    public async Task UndoSessionAsync_SeedsAndUndoesCorrectSession()
    {
        _db.Actions.AddRange(
            new ActionRecord { MessageId = "u1", Action = "trash", SessionId = "s1", Reason = "test", PerformedAt = DateTimeOffset.UtcNow },
            new ActionRecord { MessageId = "u2", Action = "trash", SessionId = "s1", Reason = "test", PerformedAt = DateTimeOffset.UtcNow },
            new ActionRecord { MessageId = "u3", Action = "trash", SessionId = "s2", Reason = "test", PerformedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var service = CreateService();
        var count = await service.UndoSessionAsync("s1", progress: null, CancellationToken.None);

        Assert.Equal(2, count);

        await _gmailApi.Received(1).BatchModifyAsync(
            Arg.Is<IList<string>>(ids => ids != null && ids.Contains("u1") && ids.Contains("u2") && !ids.Contains("u3")),
            Arg.Any<IList<string>?>(),
            Arg.Is<IList<string>?>(labels => labels != null && labels.Contains("TRASH")),
            Arg.Any<CancellationToken>());

        var undoRecords = await _db.Actions.Where(a => a.Action == "untrash").ToListAsync();
        Assert.Equal(2, undoRecords.Count);
        Assert.All(undoRecords, r => Assert.Equal("s1", r.SessionId));
    }

    [Fact]
    public async Task UndoSessionAsync_NoMatchingSession_DoesNotCallApi()
    {
        var service = CreateService();
        var count = await service.UndoSessionAsync("nonexistent", progress: null, CancellationToken.None);

        Assert.Equal(0, count);

        await _gmailApi.DidNotReceive().BatchModifyAsync(
            Arg.Any<IList<string>>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>());
    }
}
