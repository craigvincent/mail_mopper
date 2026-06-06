using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Models;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Services;

/// <summary>
/// Service for executing trash/untrash actions on Gmail emails.
/// </summary>
public class ActionService(IGmailApi gmail, AppDbContext db, AppSettings settings)
{
    private readonly IGmailApi _gmail = gmail ?? throw new ArgumentNullException(nameof(gmail));
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly AppSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// Executes trash action on emails classified as approved for trash.
    /// </summary>
    /// <param name="dryRun">If true, counts emails without actually trashing them.</param>
    /// <param name="progress">Optional progress reporter (processed, total).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Summary of actions performed.</returns>
    public async Task<ActionSummary> TrashApprovedAsync(
        bool dryRun,
        IProgress<(int processed, int total)>? progress,
        CancellationToken ct)
    {
        var sessionId = $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var batchSize = _settings.Actions.TrashBatchSize;

        // Query approved classifications (exclude already executed)
        var approvedEmails = await _db.Classifications
            .Where(c => c.ReviewDecision == ReviewDecision.ApproveTrash)
            .Include(c => c.Email)
            .Select(c => new
            {
                ClassificationId = c.Id,
                c.Email!.MessageId,
                c.Reason,
                c.Email.SizeEstimate
            })
            .ToListAsync(ct);

        var totalCount = approvedEmails.Count;
        var processedCount = 0;
        var errorCount = 0;
        var spaceSaved = 0L;

        // If dry run, just return counts
        if (dryRun)
        {
            spaceSaved = approvedEmails.Sum(e => e.SizeEstimate);
            return new ActionSummary(
                SessionId: sessionId,
                EmailsTrashed: totalCount,
                Errors: 0,
                EstimatedSpaceFreed: spaceSaved);
        }

        // Process emails in batches
        var batches = approvedEmails
            .Chunk(batchSize)
            .ToList();

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            var messageIds = batch.Select(e => e.MessageId).ToList();

            try
            {
                // Batch modify to add TRASH label
                await _gmail.BatchModifyAsync(messageIds, addLabelIds: ["TRASH"], removeLabelIds: null, ct);

                // Create action records for successfully trashed emails
                var actionRecords = batch.Select(e => new ActionRecord
                {
                    SessionId = sessionId,
                    Action = "trash",
                    MessageId = e.MessageId,
                    Reason = e.Reason,
                    PerformedAt = DateTimeOffset.UtcNow
                }).ToList();

                _db.Actions.AddRange(actionRecords);
                spaceSaved += batch.Sum(e => e.SizeEstimate);
                processedCount += batch.Length;

                // Mark classifications as Executed so they don't accumulate
                var executedIds = batch.Select(e => e.ClassificationId).ToList();
                var executedClassifications = await _db.Classifications
                    .Where(c => executedIds.Contains(c.Id))
                    .ToListAsync(ct);
                foreach (var c in executedClassifications)
                    c.ReviewDecision = ReviewDecision.Executed;
            }
            catch (Exception ex)
            {
                errorCount += batch.Length;
                Console.Error.WriteLine($"Error trashing batch: {ex.Message}");
            }

            progress?.Report((processedCount, totalCount));
        }

        await _db.SaveChangesAsync(ct);

        return new ActionSummary(
            SessionId: sessionId,
            EmailsTrashed: totalCount - errorCount,
            Errors: errorCount,
            EstimatedSpaceFreed: spaceSaved);
    }

    /// <summary>
    /// Reverts trash action for a specific session.
    /// </summary>
    /// <param name="sessionId">The session ID to undo.</param>
    /// <param name="progress">Optional progress reporter (processed, total).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Count of emails untrashed.</returns>
    public async Task<int> UndoSessionAsync(
        string sessionId,
        IProgress<(int processed, int total)>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        var batchSize = _settings.Actions.TrashBatchSize;

        // Query action records for this session
        var trashedEmails = await _db.Actions
            .Where(a => a.SessionId == sessionId && a.Action == "trash")
            .Select(a => a.MessageId)
            .ToListAsync(ct);

        var totalCount = trashedEmails.Count;
        var processedCount = 0;

        // Process emails in batches
        var batches = trashedEmails
            .Chunk(batchSize)
            .ToList();

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            var batchIds = batch.ToList();

            try
            {
                // Batch modify to remove TRASH label
                await _gmail.BatchModifyAsync(batchIds, addLabelIds: null, removeLabelIds: ["TRASH"], ct);

                // Create untrash action records
                var undoRecords = batchIds.Select(id => new ActionRecord
                {
                    SessionId = sessionId,
                    Action = "untrash",
                    MessageId = id,
                    Reason = $"Undo of previous trash action",
                    PerformedAt = DateTimeOffset.UtcNow
                }).ToList();

                _db.Actions.AddRange(undoRecords);
                processedCount += batch.Length;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error untrashing batch: {ex.Message}");
            }

            progress?.Report((processedCount, totalCount));
        }

        await _db.SaveChangesAsync(ct);
        return processedCount;
    }
}

/// <summary>
/// Summary of actions performed in a trash session.
/// </summary>
public record ActionSummary(
    string SessionId,
    int EmailsTrashed,
    int Errors,
    long EstimatedSpaceFreed);
