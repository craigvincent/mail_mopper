using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Tests.Services;

public class ClassificationPipelineTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly RuleClassifier _ruleClassifier;

    public ClassificationPipelineTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _tempDir = Path.Combine(Path.GetTempPath(), $"pipeline_test_{Guid.NewGuid():N}");
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

        _ruleClassifier = new RuleClassifier(_settings);
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
    // skipMl: false with null mlClassifier
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SkipMlFalseWithNullMlClassifier_CreatesUnclassifiedAndEmitsMlNotAvailable()
    {
        _db.Emails.AddRange(
            MakeEmail("nl1", hasListUnsubscribe: true),
            MakeEmail("personal1", "jane@gmail.com", "gmail.com", subject: "Lunch?"));
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);
        var statuses = new List<string>();
        void onStatus(string s) => statuses.Add(s);

        var summary = await pipeline.RunAsync(skipMl: false, onStatus: onStatus, CancellationToken.None);

        // Unclassified record should be created (Phase 1.5 fires when mlClassifier == null)
        var classifications = await _db.Classifications.ToListAsync();
        Assert.Equal(2, classifications.Count);

        var personal = classifications.Single(c => c.MessageId == "personal1");
        Assert.Equal(ClassificationCategory.Unclassified, personal.Category);
        Assert.Equal("none", personal.ClassifiedBy);

        // ML not available status emitted
        Assert.Contains(statuses, s => s.Contains("ML model not available"));

        // Summary reflects 1 rule-classified, 0 ML
        Assert.Equal(2, summary.TotalEmails);
        Assert.Equal(1, summary.RuleClassified);
        Assert.Equal(0, summary.MlClassified);
        Assert.Equal(1, summary.Unclassified);
    }

    // ──────────────────────────────────────────────────────────────────
    // onStatus callbacks are invoked with expected messages
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_OnStatusCallbacksInvoked_CollectsExpectedMessages()
    {
        _db.Emails.AddRange(
            MakeEmail("s1", hasListUnsubscribe: true),
            MakeEmail("s2", "person@gmail.com", "gmail.com", subject: "Hi"));
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);
        var statuses = new List<string>();
        void onStatus(string s) => statuses.Add(s);

        await pipeline.RunAsync(skipMl: true, onStatus: onStatus, CancellationToken.None);

        Assert.Contains(statuses, s => s == "Rules loaded");
        Assert.Contains(statuses, s => s.StartsWith("Rule-classified", StringComparison.Ordinal));
        Assert.Contains(statuses, s => s.Contains("unclassified emails"));
        Assert.Contains(statuses, s => s == "Classification pipeline completed");
    }

    // ──────────────────────────────────────────────────────────────────
    // All emails match rules → no unclassified records
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AllEmailsMatchRules_NoUnclassifiedRecords()
    {
        _db.Emails.AddRange(
            MakeEmail("r1", hasListUnsubscribe: true),
            MakeEmail("r2", "sale@mailchimp.com", "mailchimp.com"),
            MakeEmail("r3", gmailCategory: "CATEGORY_SOCIAL"));
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);

        var summary = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);

        var classifications = await _db.Classifications.ToListAsync();
        Assert.Equal(3, classifications.Count);
        Assert.DoesNotContain(classifications, c => c.Category == ClassificationCategory.Unclassified);

        Assert.Equal(3, summary.TotalEmails);
        Assert.Equal(3, summary.RuleClassified);
        Assert.Equal(0, summary.Unclassified);
    }

    // ──────────────────────────────────────────────────────────────────
    // No emails to classify → summary all zeros
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoEmails_SummaryShowsZeros()
    {
        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);

        var summary = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);

        Assert.Equal(0, summary.TotalEmails);
        Assert.Equal(0, summary.RuleClassified);
        Assert.Equal(0, summary.MlClassified);
        Assert.Equal(0, summary.Unclassified);
        Assert.Empty(summary.CategoryCounts);
        Assert.Empty(await _db.Classifications.ToListAsync());
    }

    // ──────────────────────────────────────────────────────────────────
    // CategoryCounts in summary has correct entries
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CategoryCounts_ReflectsClassificationDistribution()
    {
        _db.Emails.AddRange(
            MakeEmail("c1", hasListUnsubscribe: true),
            MakeEmail("c2", hasListUnsubscribe: true),
            MakeEmail("c3", gmailCategory: "CATEGORY_SOCIAL"),
            MakeEmail("c4", "noreply@service.io"),
            MakeEmail("c5", "friend@gmail.com", "gmail.com", subject: "Hey"));
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);

        var summary = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);

        // Verify category counts dictionary
        Assert.True(summary.CategoryCounts.ContainsKey(ClassificationCategory.Newsletter));
        Assert.Equal(2, summary.CategoryCounts[ClassificationCategory.Newsletter]);
        Assert.True(summary.CategoryCounts.ContainsKey(ClassificationCategory.Social));
        Assert.Equal(1, summary.CategoryCounts[ClassificationCategory.Social]);
        Assert.True(summary.CategoryCounts.ContainsKey(ClassificationCategory.Automated));
        Assert.Equal(1, summary.CategoryCounts[ClassificationCategory.Automated]);
        Assert.True(summary.CategoryCounts.ContainsKey(ClassificationCategory.Unclassified));
        Assert.Equal(1, summary.CategoryCounts[ClassificationCategory.Unclassified]);
    }

    // ──────────────────────────────────────────────────────────────────
    // Phase 1.5 with no stillUnclassified — no extra records created
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AllRuleMatched_Phase15CreatesNoRecords()
    {
        _db.Emails.AddRange(
            MakeEmail("p1", hasListUnsubscribe: true),
            MakeEmail("p2", gmailCategory: "CATEGORY_FORUMS"));
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);
        var statuses = new List<string>();
        void onStatus(string s) => statuses.Add(s);

        await pipeline.RunAsync(skipMl: true, onStatus: onStatus, CancellationToken.None);

        var classifications = await _db.Classifications.ToListAsync();
        Assert.Equal(2, classifications.Count);
        Assert.All(classifications, c => Assert.NotEqual(ClassificationCategory.Unclassified, c.Category));

        // No "unclassified emails" status message since Phase 1.5 was skipped
        Assert.DoesNotContain(statuses, s => s.Contains("unclassified emails"));
    }

    // ──────────────────────────────────────────────────────────────────
    // No status callback (null onStatus) — runs without error
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_NullOnStatus_CompletesWithoutError()
    {
        _db.Emails.Add(MakeEmail("n1", hasListUnsubscribe: true));
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);

        var summary = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);

        Assert.Equal(1, summary.TotalEmails);
        Assert.Equal(1, summary.RuleClassified);
    }

    // ──────────────────────────────────────────────────────────────────
    // Batch flushing for >500 rule classifications
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MoreThan500RuleMatches_BatchFlushesCorrectly()
    {
        // Seed 510 emails that all match the list-unsubscribe rule
        var emails = Enumerable.Range(1, 510)
            .Select(i => MakeEmail($"batch-r-{i}", hasListUnsubscribe: true))
            .ToList();
        _db.Emails.AddRange(emails);
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);
        var statuses = new List<string>();

        var summary = await pipeline.RunAsync(skipMl: true, onStatus: s => statuses.Add(s), CancellationToken.None);

        Assert.Equal(510, summary.RuleClassified);
        Assert.Equal(510, await _db.Classifications.CountAsync());

        // The intermediate batch status should have been emitted at the 500 mark
        Assert.Contains(statuses, s => s.Contains("Rule-classified 500 of 510"));
    }

    // ──────────────────────────────────────────────────────────────────
    // Batch flushing for >500 unclassified records
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MoreThan500Unclassified_BatchFlushesCorrectly()
    {
        // Seed 510 emails that won't match any rule (personal-style)
        var emails = Enumerable.Range(1, 510)
            .Select(i => MakeEmail($"batch-u-{i}", $"person{i}@gmail.com", "gmail.com", $"Hey {i}"))
            .ToList();
        _db.Emails.AddRange(emails);
        await _db.SaveChangesAsync();

        var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier: null, _db, _settings);

        var summary = await pipeline.RunAsync(skipMl: true, onStatus: null, CancellationToken.None);

        // All 510 should end up as Unclassified
        var unclassifiedCount = await _db.Classifications
            .CountAsync(c => c.Category == ClassificationCategory.Unclassified);
        Assert.Equal(510, unclassifiedCount);
        Assert.Equal(510, summary.TotalEmails);
    }
}
