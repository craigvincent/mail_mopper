using MailMopper.Data;
using MailMopper.Models;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Services;

public sealed class ReviewCategoryGroup
{
    public ClassificationCategory Category { get; set; }
    public List<Classification> Classifications { get; set; } = [];
    public ReviewDecision Decision { get; set; }
}

public sealed class ReviewSenderGroup
{
    public string From { get; set; } = "";
    public string Domain { get; set; } = "";
    public List<Classification> Classifications { get; set; } = [];
    public ReviewDecision Decision { get; set; }
}

public sealed class ReviewService(AppDbContext db)
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    private List<Classification> _allReviewable = [];
    private List<ReviewCategoryGroup> _groups = [];
    private List<int> _availableYears = [];
    private const int AutoSaveThreshold = 20;

    public IReadOnlyList<Classification> AllReviewable => _allReviewable;
    public IReadOnlyList<ReviewCategoryGroup> Groups => _groups;
    public IReadOnlyList<int> AvailableYears => _availableYears;

    public int IndexOfYear(int year) => _availableYears.IndexOf(year);
    public int? YearFilter
    {
        get;
        set { field = value; RebuildGroups(); }
    }
    public int PreviouslyTrashedCount { get; private set; }
    public long PreviouslyTrashedSize { get; private set; }
    public bool IsDirty { get; private set; }
    public int UnsavedActions { get; private set; }

    public async Task LoadDataAsync(CancellationToken ct = default)
    {
        if (_groups.Count > 0)
            return;

        var all = await _db.Classifications
            .Include(c => c.Email)
            .Where(c => c.Email != null)
            .ToListAsync(ct);

        var trashedIds = await _db.Actions
            .Where(a => a.Action == "trash")
            .Select(a => a.MessageId)
            .Distinct()
            .ToListAsync(ct);
        var trashedSet = new HashSet<string>(trashedIds);

        var retroFixed = false;
        foreach (var c in all)
        {
            if (c.ReviewDecision != ReviewDecision.Executed && trashedSet.Contains(c.MessageId))
            {
                c.ReviewDecision = ReviewDecision.Executed;
                retroFixed = true;
            }
        }
        if (retroFixed)
            await _db.SaveChangesAsync(ct);

        var executed = all.Where(c => c.ReviewDecision == ReviewDecision.Executed).ToList();
        PreviouslyTrashedCount = executed.Count;
        PreviouslyTrashedSize = executed.Sum(c => c.Email?.SizeEstimate ?? 0);

        _allReviewable = [.. all.Where(c => c.ReviewDecision != ReviewDecision.Executed)];

        _availableYears = [.. _allReviewable
            .Where(c => c.Email?.Date != null)
            .Select(c => c.Email!.Date!.Value.Year)
            .Distinct()
            .OrderBy(y => y)];

        RebuildGroups();
    }

    public void RebuildGroups()
    {
        var filtered = YearFilter.HasValue
            ? [.. _allReviewable.Where(c => c.Email?.Date?.Year == YearFilter.Value)]
            : _allReviewable;

        _groups = [.. filtered
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new ReviewCategoryGroup
            {
                Category = g.Key,
                Classifications = [.. g],
                Decision = ComputeGroupDecision(g)
            })];
    }

    public static ReviewDecision ComputeGroupDecision(IGrouping<ClassificationCategory, Classification> g)
    {
        if (g.All(c => c.ReviewDecision == ReviewDecision.ApproveTrash))
            return ReviewDecision.ApproveTrash;
        if (g.All(c => c.ReviewDecision == ReviewDecision.Keep))
            return ReviewDecision.Keep;
        if (g.All(c => c.ReviewDecision == ReviewDecision.Whitelisted))
            return ReviewDecision.Whitelisted;
        return ReviewDecision.Pending;
    }

    public static List<ReviewSenderGroup> BuildSenderList(ReviewCategoryGroup group)
    {
        return [.. group.Classifications
            .GroupBy(c => new { Domain = c.Email?.FromDomain ?? "unknown", Email = c.Email?.From ?? "unknown" })
            .OrderByDescending(g => g.Count())
            .Select(g => new ReviewSenderGroup
            {
                From = g.Key.Email,
                Domain = g.Key.Domain,
                Classifications = [.. g],
                Decision = ComputeSenderDecision(g)
            })];
    }

    private static ReviewDecision ComputeSenderDecision(IEnumerable<Classification> classifications)
    {
        var list = classifications as IList<Classification> ?? [.. classifications];
        if (list.All(c => c.ReviewDecision == ReviewDecision.ApproveTrash))
            return ReviewDecision.ApproveTrash;
        if (list.All(c => c.ReviewDecision == ReviewDecision.Keep))
            return ReviewDecision.Keep;
        if (list.All(c => c.ReviewDecision == ReviewDecision.Whitelisted))
            return ReviewDecision.Whitelisted;
        return ReviewDecision.Pending;
    }

    public static int FindNextPendingIndex(List<ReviewSenderGroup> senders, int current)
    {
        for (var i = current + 1; i < senders.Count; i++)
        {
            if (senders[i].Decision == ReviewDecision.Pending)
                return i;
        }
        for (var i = 0; i < current; i++)
        {
            if (senders[i].Decision == ReviewDecision.Pending)
                return i;
        }
        return -1;
    }

    public void MarkDirty(int count = 1) { IsDirty = true; UnsavedActions += count; }

    public async Task AutoSaveIfNeededAsync(CancellationToken ct)
    {
        if (UnsavedActions >= AutoSaveThreshold)
        {
            await _db.SaveChangesAsync(ct);
            UnsavedActions = 0;
        }
    }

    public async Task SaveAsync(CancellationToken ct) { await _db.SaveChangesAsync(ct); UnsavedActions = 0; }

    public static void ApplySenderDecision(ReviewSenderGroup sender, ReviewDecision decision)
    {
        foreach (var c in sender.Classifications)
            c.ReviewDecision = decision;
        sender.Decision = decision;
    }

    public void ApplyBulkDecision(ReviewCategoryGroup categoryGroup, ReviewDecision decision)
    {
        var pending = categoryGroup.Classifications.Where(c => c.ReviewDecision == ReviewDecision.Pending).ToList();
        if (pending.Count == 0)
            return;
        foreach (var c in pending)
            c.ReviewDecision = decision;
        MarkDirty(pending.Count);
    }

    public static void UpdateCategoryDecision(ReviewCategoryGroup group)
    {
        group.Decision = group.Classifications switch
        {
            var c when c.All(x => x.ReviewDecision == ReviewDecision.ApproveTrash) => ReviewDecision.ApproveTrash,
            var c when c.All(x => x.ReviewDecision == ReviewDecision.Keep) => ReviewDecision.Keep,
            var c when c.All(x => x.ReviewDecision == ReviewDecision.Whitelisted) => ReviewDecision.Whitelisted,
            _ => ReviewDecision.Pending,
        };
    }

    public async Task WhitelistDomainAsync(string domain, ReviewSenderGroup sender, CancellationToken ct)
    {
        var existing = await _db.Whitelist
            .AnyAsync(w => w.Pattern == domain, ct);
        if (!existing)
        {
            _db.Whitelist.Add(new WhitelistEntry
            {
                Pattern = domain,
                PatternType = "domain",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        ApplySenderDecision(sender, ReviewDecision.Whitelisted);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    public static string FormatDecision(ReviewDecision d) => d switch
    {
        ReviewDecision.ApproveTrash => "[red]Trash[/]",
        ReviewDecision.Keep => "[green]Keep[/]",
        ReviewDecision.Whitelisted => "[cyan]Whitelisted[/]",
        _ => "[yellow]Pending[/]"
    };
}
