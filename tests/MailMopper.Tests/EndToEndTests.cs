using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MailMopper.Tests;

/// <summary>
/// End-to-end integration tests that exercise the full pipeline:
/// seeding emails → rule classification → review decisions → trash execution.
/// Uses in-memory SQLite for the database and NSubstitute for Gmail API.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly string _tempDir;
    private readonly AppSettings _settings;

    public EndToEndTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        // Copy rules to temp dir for the classifier
        _tempDir = Path.Combine(Path.GetTempPath(), $"gmail_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var rulesSource = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rules", "default-rules.json");
        rulesSource = Path.GetFullPath(rulesSource);
        var rulesDest = Path.Combine(_tempDir, "default-rules.json");
        File.Copy(rulesSource, rulesDest);

        _settings = new AppSettings
        {
            Classification = new ClassificationSettings { RulesPath = rulesDest },
            Actions = new ActionSettings { TrashBatchSize = 10 }
        };
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    #region Helpers

    private static EmailRecord MakeEmail(
        string id,
        string from = "user@example.com",
        string? fromDomain = null,
        string subject = "Test Subject",
        string snippet = "",
        bool hasListUnsubscribe = false,
        string gmailCategory = "",
        long size = 5000,
        DateTimeOffset? date = null) => new()
        {
            MessageId = id,
            From = from,
            FromDomain = fromDomain ?? from.Split('@').LastOrDefault() ?? "unknown",
            To = "me@gmail.com",
            Subject = subject,
            Snippet = snippet,
            HasListUnsubscribe = hasListUnsubscribe,
            GmailCategory = gmailCategory,
            GmailLabels = "",
            Date = date ?? DateTimeOffset.UtcNow,
            SizeEstimate = size
        };

    #endregion

    // ──────────────────────────────────────────────────────────────────
    // TEST 1: Full classification pipeline (rules only, no ML)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pipeline_ClassifiesEmailsByRules_AndCreatesUnclassifiedRecords()
    {
        // Arrange: emails matching different rules + one personal email
        _db.Emails.AddRange(
            MakeEmail("newsletter1", "digest@company.com", hasListUnsubscribe: true),
            MakeEmail("promo1", "sale@mailchimp.com", "mailchimp.com"),
            MakeEmail("social1", "friend@facebook.com", gmailCategory: "CATEGORY_SOCIAL"),
            MakeEmail("update1", "alerts@bank.com", gmailCategory: "CATEGORY_UPDATES"),
            MakeEmail("forum1", "thread@community.org", gmailCategory: "CATEGORY_FORUMS"),
            MakeEmail("noreply1", "noreply@service.io"),
            MakeEmail("digest1", "team@startup.com", subject: "Your Weekly Digest for Dec"),
            MakeEmail("notif1", "orders@shop.com", subject: "Your order has been shipped"),
            MakeEmail("personal1", "jane@gmail.com", "gmail.com", subject: "Hey, lunch tomorrow?")
        );
        await _db.SaveChangesAsync();

        var ruleClassifier = new RuleClassifier(_settings);
        var pipeline = new ClassificationPipeline(ruleClassifier, mlClassifier: null, _db, _settings);

        // Act
        var summary = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);

        // Assert: all 9 emails should have classification records
        var classifications = await _db.Classifications.ToListAsync();
        Assert.Equal(9, classifications.Count);

        // Rule-matched emails
        Assert.Equal(ClassificationCategory.Newsletter,
            classifications.Single(c => c.MessageId == "newsletter1").Category);
        Assert.Equal(ClassificationCategory.Marketing,
            classifications.Single(c => c.MessageId == "promo1").Category);
        Assert.Equal(ClassificationCategory.Social,
            classifications.Single(c => c.MessageId == "social1").Category);
        Assert.Equal(ClassificationCategory.Notification,
            classifications.Single(c => c.MessageId == "update1").Category);
        Assert.Equal(ClassificationCategory.Forum,
            classifications.Single(c => c.MessageId == "forum1").Category);
        Assert.Equal(ClassificationCategory.Automated,
            classifications.Single(c => c.MessageId == "noreply1").Category);
        Assert.Equal(ClassificationCategory.Newsletter,
            classifications.Single(c => c.MessageId == "digest1").Category);
        Assert.Equal(ClassificationCategory.Notification,
            classifications.Single(c => c.MessageId == "notif1").Category);

        // Personal email should get Unclassified (no rule matched)
        var personalClassification = classifications.Single(c => c.MessageId == "personal1");
        Assert.Equal(ClassificationCategory.Unclassified, personalClassification.Category);
        Assert.Equal("none", personalClassification.ClassifiedBy);

        // Summary numbers
        Assert.Equal(9, summary.TotalEmails);
        Assert.Equal(8, summary.RuleClassified);
        Assert.Equal(0, summary.MlClassified);
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 2: Pipeline is idempotent — running again skips already classified
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pipeline_IsIdempotent_SkipsAlreadyClassified()
    {
        _db.Emails.AddRange(
            MakeEmail("e1", "promo@mailchimp.com", "mailchimp.com"),
            MakeEmail("e2", "jane@gmail.com", "gmail.com", "Hey"));
        await _db.SaveChangesAsync();

        var ruleClassifier = new RuleClassifier(_settings);
        var pipeline = new ClassificationPipeline(ruleClassifier, null, _db, _settings);

        // Run first time
        await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);
        var firstRunCount = await _db.Classifications.CountAsync();
        Assert.Equal(2, firstRunCount);

        // Run second time — no new classifications should be created
        var summary2 = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);
        var secondRunCount = await _db.Classifications.CountAsync();
        Assert.Equal(2, secondRunCount);
        Assert.Equal(0, summary2.TotalEmails); // No unclassified emails to process
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 3: Trash execution with mocked Gmail API
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrashApproved_ExecutesTrashes_MarksAsExecuted()
    {
        // Arrange: 3 emails, 2 approved for trash, 1 kept
        _db.Emails.AddRange(
            MakeEmail("t1", size: 10000),
            MakeEmail("t2", size: 20000),
            MakeEmail("t3", size: 5000));
        _db.Classifications.AddRange(
            new Classification { MessageId = "t1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash },
            new Classification { MessageId = "t2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.ApproveTrash },
            new Classification { MessageId = "t3", Category = ClassificationCategory.Notification, ReviewDecision = ReviewDecision.Keep });
        await _db.SaveChangesAsync();

        // Mock Gmail API — just record calls
        var gmailApi = Substitute.For<IGmailApi>();
        var actionService = new ActionService(gmailApi, _db, _settings);

        // Act
        var result = await actionService.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        // Assert: 2 emails trashed
        Assert.Equal(2, result.EmailsTrashed);
        Assert.Equal(0, result.Errors);
        Assert.Equal(30000, result.EstimatedSpaceFreed);

        // Gmail API was called with the right message IDs
        await gmailApi.Received(1).BatchModifyAsync(
            Arg.Is<IList<string>>(ids => ids != null && ids.Contains("t1") && ids.Contains("t2")),
            Arg.Is<IList<string>?>(labels => labels != null && labels.Contains("TRASH")),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>());

        // Classifications marked as Executed
        var executed = await _db.Classifications
            .Where(c => c.ReviewDecision == ReviewDecision.Executed)
            .ToListAsync();
        Assert.Equal(2, executed.Count);

        // Action records created
        var actionRecords = await _db.Actions.ToListAsync();
        Assert.Equal(2, actionRecords.Count);
        Assert.All(actionRecords, a => Assert.Equal("trash", a.Action));

        // The kept email is untouched
        var keptClassification = await _db.Classifications.SingleAsync(c => c.MessageId == "t3");
        Assert.Equal(ReviewDecision.Keep, keptClassification.ReviewDecision);
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 4: Dry run does not call Gmail API or create action records
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrashApproved_DryRun_DoesNotTrash()
    {
        _db.Emails.Add(MakeEmail("dr1", size: 15000));
        _db.Classifications.Add(
            new Classification { MessageId = "dr1", Category = ClassificationCategory.Spam, ReviewDecision = ReviewDecision.ApproveTrash });
        await _db.SaveChangesAsync();

        var gmailApi = Substitute.For<IGmailApi>();
        var actionService = new ActionService(gmailApi, _db, _settings);

        // Act
        var result = await actionService.TrashApprovedAsync(dryRun: true, progress: null, CancellationToken.None);

        // Assert: counts reported but no side effects
        Assert.Equal(1, result.EmailsTrashed);
        Assert.Equal(15000, result.EstimatedSpaceFreed);

        // Gmail API was NOT called
        await gmailApi.DidNotReceive().BatchModifyAsync(
            Arg.Any<IList<string>>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>());

        // No action records created
        Assert.Empty(await _db.Actions.ToListAsync());
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 5: Undo session restores trashed emails
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UndoSession_UntrashesCorrectEmails()
    {
        // Arrange: simulate prior trash session
        var sessionId = "session-20240101-120000";
        _db.Actions.AddRange(
            new ActionRecord { MessageId = "u1", Action = "trash", SessionId = sessionId, PerformedAt = DateTimeOffset.UtcNow },
            new ActionRecord { MessageId = "u2", Action = "trash", SessionId = sessionId, PerformedAt = DateTimeOffset.UtcNow },
            new ActionRecord { MessageId = "u3", Action = "trash", SessionId = "other-session", PerformedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();

        var gmailApi = Substitute.For<IGmailApi>();
        var actionService = new ActionService(gmailApi, _db, _settings);

        // Act
        var count = await actionService.UndoSessionAsync(sessionId, progress: null, CancellationToken.None);

        // Assert: only the correct session's emails untrashed
        Assert.Equal(2, count);
        await gmailApi.Received(1).BatchModifyAsync(
            Arg.Is<IList<string>>(ids => ids != null && ids.Contains("u1") && ids.Contains("u2") && !ids.Contains("u3")),
            Arg.Any<IList<string>?>(),
            Arg.Is<IList<string>?>(labels => labels != null && labels.Contains("TRASH")),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 6: Full pipeline → review → trash → verify Executed status
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullPipeline_Classify_Approve_Trash_VerifyExecuted()
    {
        // Step 1: Seed emails
        _db.Emails.AddRange(
            MakeEmail("pipe1", "promo@mailchimp.com", "mailchimp.com", "Big Sale"),
            MakeEmail("pipe2", "alerts@noreply.com", subject: "noreply notification"),
            MakeEmail("pipe3", "friend@gmail.com", "gmail.com", "Lunch tomorrow?"));
        await _db.SaveChangesAsync();

        // Step 2: Classify
        var ruleClassifier = new RuleClassifier(_settings);
        var pipeline = new ClassificationPipeline(ruleClassifier, null, _db, _settings);
        await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);

        // Verify classifications exist
        Assert.Equal(3, await _db.Classifications.CountAsync());

        // Step 3: Simulate review decisions
        var marketingClass = await _db.Classifications.SingleAsync(c => c.MessageId == "pipe1");
        marketingClass.ReviewDecision = ReviewDecision.ApproveTrash;
        var automatedClass = await _db.Classifications.SingleAsync(c => c.MessageId == "pipe2");
        automatedClass.ReviewDecision = ReviewDecision.ApproveTrash;
        var personalClass = await _db.Classifications.SingleAsync(c => c.MessageId == "pipe3");
        personalClass.ReviewDecision = ReviewDecision.Keep;
        await _db.SaveChangesAsync();

        // Step 4: Execute trash
        var gmailApi = Substitute.For<IGmailApi>();
        var actionService = new ActionService(gmailApi, _db, _settings);
        var result = await actionService.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        // Step 5: Verify
        Assert.Equal(2, result.EmailsTrashed);

        // Trashed classifications are Executed
        var executedCount = await _db.Classifications
            .CountAsync(c => c.ReviewDecision == ReviewDecision.Executed);
        Assert.Equal(2, executedCount);

        // Personal email is still Keep
        var kept = await _db.Classifications.SingleAsync(c => c.MessageId == "pipe3");
        Assert.Equal(ReviewDecision.Keep, kept.ReviewDecision);

        // Step 6: Re-run pipeline — no new classifications (all already classified)
        var summary2 = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);
        Assert.Equal(0, summary2.TotalEmails);
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 7: Stats reflect actual state after mixed operations
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_ReflectCorrectCounts_AfterMixedOperations()
    {
        // 5 emails: 2 marketing (trashed), 1 newsletter (approved), 1 keep, 1 unclassified
        _db.Emails.AddRange(
            MakeEmail("s1", size: 1000),
            MakeEmail("s2", size: 2000),
            MakeEmail("s3", size: 3000),
            MakeEmail("s4", size: 4000),
            MakeEmail("s5", size: 5000));

        _db.Classifications.AddRange(
            new Classification { MessageId = "s1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Executed },
            new Classification { MessageId = "s2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Executed },
            new Classification { MessageId = "s3", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.ApproveTrash },
            new Classification { MessageId = "s4", Category = ClassificationCategory.Keep, ReviewDecision = ReviewDecision.Keep });

        _db.Actions.AddRange(
            new ActionRecord { MessageId = "s1", Action = "trash", SessionId = "sess1" },
            new ActionRecord { MessageId = "s2", Action = "trash", SessionId = "sess1" });
        await _db.SaveChangesAsync();

        var dbService = new DatabaseService(_db);
        var stats = await dbService.GetStatsAsync(CancellationToken.None);

        Assert.Equal(5, stats.TotalEmails);
        Assert.Equal(4, stats.Classified);
        Assert.Equal(1, stats.Unclassified);
        Assert.Equal(1, stats.ApprovedForTrash); // only s3 is ApproveTrash (not Executed)
        Assert.Equal(2, stats.Trashed);
        Assert.Equal(15000, stats.TotalSize);
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 8: Gmail API failure doesn't lose data — partial batch handling
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrashApproved_GmailApiFailure_CountsAsErrors()
    {
        // 2 emails in separate batches (batch size = 1 for this test)
        var smallBatchSettings = new AppSettings { Actions = new ActionSettings { TrashBatchSize = 1 } };

        _db.Emails.AddRange(
            MakeEmail("fail1", size: 1000),
            MakeEmail("fail2", size: 2000));
        _db.Classifications.AddRange(
            new Classification { MessageId = "fail1", Category = ClassificationCategory.Spam, ReviewDecision = ReviewDecision.ApproveTrash },
            new Classification { MessageId = "fail2", Category = ClassificationCategory.Spam, ReviewDecision = ReviewDecision.ApproveTrash });
        await _db.SaveChangesAsync();

        // First batch succeeds, second fails
        var gmailApi = Substitute.For<IGmailApi>();
        var callCount = 0;
        gmailApi.BatchModifyAsync(
            Arg.Any<IList<string>>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<IList<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                return callCount == 2 ? throw new InvalidOperationException("API rate limit exceeded") : Task.CompletedTask;
            });

        var actionService = new ActionService(gmailApi, _db, smallBatchSettings);
        var result = await actionService.TrashApprovedAsync(dryRun: false, progress: null, CancellationToken.None);

        // One succeeded, one failed
        Assert.Equal(1, result.EmailsTrashed);
        Assert.Equal(1, result.Errors);

        // Only 1 action record (the successful one)
        Assert.Single(await _db.Actions.ToListAsync());
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 9: Whitelist prevents classification review
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Whitelist_IsCheckedCorrectly()
    {
        var dbService = new DatabaseService(_db);

        // Add domain and email whitelist entries
        await dbService.AddWhitelistAsync("important.com", "domain", CancellationToken.None);
        await dbService.AddWhitelistAsync("vip@other.com", "email", CancellationToken.None);

        // Domain match
        Assert.True(await dbService.IsWhitelistedAsync("important.com", "user@important.com", CancellationToken.None));
        // Email match
        Assert.True(await dbService.IsWhitelistedAsync("other.com", "vip@other.com", CancellationToken.None));
        // No match
        Assert.False(await dbService.IsWhitelistedAsync("random.com", "user@random.com", CancellationToken.None));

        // Duplicate add is a no-op
        await dbService.AddWhitelistAsync("important.com", "domain", CancellationToken.None);
        var count = await _db.Whitelist.CountAsync();
        Assert.Equal(2, count);
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 10: Year-based filtering for review data
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Emails_GroupCorrectlyByYear()
    {
        // Emails from different years
        _db.Emails.AddRange(
            MakeEmail("y1", "old@ex.com", date: new DateTimeOffset(2020, 3, 15, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("y2", "old@ex.com", date: new DateTimeOffset(2020, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("y3", "recent@ex.com", date: new DateTimeOffset(2024, 1, 10, 0, 0, 0, TimeSpan.Zero)),
            MakeEmail("y4", "recent@ex.com", date: new DateTimeOffset(2024, 6, 20, 0, 0, 0, TimeSpan.Zero)));
        _db.Classifications.AddRange(
            new Classification { MessageId = "y1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "y2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "y3", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "y4", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending });
        await _db.SaveChangesAsync();

        // Query year breakdown (same logic ReviewApp uses — materialize then group in memory)
        var pendingWithEmail = await _db.Classifications
            .Include(c => c.Email)
            .Where(c => c.ReviewDecision == ReviewDecision.Pending)
            .ToListAsync();

        var yearBreakdown = pendingWithEmail
            .GroupBy(c => c.Email!.Date!.Value.Year)
            .Select(g => new { Year = g.Key, Count = g.Count() })
            .OrderBy(g => g.Year)
            .ToList();

        Assert.Equal(2, yearBreakdown.Count);
        Assert.Equal(2020, yearBreakdown[0].Year);
        Assert.Equal(2, yearBreakdown[0].Count);
        Assert.Equal(2024, yearBreakdown[1].Year);
        Assert.Equal(2, yearBreakdown[1].Count);

        // Filter to just 2020 (client-side since SQLite can't translate Date.Year)
        var oldEmails = pendingWithEmail
            .Where(c => c.Email!.Date!.Value.Year == 2020)
            .ToList();
        Assert.Equal(2, oldEmails.Count);
        Assert.All(oldEmails, c => Assert.Equal(2020, c.Email!.Date!.Value.Year));
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 11: Executed emails are excluded from review queries
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecutedEmails_AreExcludedFromReview()
    {
        _db.Emails.AddRange(
            MakeEmail("ex1"),
            MakeEmail("ex2"),
            MakeEmail("ex3"));
        _db.Classifications.AddRange(
            new Classification { MessageId = "ex1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Executed },
            new Classification { MessageId = "ex2", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Pending },
            new Classification { MessageId = "ex3", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.Keep });
        await _db.SaveChangesAsync();

        // Simulate review query: only Pending should show
        var reviewable = await _db.Classifications
            .Where(c => c.ReviewDecision != ReviewDecision.Executed)
            .ToListAsync();
        Assert.Equal(2, reviewable.Count);
        Assert.DoesNotContain(reviewable, c => c.MessageId == "ex1");
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 12: Retroactive Executed fix (emails trashed before Executed enum)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetroactiveExecutedFix_MarksOldTrashedAsExecuted()
    {
        // Simulate emails that were trashed before the Executed status existed:
        // Classification still shows ApproveTrash, but ActionRecord has "trash" record
        _db.Emails.AddRange(MakeEmail("retro1"), MakeEmail("retro2"), MakeEmail("retro3"));
        _db.Classifications.AddRange(
            new Classification { MessageId = "retro1", Category = ClassificationCategory.Marketing, ReviewDecision = ReviewDecision.ApproveTrash },
            new Classification { MessageId = "retro2", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.ApproveTrash },
            new Classification { MessageId = "retro3", Category = ClassificationCategory.Newsletter, ReviewDecision = ReviewDecision.Pending });
        _db.Actions.AddRange(
            new ActionRecord { MessageId = "retro1", Action = "trash", SessionId = "old-session" },
            new ActionRecord { MessageId = "retro2", Action = "trash", SessionId = "old-session" });
        await _db.SaveChangesAsync();

        // Apply the same retroactive fix that ReviewApp.LoadDataAsync uses
        var trashedMessageIds = new HashSet<string>(
            await _db.Actions
                .Where(a => a.Action == "trash")
                .Select(a => a.MessageId)
                .Distinct()
                .ToListAsync());

        var staleApproved = await _db.Classifications
            .Where(c => c.ReviewDecision == ReviewDecision.ApproveTrash)
            .ToListAsync();

        foreach (var c in staleApproved)
        {
            if (trashedMessageIds.Contains(c.MessageId))
                c.ReviewDecision = ReviewDecision.Executed;
        }
        await _db.SaveChangesAsync();

        // Verify: retro1 and retro2 are now Executed, retro3 is still Pending
        var classifications = await _db.Classifications.ToListAsync();
        Assert.Equal(ReviewDecision.Executed, classifications.Single(c => c.MessageId == "retro1").ReviewDecision);
        Assert.Equal(ReviewDecision.Executed, classifications.Single(c => c.MessageId == "retro2").ReviewDecision);
        Assert.Equal(ReviewDecision.Pending, classifications.Single(c => c.MessageId == "retro3").ReviewDecision);
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 13: Category summary includes correct top sender
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CategorySummary_ReportsCorrectTopSender()
    {
        _db.Emails.AddRange(
            MakeEmail("cs1", "frequent@spam.com", "spam.com", size: 100),
            MakeEmail("cs2", "frequent@spam.com", "spam.com", size: 200),
            MakeEmail("cs3", "frequent@spam.com", "spam.com", size: 300),
            MakeEmail("cs4", "rare@spam.com", "spam.com", size: 500));
        _db.Classifications.AddRange(
            new Classification { MessageId = "cs1", Category = ClassificationCategory.Spam },
            new Classification { MessageId = "cs2", Category = ClassificationCategory.Spam },
            new Classification { MessageId = "cs3", Category = ClassificationCategory.Spam },
            new Classification { MessageId = "cs4", Category = ClassificationCategory.Spam });
        await _db.SaveChangesAsync();

        var dbService = new DatabaseService(_db);
        var summaries = await dbService.GetCategorySummaryAsync(CancellationToken.None);

        var spam = summaries.Single(s => s.Category == ClassificationCategory.Spam);
        Assert.Equal(4, spam.Count);
        Assert.Equal(1100, spam.TotalSize);
        Assert.Equal("frequent@spam.com", spam.TopSender);
    }

    // ──────────────────────────────────────────────────────────────────
    // TEST 14: Session info tracks trash/untrash actions
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sessions_TrackTrashAndUntrashSeparately()
    {
        _db.Actions.AddRange(
            new ActionRecord { MessageId = "a1", Action = "trash", SessionId = "sess-1", PerformedAt = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero) },
            new ActionRecord { MessageId = "a2", Action = "trash", SessionId = "sess-1", PerformedAt = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero) },
            new ActionRecord { MessageId = "a1", Action = "untrash", SessionId = "sess-1", PerformedAt = new DateTimeOffset(2024, 1, 2, 12, 0, 0, TimeSpan.Zero) });
        await _db.SaveChangesAsync();

        var dbService = new DatabaseService(_db);
        var sessions = await dbService.GetSessionsAsync(CancellationToken.None);

        Assert.Equal(2, sessions.Count); // trash group + untrash group
        var trashSession = sessions.Single(s => s.Action == "trash");
        Assert.Equal(2, trashSession.Count);
        var untrashSession = sessions.Single(s => s.Action == "untrash");
        Assert.Equal(1, untrashSession.Count);
    }
}
