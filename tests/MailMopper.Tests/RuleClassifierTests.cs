using MailMopper.Config;
using MailMopper.Models;
using MailMopper.Services;

namespace MailMopper.Tests;

public class RuleClassifierTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RuleClassifier _classifier;

    public RuleClassifierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MAIL_MOPPER_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var rulesSource = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "rules", "default-rules.json");
        rulesSource = Path.GetFullPath(rulesSource);
        var rulesDest = Path.Combine(_tempDir, "default-rules.json");
        File.Copy(rulesSource, rulesDest);

        var settings = new AppSettings
        {
            Classification = new ClassificationSettings { RulesPath = rulesDest }
        };

        _classifier = new RuleClassifier(settings);
        _classifier.LoadRulesAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    private static RuleClassifier CreateClassifierWithRules(string tempDir, string rulesJson)
    {
        var rulesPath = Path.Combine(tempDir, $"rules-{Guid.NewGuid():N}.json");
        File.WriteAllText(rulesPath, rulesJson);

        var settings = new AppSettings
        {
            Classification = new ClassificationSettings { RulesPath = rulesPath }
        };

        var classifier = new RuleClassifier(settings);
        classifier.LoadRulesAsync(CancellationToken.None).GetAwaiter().GetResult();
        return classifier;
    }

    private static EmailRecord MakeEmail(
        string? from = null,
        string? fromDomain = null,
        string? subject = null,
        bool hasListUnsubscribe = false,
        string? gmailCategory = null) => new()
        {
            MessageId = Guid.NewGuid().ToString(),
            From = from ?? "someone@example.com",
            FromDomain = fromDomain ?? "example.com",
            Subject = subject ?? "Hello",
            HasListUnsubscribe = hasListUnsubscribe,
            GmailCategory = gmailCategory ?? "",
            GmailLabels = "",
            Date = DateTimeOffset.UtcNow,
            Snippet = ""
        };

    [Fact]
    public void ClassifiesEmailWithListUnsubscribeHeader()
    {
        var email = MakeEmail(hasListUnsubscribe: true);

        var result = _classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Newsletter, result.Category);
    }

    [Fact]
    public void ClassifiesGmailPromotionsCategory()
    {
        var email = MakeEmail(gmailCategory: "CATEGORY_PROMOTIONS");

        var result = _classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Marketing, result.Category);
    }

    [Fact]
    public void ClassifiesGmailSocialCategory()
    {
        var email = MakeEmail(gmailCategory: "CATEGORY_SOCIAL");

        var result = _classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Social, result.Category);
    }

    [Fact]
    public void ClassifiesKnownMarketingDomain()
    {
        var email = MakeEmail(fromDomain: "mailchimp.com");

        var result = _classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Marketing, result.Category);
    }

    [Fact]
    public void ClassifiesNoreplyPattern()
    {
        var email = MakeEmail(from: "noreply@example.com");

        var result = _classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Automated, result.Category);
    }

    [Fact]
    public void ClassifiesNewsletterSubjectPattern()
    {
        var email = MakeEmail(subject: "Your Weekly Digest for March");

        var result = _classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Newsletter, result.Category);
    }

    [Fact]
    public void DoesNotClassifyPersonalEmail()
    {
        var email = MakeEmail(
            from: "jane.doe@gmail.com",
            fromDomain: "gmail.com",
            subject: "Hey, want to grab lunch?");

        var result = _classifier.Classify(email);

        Assert.Null(result);
    }

    [Fact]
    public void HigherPriorityRuleWins()
    {
        // List-Unsubscribe header rule (priority 10) should beat gmail-category (priority 20)
        var email = MakeEmail(
            hasListUnsubscribe: true,
            gmailCategory: "CATEGORY_PROMOTIONS");

        var result = _classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Newsletter, result.Category);
        Assert.Equal("list-unsubscribe-header", result.RuleName);
    }

    [Fact]
    public void ClassifyAll_ReturnsOnlyMatchedEmails()
    {
        var matched = MakeEmail(hasListUnsubscribe: true);
        var unmatched = MakeEmail(from: "jane@gmail.com", fromDomain: "gmail.com", subject: "Hi");

        var results = _classifier.ClassifyAll([matched, unmatched]);

        Assert.Single(results);
        Assert.Equal(matched.MessageId, results[0].email.MessageId);
        Assert.Equal(ClassificationCategory.Newsletter, results[0].result.Category);
    }

    [Fact]
    public void ClassifyAll_EmptyInput_ReturnsEmpty()
    {
        var results = _classifier.ClassifyAll([]);

        Assert.Empty(results);
    }

    [Fact]
    public void InvalidCategory_FallsBackToUnclassified()
    {
        var rulesJson = """
        {
          "rules": [{
            "name": "bad-category",
            "type": "header",
            "condition": { "header": "List-Unsubscribe", "present": true },
            "category": "NotARealCategory",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail(hasListUnsubscribe: true);

        var result = classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal(ClassificationCategory.Unclassified, result.Category);
    }

    [Fact]
    public void RuleWithNoName_UsesTypeAsRuleName()
    {
        var rulesJson = """
        {
          "rules": [{
            "type": "header",
            "condition": { "header": "List-Unsubscribe", "present": true },
            "category": "Newsletter",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail(hasListUnsubscribe: true);

        var result = classifier.Classify(email);

        Assert.NotNull(result);
        Assert.Equal("header", result.RuleName);
    }

    [Fact]
    public void SenderPattern_NullFrom_DoesNotMatch()
    {
        var rulesJson = """
        {
          "rules": [{
            "name": "sender-test",
            "type": "sender-pattern",
            "condition": { "patterns": ["^noreply@"] },
            "category": "Automated",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail(from: "");

        var result = classifier.Classify(email);

        Assert.Null(result);
    }

    [Fact]
    public void SubjectPattern_NullSubject_DoesNotMatch()
    {
        var rulesJson = """
        {
          "rules": [{
            "name": "subject-test",
            "type": "subject-pattern",
            "condition": { "patterns": [".*digest.*"] },
            "category": "Newsletter",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail(subject: "");

        var result = classifier.Classify(email);

        Assert.Null(result);
    }

    [Fact]
    public void SenderDomain_NullFromDomain_DoesNotMatch()
    {
        var rulesJson = """
        {
          "rules": [{
            "name": "domain-test",
            "type": "sender-domain",
            "condition": { "domains": ["example.com"] },
            "category": "Marketing",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail(fromDomain: "");

        var result = classifier.Classify(email);

        Assert.Null(result);
    }

    [Fact]
    public void GmailCategory_EmptyCategory_DoesNotMatch()
    {
        var rulesJson = """
        {
          "rules": [{
            "name": "category-test",
            "type": "gmail-category",
            "condition": { "category": "CATEGORY_PROMOTIONS" },
            "category": "Marketing",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail(gmailCategory: "");

        var result = classifier.Classify(email);

        Assert.Null(result);
    }

    [Fact]
    public void InvalidRegexPattern_IsSkippedGracefully()
    {
        var rulesJson = """
        {
          "rules": [{
            "name": "bad-regex",
            "type": "sender-pattern",
            "condition": { "patterns": ["[invalid(regex"] },
            "category": "Automated",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail(from: "[invalid(regex");

        var result = classifier.Classify(email);

        // Invalid regex should be skipped, not throw
        Assert.Null(result);
    }

    [Fact]
    public void UnknownRuleType_DoesNotMatch()
    {
        var rulesJson = """
        {
          "rules": [{
            "name": "unknown-type",
            "type": "not-a-real-type",
            "condition": {},
            "category": "Marketing",
            "priority": 1
          }]
        }
        """;
        var classifier = CreateClassifierWithRules(_tempDir, rulesJson);
        var email = MakeEmail();

        var result = classifier.Classify(email);

        Assert.Null(result);
    }

    [Fact]
    public void RuleCount_AfterLoadingDefaultRules_ReturnsPositiveNumber()
    {
        Assert.True(_classifier.RuleCount > 0);
    }
}
