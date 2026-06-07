using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MailMopper.Config;
using MailMopper.Models;

namespace MailMopper.Services;

public record ClassificationResult(
    ClassificationCategory Category,
    string RuleName,
    double Confidence);

public class RuleClassifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string PatternsPropertyName = "patterns";

    private readonly AppSettings _appSettings;
    private List<Rule> _rules = [];
    private readonly Dictionary<string, Regex> _compiledRegexCache = [];

    public int RuleCount => _rules.Count;

    public RuleClassifier(AppSettings appSettings)
    {
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    }

    public async Task LoadRulesAsync(CancellationToken ct)
    {
        var rulesPath = _appSettings.Classification.RulesPath;

        if (!File.Exists(rulesPath))
        {
            throw new FileNotFoundException($"Rules file not found at {rulesPath}");
        }

        var json = await File.ReadAllTextAsync(rulesPath, ct);

        var rulesDocument = JsonSerializer.Deserialize<RulesDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize rules file");

        _rules = rulesDocument.Rules
            .OrderBy(r => r.Priority)
            .ToList();

        // Pre-compile all regex patterns for performance
        CompileRegexPatterns();
    }

    public ClassificationResult? Classify(EmailRecord email)
    {
        var matchedRule = _rules.FirstOrDefault(r => MatchesRule(email, r));
        if (matchedRule == null)
            return null;

        if (!Enum.TryParse<ClassificationCategory>(matchedRule.Category, ignoreCase: true, out var category))
        {
            category = ClassificationCategory.Unclassified;
        }

        return new ClassificationResult(
            Category: category,
            RuleName: matchedRule.Name ?? matchedRule.Type,
            Confidence: 1.0);
    }

    public IReadOnlyList<(EmailRecord email, ClassificationResult result)> ClassifyAll(
        IEnumerable<EmailRecord> emails)
    {
        var results = new List<(EmailRecord, ClassificationResult)>();

        foreach (var email in emails)
        {
            var classification = Classify(email);
            if (classification != null)
            {
                results.Add((email, classification));
            }
        }

        return results.AsReadOnly();
    }

    private bool MatchesRule(EmailRecord email, Rule rule) =>
        rule.Type switch
        {
            "header" => MatchesHeaderRule(email, rule),
            "gmail-category" => MatchesGmailCategoryRule(email, rule),
            "sender-domain" => MatchesSenderDomainRule(email, rule),
            "sender-pattern" => MatchesSenderPatternRule(email, rule),
            "subject-pattern" => MatchesSubjectPatternRule(email, rule),
            _ => false
        };

    private static bool MatchesHeaderRule(EmailRecord email, Rule rule)
    {
        var condition = rule.Condition;
        if (condition.ValueKind == JsonValueKind.Undefined)
            return false;

        if (condition.TryGetProperty("header", out var headerElement) &&
            condition.TryGetProperty("present", out var presentElement))
        {
            var headerName = headerElement.GetString();
            var shouldBePresent = presentElement.GetBoolean();

            if (headerName == "List-Unsubscribe")
            {
                return email.HasListUnsubscribe == shouldBePresent;
            }
        }

        return false;
    }

    private static bool MatchesGmailCategoryRule(EmailRecord email, Rule rule)
    {
        var condition = rule.Condition;
        if (condition.ValueKind == JsonValueKind.Undefined)
            return false;

        if (condition.TryGetProperty("category", out var categoryElement))
        {
            var targetCategory = categoryElement.GetString();
            return !string.IsNullOrEmpty(email.GmailCategory) &&
                   email.GmailCategory.Equals(targetCategory, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool MatchesSenderDomainRule(EmailRecord email, Rule rule)
    {
        var condition = rule.Condition;
        if (condition.ValueKind == JsonValueKind.Undefined)
            return false;

        if (condition.TryGetProperty("domains", out var domainsElement))
        {
            var domains = domainsElement.EnumerateArray()
                .Select(d => d.GetString())
                .Where(d => !string.IsNullOrEmpty(d))
                .ToList();

            var emailDomain = email.FromDomain?.ToLowerInvariant();
            if (string.IsNullOrEmpty(emailDomain))
                return false;

            return domains.Any(d => emailDomain.Equals(d?.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private bool MatchesSenderPatternRule(EmailRecord email, Rule rule)
    {
        var condition = rule.Condition;
        if (condition.ValueKind == JsonValueKind.Undefined)
            return false;

        if (condition.TryGetProperty(PatternsPropertyName, out var patternsElement))
        {
            var patterns = patternsElement.EnumerateArray()
                .Select(p => p.GetString())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            if (string.IsNullOrEmpty(email.From))
                return false;

            return patterns.Any(pattern => MatchesPattern(email.From, pattern!));
        }

        return false;
    }

    private bool MatchesSubjectPatternRule(EmailRecord email, Rule rule)
    {
        var condition = rule.Condition;
        if (condition.ValueKind == JsonValueKind.Undefined)
            return false;

        if (condition.TryGetProperty(PatternsPropertyName, out var patternsElement))
        {
            var patterns = patternsElement.EnumerateArray()
                .Select(p => p.GetString())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            if (string.IsNullOrEmpty(email.Subject))
                return false;

            return patterns.Any(pattern => MatchesPattern(email.Subject, pattern!));
        }

        return false;
    }

    private bool MatchesPattern(string input, string pattern)
    {
        if (!_compiledRegexCache.TryGetValue(pattern, out var regex))
        {
            return false; // Pattern not pre-compiled
        }

        return regex.IsMatch(input);
    }

    private void CompileRegexPatterns()
    {
        _compiledRegexCache.Clear();

        foreach (var rule in _rules)
        {
            foreach (var pattern in ExtractPatternsFromRule(rule)
                .Where(p => !_compiledRegexCache.ContainsKey(p)))
            {
                TryCompilePattern(pattern);
            }
        }
    }

    private static IEnumerable<string> ExtractPatternsFromRule(Rule rule)
    {
        var condition = rule.Condition;
        if (condition.ValueKind == JsonValueKind.Undefined)
            return [];

        if (rule.Type is not ("sender-pattern" or "subject-pattern"))
            return [];

        if (!condition.TryGetProperty(PatternsPropertyName, out var patternsElement))
            return [];

        return patternsElement.EnumerateArray()
            .Select(p => p.GetString())
            .Where(p => !string.IsNullOrEmpty(p))!;
    }

    private void TryCompilePattern(string pattern)
    {
        try
        {
            _compiledRegexCache[pattern] = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // Invalid regex pattern, skip it
        }
    }

    // Internal classes for JSON deserialization
    private sealed class RulesDocument
    {
        [JsonPropertyName("rules")]
        public List<Rule> Rules { get; set; } = [];
    }

    private sealed class Rule
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("condition")]
        public JsonElement Condition { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("priority")]
        public int Priority { get; set; }
    }
}
