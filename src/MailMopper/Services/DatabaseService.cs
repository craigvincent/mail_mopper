using MailMopper.Data;
using MailMopper.Models;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Services;

/// <summary>
/// Service for common database operations.
/// </summary>
public class DatabaseService(AppDbContext db)
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Ensures the database is created and schema is up to date.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task EnsureCreatedAsync(CancellationToken ct) => await _db.Database.EnsureCreatedAsync(ct);

    /// <summary>
    /// Gets overall email statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Email statistics.</returns>
    public async Task<EmailStats> GetStatsAsync(CancellationToken ct)
    {
        var totalEmails = await _db.Emails.CountAsync(ct);

        var classified = await _db.Classifications
            .Select(c => c.MessageId)
            .Distinct()
            .CountAsync(ct);

        var unclassified = totalEmails - classified;

        var approvedForTrash = await _db.Classifications
            .Where(c => c.ReviewDecision == ReviewDecision.ApproveTrash)
            .CountAsync(ct);

        var trashed = await _db.Actions
            .Where(a => a.Action == "trash")
            .CountAsync(ct);

        var totalSize = await _db.Emails
            .SumAsync(e => e.SizeEstimate, ct);

        return new EmailStats(
            TotalEmails: totalEmails,
            Classified: classified,
            Unclassified: unclassified,
            ApprovedForTrash: approvedForTrash,
            Trashed: trashed,
            TotalSize: totalSize);
    }

    /// <summary>
    /// Gets summary statistics grouped by classification category.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of category summaries.</returns>
    public async Task<IReadOnlyList<CategorySummary>> GetCategorySummaryAsync(CancellationToken ct)
    {
        var summaries = await _db.Classifications
            .Include(c => c.Email)
            .GroupBy(c => c.Category)
            .Select(g => new CategorySummary(
                g.Key,
                g.Count(),
                g.Sum(c => c.Email!.SizeEstimate),
                g.Count(c => c.ReviewDecision == ReviewDecision.ApproveTrash),
                g
                    .GroupBy(c => c.Email!.From)
                    .OrderByDescending(sg => sg.Count())
                    .Select(sg => sg.Key)
                    .FirstOrDefault() ?? "Unknown"))
            .ToListAsync(ct);

        return summaries.AsReadOnly();
    }

    /// <summary>
    /// Gets top senders, optionally filtered by category.
    /// </summary>
    /// <param name="category">Optional category filter.</param>
    /// <param name="count">Maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of sender summaries.</returns>
    public async Task<IReadOnlyList<SenderSummary>> GetTopSendersAsync(
        ClassificationCategory? category,
        int count,
        CancellationToken ct)
    {
        var query = _db.Classifications
            .Include(c => c.Email)
            .AsQueryable();

        if (category.HasValue)
        {
            query = query.Where(c => c.Category == category.Value);
        }

        var grouped = await query
            .GroupBy(c => new { Email = c.Email!.From, Domain = c.Email.FromDomain })
            .Select(g => new
            {
                g.Key.Email,
                g.Key.Domain,
                Count = g.Count(),
                TotalSize = g.Sum(c => c.Email!.SizeEstimate),
                FirstCategory = g.Min(c => c.Category)
            })
            .OrderByDescending(g => g.Count)
            .Take(count)
            .ToListAsync(ct);

        var summaries = grouped
            .Select(g => new SenderSummary(
                Sender: g.Email ?? "Unknown",
                Domain: g.Domain ?? "Unknown",
                Count: g.Count,
                TotalSize: g.TotalSize,
                Category: category ?? g.FirstCategory))
            .ToList();

        return summaries.AsReadOnly();
    }

    /// <summary>
    /// Checks if an email address or domain is whitelisted.
    /// </summary>
    /// <param name="fromDomain">The email domain.</param>
    /// <param name="fromEmail">The full email address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if whitelisted, false otherwise.</returns>
    public async Task<bool> IsWhitelistedAsync(string fromDomain, string fromEmail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromDomain) && string.IsNullOrWhiteSpace(fromEmail))
            return false;

        var isWhitelisted = await _db.Whitelist
            .Where(w =>
                (w.PatternType == "domain" && w.Pattern == fromDomain) ||
                (w.PatternType == "email" && w.Pattern == fromEmail))
            .AnyAsync(ct);

        return isWhitelisted;
    }

    /// <summary>
    /// Adds a pattern to the whitelist.
    /// </summary>
    /// <param name="pattern">The pattern to whitelist (domain or email).</param>
    /// <param name="patternType">Type of pattern: "domain" or "email".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AddWhitelistAsync(string pattern, string patternType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Pattern cannot be empty.", nameof(pattern));

        if (patternType is not "domain" and not "email")
            throw new ArgumentException("Pattern type must be 'domain' or 'email'.", nameof(patternType));

        // Check if already exists
        var exists = await _db.Whitelist
            .Where(w => w.Pattern == pattern && w.PatternType == patternType)
            .AnyAsync(ct);

        if (exists)
            return;

        var whitelist = new WhitelistEntry
        {
            Pattern = pattern,
            PatternType = patternType,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Whitelist.Add(whitelist);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Gets information about all action sessions.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of session information.</returns>
    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct)
    {
        // SQLite cannot aggregate DateTimeOffset (Max/Min), so we do a hybrid:
        // group + count server-side, resolve timestamps from a lightweight projection.
        var groups = await _db.Actions
            .GroupBy(a => new { a.SessionId, a.Action })
            .Select(g => new { g.Key.SessionId, g.Key.Action, Count = g.Count() })
            .ToListAsync(ct);

        // One lightweight query to get per-session latest timestamp (small projection, grouped client-side)
        var timestamps = await _db.Actions
            .Select(a => new { a.SessionId, a.PerformedAt })
            .ToListAsync(ct);
        var latestBySession = timestamps
            .GroupBy(a => a.SessionId)
            .ToDictionary(g => g.Key, g => g.Max(a => a.PerformedAt));

        var sessions = groups
            .Select(g => new SessionInfo(
                SessionId: g.SessionId,
                Count: g.Count,
                Action: g.Action,
                PerformedAt: latestBySession.GetValueOrDefault(g.SessionId, DateTimeOffset.MinValue)))
            .OrderByDescending(s => s.PerformedAt)
            .ToList();

        return sessions.AsReadOnly();
    }
}

/// <summary>
/// Overall email statistics.
/// </summary>
public record EmailStats(
    int TotalEmails,
    int Classified,
    int Unclassified,
    int ApprovedForTrash,
    int Trashed,
    long TotalSize);

/// <summary>
/// Summary statistics for a classification category.
/// </summary>
public record CategorySummary(
    ClassificationCategory Category,
    int Count,
    long TotalSize,
    int ApprovedCount,
    string TopSender);

/// <summary>
/// Summary of a sender's emails.
/// </summary>
public record SenderSummary(
    string Sender,
    string Domain,
    int Count,
    long TotalSize,
    ClassificationCategory? Category);

/// <summary>
/// Information about an action session.
/// </summary>
public record SessionInfo(
    string SessionId,
    int Count,
    string Action,
    DateTimeOffset PerformedAt);
