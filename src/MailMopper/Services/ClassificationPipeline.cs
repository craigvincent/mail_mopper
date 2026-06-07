using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Services;

/// <summary>
/// Orchestrates the full classification pipeline: rules → ML → human review.
/// </summary>
public class ClassificationPipeline(RuleClassifier ruleClassifier, MlClassifier? mlClassifier, AppDbContext db, AppSettings settings)
{
    private readonly RuleClassifier _ruleClassifier = ruleClassifier;
    private readonly MlClassifier? _mlClassifier = mlClassifier;
    private readonly AppDbContext _db = db;
    private readonly AppSettings _settings = settings;

    /// <summary>
    /// Runs the full classification pipeline for unclassified emails.
    /// </summary>
    public async Task<ClassificationSummary> RunAsync(
        bool skipMl,
        Action<string>? onStatus,
        CancellationToken ct)
    {
        // Load rules
        await _ruleClassifier.LoadRulesAsync(ct);
        onStatus?.Invoke("Rules loaded");

        // Pre-load all classified message IDs to avoid correlated subqueries
        var classifiedIds = new HashSet<string>(
            await _db.Classifications.Select(c => c.MessageId).ToListAsync(ct));

        // Get unclassified emails
        var unclassified = await _db.Emails
            .Where(e => !classifiedIds.Contains(e.MessageId))
            .ToListAsync(ct);

        var totalEmails = unclassified.Count;
        var ruleClassified = 0;
        var aiClassified = 0;

        // Phase 1: Rule classification
        if (unclassified.Count > 0)
        {
            var ruleResults = _ruleClassifier.ClassifyAll(unclassified);
            var classifications = new List<Classification>();

            foreach (var (email, result) in ruleResults)
            {
                var classification = new Classification
                {
                    MessageId = email.MessageId,
                    Category = result.Category,
                    Reason = $"rule:{result.RuleName}",
                    ClassifiedBy = "rule",
                    Confidence = result.Confidence,
                    ReviewDecision = ReviewDecision.Pending,
                    ClassifiedAt = DateTime.UtcNow
                };

                classifications.Add(classification);
                ruleClassified++;

                if (classifications.Count >= (_settings.Ml?.BatchSize ?? 500))
                {
                    _db.Classifications.AddRange(classifications);
                    await _db.SaveChangesAsync(ct);
                    classifications.Clear();
                    onStatus?.Invoke($"Rule-classified {ruleClassified} of {totalEmails} emails");
                }
            }

            if (classifications.Count > 0)
            {
                _db.Classifications.AddRange(classifications);
                await _db.SaveChangesAsync(ct);
            }

            onStatus?.Invoke($"Rule-classified {ruleClassified} of {totalEmails} emails");
        }

        // Phase 1.5: Create Unclassified records for emails that didn't match any rule
        // This ensures ALL emails appear in the review TUI (especially Primary inbox)
        // Refresh classified IDs after Phase 1
        classifiedIds = [.. await _db.Classifications.Select(c => c.MessageId).ToListAsync(ct)];
        var stillUnclassified = await _db.Emails
            .Where(e => !classifiedIds.Contains(e.MessageId))
            .ToListAsync(ct);

        if (stillUnclassified.Count > 0 && (skipMl || _mlClassifier == null))
        {
            var unclassifiedRecords = new List<Classification>();
            foreach (var email in stillUnclassified)
            {
                unclassifiedRecords.Add(new Classification
                {
                    MessageId = email.MessageId,
                    Category = ClassificationCategory.Unclassified,
                    Reason = "no-rule-match",
                    ClassifiedBy = "none",
                    Confidence = 0.0,
                    ReviewDecision = ReviewDecision.Pending,
                    ClassifiedAt = DateTime.UtcNow
                });

                if (unclassifiedRecords.Count >= (_settings.Ml?.BatchSize ?? 500))
                {
                    _db.Classifications.AddRange(unclassifiedRecords);
                    await _db.SaveChangesAsync(ct);
                    unclassifiedRecords.Clear();
                }
            }

            if (unclassifiedRecords.Count > 0)
            {
                _db.Classifications.AddRange(unclassifiedRecords);
                await _db.SaveChangesAsync(ct);
            }

            onStatus?.Invoke($"Tagged {stillUnclassified.Count} unclassified emails (Primary inbox / no rule match)");
        }

        // Phase 2: ML classification (unless skipped or no model)
        if (!skipMl)
        {
            if (_mlClassifier == null)
            {
                onStatus?.Invoke("ML model not available. Run 'mail-mopper train' to train the classifier, or use --skip-ml.");
            }
            else
            {
                // Refresh classified IDs; include Unclassified placeholders as "needing ML"
                var mlClassifiedIds = new HashSet<string>(
                    await _db.Classifications
                        .Where(c => c.Category != ClassificationCategory.Unclassified)
                        .Select(c => c.MessageId)
                        .ToListAsync(ct));
                var remaining = await _db.Emails
                    .Where(e => !mlClassifiedIds.Contains(e.MessageId))
                    .ToListAsync(ct);

                if (remaining.Count > 0)
                {
                    // Remove existing Unclassified placeholder records before ML classifies
                    var remainingIds = new HashSet<string>(remaining.Select(r => r.MessageId));
                    var placeholders = await _db.Classifications
                        .Where(c => c.Category == ClassificationCategory.Unclassified && c.ClassifiedBy == "none")
                        .ToListAsync(ct);
                    var toRemove = placeholders.Where(c => remainingIds.Contains(c.MessageId)).ToList();
                    if (toRemove.Count > 0)
                    {
                        _db.Classifications.RemoveRange(toRemove);
                        await _db.SaveChangesAsync(ct);
                    }

                    onStatus?.Invoke($"Starting ML classification for {remaining.Count} emails");

                    await _mlClassifier.ClassifyAllAsync(remaining,
                        onBatchComplete: async (batchResults, processed, total) =>
                        {
                            var classifications = new List<Classification>();
                            foreach (var result in batchResults)
                            {
                                classifications.Add(new Classification
                                {
                                    MessageId = result.MessageId,
                                    Category = result.Category,
                                    Reason = $"ml:{result.Reasoning}",
                                    ClassifiedBy = "ml",
                                    Confidence = result.Confidence,
                                    ReviewDecision = ReviewDecision.Pending,
                                    ClassifiedAt = DateTime.UtcNow
                                });
                                aiClassified++;
                            }

                            _db.Classifications.AddRange(classifications);
                            await _db.SaveChangesAsync(ct);
                            onStatus?.Invoke($"ML-classified {processed} of {total} emails");
                        }, ct);
                }
            }
        }

        var summary = await BuildSummaryAsync(totalEmails, ruleClassified, aiClassified, ct);
        onStatus?.Invoke("Classification pipeline completed");

        return summary;
    }

    /// <summary>
    /// Builds a summary of the classification results.
    /// </summary>
    private async Task<ClassificationSummary> BuildSummaryAsync(
        int totalEmails,
        int ruleClassified,
        int aiClassified,
        CancellationToken ct)
    {
        var categoryCounts = await _db.Classifications
            .GroupBy(c => c.Category)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Category, x => x.Count, ct);

        var classified = ruleClassified + aiClassified;
        var unclassified = Math.Max(0, totalEmails - classified);

        return new ClassificationSummary(
            TotalEmails: totalEmails,
            RuleClassified: ruleClassified,
            MlClassified: aiClassified,
            Unclassified: unclassified,
            CategoryCounts: categoryCounts);
    }

}

/// <summary>
/// Summary of the classification pipeline results.
/// </summary>
public record ClassificationSummary(
    int TotalEmails,
    int RuleClassified,
    int MlClassified,
    int Unclassified,
    Dictionary<ClassificationCategory, int> CategoryCounts);
