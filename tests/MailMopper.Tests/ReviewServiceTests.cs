using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Tests;

public class ReviewServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly ReviewService _service;

    public ReviewServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _service = new ReviewService(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    #region Helpers

    private static EmailRecord MakeEmail(string id, string from = "user@example.com", string? fromDomain = null,
        string subject = "Test Subject", long size = 1000, DateTimeOffset? date = null) => new()
        {
            MessageId = id,
            From = from,
            FromDomain = fromDomain ?? from.Split('@').LastOrDefault() ?? "unknown",
            Subject = subject,
            Snippet = "",
            Date = date ?? DateTimeOffset.UtcNow,
            SizeEstimate = size,
            GmailCategory = "",
            GmailLabels = ""
        };

    private static Classification MakeClassification(string messageId, ClassificationCategory category = ClassificationCategory.Unclassified,
        ReviewDecision decision = ReviewDecision.Pending, EmailRecord? email = null) => new()
        {
            MessageId = messageId,
            Category = category,
            ReviewDecision = decision,
            ClassifiedBy = "rule",
            Reason = "test",
            Email = email ?? MakeEmail(messageId)
        };

    #endregion

    #region FormatSize

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1_048_575, "1024.0 KB")]
    [InlineData(1_048_576, "1.0 MB")]
    [InlineData(1_600_000, "1.5 MB")]
    [InlineData(1_073_741_823, "1024.0 MB")]
    [InlineData(1_073_741_824, "1.0 GB")]
    [InlineData(2_000_000_000, "1.9 GB")]
    public void FormatSize_VariousBytes_ReturnsExpectedString(long bytes, string expected)
    {
        var result = ReviewService.FormatSize(bytes);
        Assert.Equal(expected, result);
    }

    #endregion

    #region FormatDecision

    [Fact]
    public void FormatDecision_ApproveTrash_ReturnsRedTrash()
    {
        Assert.Equal("[red]Trash[/]", ReviewService.FormatDecision(ReviewDecision.ApproveTrash));
    }

    [Fact]
    public void FormatDecision_Keep_ReturnsGreenKeep()
    {
        Assert.Equal("[green]Keep[/]", ReviewService.FormatDecision(ReviewDecision.Keep));
    }

    [Fact]
    public void FormatDecision_Whitelisted_ReturnsCyanWhitelisted()
    {
        Assert.Equal("[cyan]Whitelisted[/]", ReviewService.FormatDecision(ReviewDecision.Whitelisted));
    }

    [Fact]
    public void FormatDecision_Pending_ReturnsYellowPending()
    {
        Assert.Equal("[yellow]Pending[/]", ReviewService.FormatDecision(ReviewDecision.Pending));
    }

    [Fact]
    public void FormatDecision_Executed_FallsThroughToPending()
    {
        Assert.Equal("[yellow]Pending[/]", ReviewService.FormatDecision(ReviewDecision.Executed));
    }

    #endregion

    #region ComputeGroupDecision

    [Fact]
    public void ComputeGroupDecision_AllApproveTrash_ReturnsApproveTrash()
    {
        var group = new[] { ClassificationCategory.Marketing }
            .GroupBy(_ => _)
            .SelectMany(g => Enumerable.Range(0, 3).Select(_ =>
                MakeClassification($"m{_}", ClassificationCategory.Marketing, ReviewDecision.ApproveTrash)))
            .GroupBy(c => c.Category)
            .First();

        var result = ReviewService.ComputeGroupDecision(group);

        Assert.Equal(ReviewDecision.ApproveTrash, result);
    }

    [Fact]
    public void ComputeGroupDecision_AllKeep_ReturnsKeep()
    {
        var group = new[] { ClassificationCategory.Newsletter }
            .GroupBy(_ => _)
            .SelectMany(g => Enumerable.Range(0, 2).Select(_ =>
                MakeClassification($"n{_}", ClassificationCategory.Newsletter, ReviewDecision.Keep)))
            .GroupBy(c => c.Category)
            .First();

        var result = ReviewService.ComputeGroupDecision(group);

        Assert.Equal(ReviewDecision.Keep, result);
    }

    [Fact]
    public void ComputeGroupDecision_Mixed_ReturnsPending()
    {
        var classifications = new List<Classification>
        {
            MakeClassification("a", ClassificationCategory.Marketing, ReviewDecision.ApproveTrash),
            MakeClassification("b", ClassificationCategory.Marketing, ReviewDecision.Keep)
        };
        var group = classifications.GroupBy(c => c.Category).First();

        var result = ReviewService.ComputeGroupDecision(group);

        Assert.Equal(ReviewDecision.Pending, result);
    }

    [Fact]
    public void ComputeGroupDecision_AllWhitelisted_ReturnsWhitelisted()
    {
        var classifications = new List<Classification>
        {
            MakeClassification("w1", ClassificationCategory.Marketing, ReviewDecision.Whitelisted),
            MakeClassification("w2", ClassificationCategory.Marketing, ReviewDecision.Whitelisted)
        };
        var group = classifications.GroupBy(c => c.Category).First();

        var result = ReviewService.ComputeGroupDecision(group);

        Assert.Equal(ReviewDecision.Whitelisted, result);
    }

    #endregion

    #region BuildSenderList

    [Fact]
    public void BuildSenderList_SingleSender_ReturnsOneGroup()
    {
        var catGroup = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Marketing,
            Classifications =
            [
                MakeClassification("a1", ClassificationCategory.Marketing, email: MakeEmail("a1", "foo@bar.com", "bar.com")),
                MakeClassification("a2", ClassificationCategory.Marketing, email: MakeEmail("a2", "foo@bar.com", "bar.com"))
            ]
        };

        var result = ReviewService.BuildSenderList(catGroup);

        Assert.Single(result);
        Assert.Equal("foo@bar.com", result[0].From);
        Assert.Equal("bar.com", result[0].Domain);
        Assert.Equal(2, result[0].Classifications.Count);
        Assert.Equal(ReviewDecision.Pending, result[0].Decision);
    }

    [Fact]
    public void BuildSenderList_MultipleSenders_OrdersByCountDescending()
    {
        var catGroup = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Marketing,
            Classifications =
            [
                MakeClassification("a1", ClassificationCategory.Marketing, email: MakeEmail("a1", "frequent@domain.com", "domain.com")),
                MakeClassification("a2", ClassificationCategory.Marketing, email: MakeEmail("a2", "frequent@domain.com", "domain.com")),
                MakeClassification("a3", ClassificationCategory.Marketing, email: MakeEmail("a3", "frequent@domain.com", "domain.com")),
                MakeClassification("b1", ClassificationCategory.Marketing, email: MakeEmail("b1", "rare@other.com", "other.com"))
            ]
        };

        var result = ReviewService.BuildSenderList(catGroup);

        Assert.Equal(2, result.Count);
        Assert.Equal("frequent@domain.com", result[0].From);
        Assert.Equal(3, result[0].Classifications.Count);
        Assert.Equal("rare@other.com", result[1].From);
        Assert.Single(result[1].Classifications);
    }

    [Fact]
    public void BuildSenderList_NullEmailProps_FallsBackToUnknown()
    {
        var catGroup = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Marketing,
            Classifications =
            [
                new Classification
                {
                    MessageId = "x1",
                    Category = ClassificationCategory.Marketing,
                    Email = null
                }
            ]
        };

        var result = ReviewService.BuildSenderList(catGroup);

        Assert.Single(result);
        Assert.Equal("unknown", result[0].From);
        Assert.Equal("unknown", result[0].Domain);
    }

    [Fact]
    public void BuildSenderList_SingleSenderWithDecisions_ComputesSenderDecision()
    {
        var catGroup = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Marketing,
            Classifications =
            [
                MakeClassification("k1", ClassificationCategory.Marketing, ReviewDecision.Keep, MakeEmail("k1", "sender@domain.com", "domain.com")),
                MakeClassification("k2", ClassificationCategory.Marketing, ReviewDecision.Keep, MakeEmail("k2", "sender@domain.com", "domain.com"))
            ]
        };

        var result = ReviewService.BuildSenderList(catGroup);

        Assert.Single(result);
        Assert.Equal(ReviewDecision.Keep, result[0].Decision);
    }

    [Fact]
    public void BuildSenderList_MixedDecisions_ReturnsPending()
    {
        var catGroup = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Marketing,
            Classifications =
            [
                MakeClassification("m1", ClassificationCategory.Marketing, ReviewDecision.Keep, MakeEmail("m1", "sender@domain.com", "domain.com")),
                MakeClassification("m2", ClassificationCategory.Marketing, ReviewDecision.ApproveTrash, MakeEmail("m2", "sender@domain.com", "domain.com"))
            ]
        };

        var result = ReviewService.BuildSenderList(catGroup);

        Assert.Single(result);
        Assert.Equal(ReviewDecision.Pending, result[0].Decision);
    }

    #endregion

    #region FindNextPendingIndex

    [Fact]
    public void FindNextPendingIndex_NextIsPending_ReturnsNextIndex()
    {
        var senders = new List<ReviewSenderGroup>
        {
            new() { Decision = ReviewDecision.Keep },
            new() { Decision = ReviewDecision.Pending },
            new() { Decision = ReviewDecision.ApproveTrash }
        };

        var result = ReviewService.FindNextPendingIndex(senders, 0);

        Assert.Equal(1, result);
    }

    [Fact]
    public void FindNextPendingIndex_WrapsAround_ReturnsWrappedIndex()
    {
        var senders = new List<ReviewSenderGroup>
        {
            new() { Decision = ReviewDecision.Pending },
            new() { Decision = ReviewDecision.Keep },
            new() { Decision = ReviewDecision.Keep }
        };

        var result = ReviewService.FindNextPendingIndex(senders, 1);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FindNextPendingIndex_NoPending_ReturnsNegativeOne()
    {
        var senders = new List<ReviewSenderGroup>
        {
            new() { Decision = ReviewDecision.Keep },
            new() { Decision = ReviewDecision.ApproveTrash },
            new() { Decision = ReviewDecision.Whitelisted }
        };

        var result = ReviewService.FindNextPendingIndex(senders, 0);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindNextPendingIndex_CurrentIsLastPending_ReturnsNegativeOne()
    {
        var senders = new List<ReviewSenderGroup>
        {
            new() { Decision = ReviewDecision.Keep },
            new() { Decision = ReviewDecision.Pending }
        };

        var result = ReviewService.FindNextPendingIndex(senders, 1);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindNextPendingIndex_EmptyList_ReturnsNegativeOne()
    {
        var senders = new List<ReviewSenderGroup>();

        var result = ReviewService.FindNextPendingIndex(senders, 0);

        Assert.Equal(-1, result);
    }

    #endregion

    #region ApplySenderDecision

    [Fact]
    public void ApplySenderDecision_SetsAllClassificationsAndGroup()
    {
        var sender = new ReviewSenderGroup
        {
            From = "test@domain.com",
            Domain = "domain.com",
            Classifications =
            [
                MakeClassification("s1", ClassificationCategory.Marketing, ReviewDecision.Pending),
                MakeClassification("s2", ClassificationCategory.Marketing, ReviewDecision.Pending),
                MakeClassification("s3", ClassificationCategory.Marketing, ReviewDecision.Pending)
            ],
            Decision = ReviewDecision.Pending
        };

        ReviewService.ApplySenderDecision(sender, ReviewDecision.ApproveTrash);

        Assert.Equal(ReviewDecision.ApproveTrash, sender.Decision);
        Assert.All(sender.Classifications, c => Assert.Equal(ReviewDecision.ApproveTrash, c.ReviewDecision));
    }

    [Fact]
    public void ApplySenderDecision_WhitelistedDecision_SetsOnAll()
    {
        var sender = new ReviewSenderGroup
        {
            From = "good@domain.com",
            Domain = "domain.com",
            Classifications = [MakeClassification("w1", ClassificationCategory.Marketing, ReviewDecision.Pending)],
            Decision = ReviewDecision.Pending
        };

        ReviewService.ApplySenderDecision(sender, ReviewDecision.Whitelisted);

        Assert.Equal(ReviewDecision.Whitelisted, sender.Decision);
        Assert.Equal(ReviewDecision.Whitelisted, sender.Classifications[0].ReviewDecision);
    }

    #endregion

    #region UpdateCategoryDecision

    [Fact]
    public void UpdateCategoryDecision_AllTrash_SetsApproveTrash()
    {
        var group = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Marketing,
            Classifications =
            [
                MakeClassification("t1", ClassificationCategory.Marketing, ReviewDecision.ApproveTrash),
                MakeClassification("t2", ClassificationCategory.Marketing, ReviewDecision.ApproveTrash)
            ],
            Decision = ReviewDecision.Pending
        };

        ReviewService.UpdateCategoryDecision(group);

        Assert.Equal(ReviewDecision.ApproveTrash, group.Decision);
    }

    [Fact]
    public void UpdateCategoryDecision_AllKeep_SetsKeep()
    {
        var group = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Newsletter,
            Classifications =
            [
                MakeClassification("k1", ClassificationCategory.Newsletter, ReviewDecision.Keep)
            ],
            Decision = ReviewDecision.Pending
        };

        ReviewService.UpdateCategoryDecision(group);

        Assert.Equal(ReviewDecision.Keep, group.Decision);
    }

    [Fact]
    public void UpdateCategoryDecision_AllWhitelisted_SetsWhitelisted()
    {
        var group = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Newsletter,
            Classifications =
            [
                MakeClassification("w1", ClassificationCategory.Newsletter, ReviewDecision.Whitelisted),
                MakeClassification("w2", ClassificationCategory.Newsletter, ReviewDecision.Whitelisted)
            ],
            Decision = ReviewDecision.Pending
        };

        ReviewService.UpdateCategoryDecision(group);

        Assert.Equal(ReviewDecision.Whitelisted, group.Decision);
    }

    [Fact]
    public void UpdateCategoryDecision_Mixed_SetsPending()
    {
        var group = new ReviewCategoryGroup
        {
            Category = ClassificationCategory.Marketing,
            Classifications =
            [
                MakeClassification("x1", ClassificationCategory.Marketing, ReviewDecision.ApproveTrash),
                MakeClassification("x2", ClassificationCategory.Marketing, ReviewDecision.Keep)
            ],
            Decision = ReviewDecision.ApproveTrash
        };

        ReviewService.UpdateCategoryDecision(group);

        Assert.Equal(ReviewDecision.Pending, group.Decision);
    }

    #endregion

    #region MarkDirty / AutoSaveIfNeeded

    [Fact]
    public void MarkDirty_SetsIsDirtyAndIncrementsUnsavedActions()
    {
        _service.MarkDirty(5);

        Assert.True(_service.IsDirty);
        Assert.Equal(5, _service.UnsavedActions);
    }

    [Fact]
    public void MarkDirty_AccumulatesUnsavedActions()
    {
        _service.MarkDirty(3);
        _service.MarkDirty(7);

        Assert.True(_service.IsDirty);
        Assert.Equal(10, _service.UnsavedActions);
    }

    [Fact]
    public void MarkDirty_DefaultCountIsOne()
    {
        _service.MarkDirty();

        Assert.Equal(1, _service.UnsavedActions);
    }

    [Fact]
    public async Task AutoSaveIfNeeded_BelowThreshold_DoesNotResetCounter()
    {
        _service.MarkDirty(19);

        await _service.AutoSaveIfNeededAsync(CancellationToken.None);

        Assert.Equal(19, _service.UnsavedActions);
    }

    [Fact]
    public async Task AutoSaveIfNeeded_AtThreshold_ResetsCounter()
    {
        _service.MarkDirty(20);

        await _service.AutoSaveIfNeededAsync(CancellationToken.None);

        Assert.Equal(0, _service.UnsavedActions);
    }

    [Fact]
    public async Task AutoSaveIfNeeded_AboveThreshold_ResetsCounter()
    {
        _service.MarkDirty(25);

        await _service.AutoSaveIfNeededAsync(CancellationToken.None);

        Assert.Equal(0, _service.UnsavedActions);
    }

    [Fact]
    public async Task AutoSaveIfNeeded_NotDirty_DoesNothing()
    {
        await _service.AutoSaveIfNeededAsync(CancellationToken.None);

        Assert.Equal(0, _service.UnsavedActions);
        Assert.False(_service.IsDirty);
    }

    #endregion

    #region SaveAsync

    [Fact]
    public async Task SaveAsync_ResetsUnsavedActions()
    {
        _service.MarkDirty(12);

        await _service.SaveAsync(CancellationToken.None);

        Assert.Equal(0, _service.UnsavedActions);
    }

    #endregion

    #region LoadDataAsync / RebuildGroups

    [Fact]
    public async Task LoadDataAsync_NoData_ReturnsEmptyGroups()
    {
        await _service.LoadDataAsync();

        Assert.Empty(_service.Groups);
        Assert.Empty(_service.AllReviewable);
        Assert.Empty(_service.AvailableYears);
        Assert.Equal(0, _service.PreviouslyTrashedCount);
        Assert.Equal(0, _service.PreviouslyTrashedSize);
    }

    [Fact]
    public async Task LoadDataAsync_GroupsByCategory()
    {
        _db.Emails.AddRange(
            MakeEmail("m1", "marketing@shop.com", "shop.com"),
            MakeEmail("m2", "news@blog.com", "blog.com"),
            MakeEmail("m3", "news@other.com", "other.com"));
        _db.Classifications.AddRange(
            new Classification { MessageId = "m1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "m2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "m3", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Equal(2, _service.Groups.Count);
        Assert.Equal(ClassificationCategory.Newsletter, _service.Groups[0].Category);
        Assert.Equal(2, _service.Groups[0].Classifications.Count);
        Assert.Equal(ClassificationCategory.Marketing, _service.Groups[1].Category);
        Assert.Single(_service.Groups[1].Classifications);
    }

    [Fact]
    public async Task LoadDataAsync_ExecutedClassifications_AreExcluded()
    {
        _db.Emails.AddRange(
            MakeEmail("e1", "keep@example.com", "example.com"),
            MakeEmail("e2", "trash@example.com", "example.com"));
        _db.Classifications.AddRange(
            new Classification { MessageId = "e1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "e2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Executed });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Single(_service.AllReviewable);
        Assert.Equal("e1", _service.AllReviewable[0].MessageId);
        Assert.Single(_service.Groups);
        Assert.Equal(1, _service.PreviouslyTrashedCount);
    }

    [Fact]
    public async Task LoadDataAsync_RetroFixesExecutedFromActionsTable()
    {
        _db.Emails.Add(MakeEmail("r1", "was-trashed@example.com", "example.com"));
        _db.Classifications.Add(new Classification
        {
            MessageId = "r1",
            Category = ClassificationCategory.Marketing,
            ReviewDecision = ReviewDecision.ApproveTrash
        });
        _db.Actions.Add(new ActionRecord
        {
            MessageId = "r1",
            Action = "trash",
            SessionId = "session-1"
        });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Equal(1, _service.PreviouslyTrashedCount);
        Assert.Empty(_service.AllReviewable);
        Assert.Empty(_service.Groups);

        var updated = await _db.Classifications.FirstAsync(c => c.MessageId == "r1");
        Assert.Equal(ReviewDecision.Executed, updated.ReviewDecision);
    }

    [Fact]
    public async Task LoadDataAsync_PreviouslyTrashedStats_AccumulateSize()
    {
        _db.Emails.AddRange(
            MakeEmail("pt1", "a@foo.com", "foo.com", size: 500),
            MakeEmail("pt2", "b@bar.com", "bar.com", size: 1500));
        _db.Classifications.AddRange(
            new Classification { MessageId = "pt1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Executed },
            new Classification { MessageId = "pt2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.Executed });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Equal(2, _service.PreviouslyTrashedCount);
        Assert.Equal(2000, _service.PreviouslyTrashedSize);
    }

    [Fact]
    public async Task LoadDataAsync_AvailableYears_ExtractedFromEmailDates()
    {
        _db.Emails.AddRange(
            MakeEmail("y1", "a@foo.com", "foo.com", date: new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("y2", "b@bar.com", "bar.com", date: new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("y3", "c@baz.com", "baz.com", date: new DateTimeOffset(2023, 12, 20, 0, 0, 0, TimeSpan.Zero)));
        _db.Classifications.AddRange(
            new Classification { MessageId = "y1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "y2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "y3", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Equal([2023, 2024], _service.AvailableYears);
    }

    [Fact]
    public async Task LoadDataAsync_EmailWithoutDate_SkippedInYears()
    {
        _db.Emails.Add(new EmailRecord
        {
            MessageId = "nd1",
            From = "a@foo.com",
            FromDomain = "foo.com",
            Subject = "Test",
            Date = null,
            SizeEstimate = 1000,
            GmailCategory = "",
            GmailLabels = ""
        });
        _db.Classifications.Add(new Classification
        {
            MessageId = "nd1",
            Category = ClassificationCategory.Marketing,
            ReviewDecision = ReviewDecision.Pending
        });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Empty(_service.AvailableYears);
    }

    [Fact]
    public async Task LoadDataAsync_Idempotent_SecondCallIsNoOp()
    {
        _db.Emails.Add(MakeEmail("i1", "a@foo.com", "foo.com"));
        _db.Classifications.Add(new Classification
        {
            MessageId = "i1",
            Category = ClassificationCategory.Marketing,
            ReviewDecision = ReviewDecision.Pending
        });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();
        var groupCount = _service.Groups.Count;

        await _service.LoadDataAsync();

        Assert.Equal(groupCount, _service.Groups.Count);
    }

    #endregion

    #region RebuildGroups with YearFilter

    [Fact]
    public async Task RebuildGroups_WithYearFilter_FiltersCorrectly()
    {
        _db.Emails.AddRange(
            MakeEmail("y2023a", "a@foo.com", "foo.com", date: new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("y2023b", "b@bar.com", "bar.com", date: new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("y2024", "c@baz.com", "baz.com", date: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _db.Classifications.AddRange(
            new Classification { MessageId = "y2023a", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "y2023b", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "y2024", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();
        _service.YearFilter = 2024;

        Assert.Single(_service.Groups);
        Assert.Equal("y2024", _service.Groups[0].Classifications[0].MessageId);
    }

    [Fact]
    public async Task RebuildGroups_YearFilterNone_ShowsAll()
    {
        _db.Emails.AddRange(
            MakeEmail("a1", "a@foo.com", "foo.com", date: new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("a2", "b@bar.com", "bar.com", date: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _db.Classifications.AddRange(
            new Classification { MessageId = "a1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "a2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Single(_service.Groups);
        Assert.Equal(2, _service.Groups[0].Classifications.Count);
    }

    [Fact]
    public async Task RebuildGroups_YearFilterNoMatch_ReturnsEmpty()
    {
        _db.Emails.Add(MakeEmail("nm1", "a@foo.com", "foo.com", date: new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _db.Classifications.Add(new Classification
        {
            MessageId = "nm1",
            Category = ClassificationCategory.Marketing,
            ReviewDecision = ReviewDecision.Pending
        });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();
        _service.YearFilter = 2020;

        Assert.Empty(_service.Groups);
    }

    #endregion

    #region ApplyBulkDecision

    [Fact]
    public async Task ApplyBulkDecision_AllPending_AppliesToAll()
    {
        _db.Emails.AddRange(
            MakeEmail("bd1", "a@foo.com", "foo.com"),
            MakeEmail("bd2", "b@bar.com", "bar.com"));
        _db.Classifications.AddRange(
            new Classification { MessageId = "bd1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "bd2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();
        await _service.LoadDataAsync();

        var group = _service.Groups[0];
        _service.ApplyBulkDecision(group, ReviewDecision.ApproveTrash);

        Assert.All(group.Classifications, c => Assert.Equal(ReviewDecision.ApproveTrash, c.ReviewDecision));
        Assert.True(_service.IsDirty);
        Assert.Equal(2, _service.UnsavedActions);
    }

    [Fact]
    public async Task ApplyBulkDecision_PartiallyDecided_OnlyPendingChanged()
    {
        _db.Emails.AddRange(
            MakeEmail("pd1", "a@foo.com", "foo.com"),
            MakeEmail("pd2", "b@bar.com", "bar.com"),
            MakeEmail("pd3", "c@baz.com", "baz.com"));
        _db.Classifications.AddRange(
            new Classification { MessageId = "pd1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "pd2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Keep },
            new Classification { MessageId = "pd3", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();
        await _service.LoadDataAsync();

        var group = _service.Groups[0];
        _service.ApplyBulkDecision(group, ReviewDecision.ApproveTrash);

        Assert.Equal(ReviewDecision.ApproveTrash, group.Classifications.First(c => c.MessageId == "pd1").ReviewDecision);
        Assert.Equal(ReviewDecision.Keep, group.Classifications.First(c => c.MessageId == "pd2").ReviewDecision);
        Assert.Equal(ReviewDecision.ApproveTrash, group.Classifications.First(c => c.MessageId == "pd3").ReviewDecision);
        Assert.Equal(2, _service.UnsavedActions);
    }

    [Fact]
    public async Task ApplyBulkDecision_NoPending_DoesNothing()
    {
        _db.Emails.Add(MakeEmail("np1", "a@foo.com", "foo.com"));
        _db.Classifications.Add(new Classification
        {
            MessageId = "np1",
            Category = ClassificationCategory.Marketing,
            ReviewDecision = ReviewDecision.Keep
        });
        await _db.SaveChangesAsync();
        await _service.LoadDataAsync();

        var group = _service.Groups[0];
        _service.ApplyBulkDecision(group, ReviewDecision.ApproveTrash);

        Assert.False(_service.IsDirty);
        Assert.Equal(0, _service.UnsavedActions);
        Assert.Equal(ReviewDecision.Keep, group.Classifications[0].ReviewDecision);
    }

    #endregion

    #region WhitelistDomain

    [Fact]
    public async Task WhitelistDomainAsync_NewDomain_InsertsAndSetsWhitelisted()
    {
        var sender = new ReviewSenderGroup
        {
            From = "good@trusted.com",
            Domain = "trusted.com",
            Classifications = [MakeClassification("wl1", ClassificationCategory.Marketing, ReviewDecision.Pending)],
            Decision = ReviewDecision.Pending
        };

        await _service.WhitelistDomainAsync("trusted.com", sender, CancellationToken.None);

        var entry = await _db.Whitelist.FirstOrDefaultAsync(w => w.Pattern == "trusted.com");
        Assert.NotNull(entry);
        Assert.Equal("domain", entry.PatternType);
        Assert.Equal(ReviewDecision.Whitelisted, sender.Decision);
        Assert.Equal(ReviewDecision.Whitelisted, sender.Classifications[0].ReviewDecision);
    }

    [Fact]
    public async Task WhitelistDomainAsync_ExistingDomain_DoesNotDuplicate()
    {
        _db.Whitelist.Add(new WhitelistEntry
        {
            Pattern = "existing.com",
            PatternType = "domain",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        var sender = new ReviewSenderGroup
        {
            From = "user@existing.com",
            Domain = "existing.com",
            Classifications = [MakeClassification("ew1", ClassificationCategory.Marketing, ReviewDecision.Pending)],
            Decision = ReviewDecision.Pending
        };

        await _service.WhitelistDomainAsync("existing.com", sender, CancellationToken.None);

        var count = await _db.Whitelist.CountAsync(w => w.Pattern == "existing.com");
        Assert.Equal(1, count);
        Assert.Equal(ReviewDecision.Whitelisted, sender.Decision);
    }

    #endregion

    #region Properties and Edge Cases

    [Fact]
    public async Task IndexOfYear_ReturnsCorrectIndex()
    {
        _db.Emails.AddRange(
            MakeEmail("iy1", "a@foo.com", "foo.com", date: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("iy2", "b@bar.com", "bar.com", date: new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("iy3", "c@baz.com", "baz.com", date: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        _db.Classifications.AddRange(
            new Classification { MessageId = "iy1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "iy2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "iy3", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();

        await _service.LoadDataAsync();

        Assert.Equal(0, _service.IndexOfYear(2020));
        Assert.Equal(1, _service.IndexOfYear(2022));
        Assert.Equal(2, _service.IndexOfYear(2024));
        Assert.Equal(-1, _service.IndexOfYear(1999));
    }

    [Fact]
    public void YearFilter_Setter_RebuildsGroups()
    {
        _service.YearFilter = 2024;

        // Just verify the property setter does not throw (depends on loaded data)
        Assert.Equal(2024, _service.YearFilter);
    }

    #endregion
}
