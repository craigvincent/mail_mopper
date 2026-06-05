using System.Globalization;
using MailMopper.Data;
using MailMopper.Models;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public sealed class ReviewView : IAppView
{
    private readonly AppDbContext _db;

    private enum SubView { Dashboard, Category, Sender, YearSelect }
    private SubView _subView = SubView.Dashboard;

    private List<Classification> _allReviewable = [];
    private List<ReviewCategoryGroup> _groups = [];
    private bool _dirty;
    private int _unsavedActions;
    private int _previouslyTrashedCount;
    private long _previouslyTrashedSize;
    private int? _yearFilter;
    private List<int> _availableYears = [];

    private int _selectedCategory = -1;
    private int _categoryPage;
    private bool _hidingDecided = true;
    private int _lastSelectedSenderIdx = -1;
    private List<ReviewSenderGroup> _categorySenders = [];
    private ReviewCategoryGroup? _activeCategoryGroup;

    private ReviewSenderGroup? _activeSender;
    private int _senderPage;
    private List<Classification> _senderEmails = [];

    private char? _pendingCatCmd;
    private int _yearSelectIndex;
    private string _yearInputDigits = "";

    private const int PageSize = 30;
    private const int SenderPageSize = 25;
    private const int AutoSaveThreshold = 20;

    public Action? RequestRender { get; set; }
    public Action? RequestRenderImmediate { get; set; }

    public ReviewView(AppDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public IRenderable GetContent(int availableHeight)
    {
        _ = LoadDataIfNeeded();

        return _subView switch
        {
            SubView.Dashboard => BuildDashboard(),
            SubView.Category => BuildCategory(),
            SubView.Sender => BuildSender(),
            SubView.YearSelect => BuildYearSelect(),
            _ => new Markup("")
        };
    }

    public string GetFooterHints()
    {
        return _subView switch
        {
            SubView.Dashboard => "#: Open  Y: Year  S: Save  Esc/Del: Back",
            SubView.Category when _pendingCatCmd == 'T' => "#: Trash sender  A: Trash All  Esc: Cancel",
            SubView.Category when _pendingCatCmd == 'K' => "#: Keep sender  A: Keep All  Esc: Cancel",
            SubView.Category => "#: View  T: Trash  K: Keep  H: Toggle  N/P: Page  Y: Year  Esc/B: Back",
            SubView.Sender => "T: Trash All  K: Keep All  W: Whitelist  ←/→: Page  Esc/B: Back",
            SubView.YearSelect => "↑↓: Move  Enter: Select  Esc/B: Cancel",
            _ => ""
        };
    }

    public async Task<ViewCommand> HandleInputAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_subView == SubView.YearSelect)
        {
            await HandleYearSelectInput(key, ct);
            return ViewCommand.None;
        }

        switch (_subView)
        {
            case SubView.Dashboard:
                await HandleDashboardInput(key, ct);
                break;
            case SubView.Category:
                await HandleCategoryInput(key, ct);
                break;
            case SubView.Sender:
                await HandleSenderInput(key, ct);
                break;
        }

        if (_dirty)
        {
            await AutoSaveIfNeeded(ct);
            return ViewCommand.MarkDirty;
        }

        return ViewCommand.None;
    }

    private async Task LoadDataIfNeeded()
    {
        if (_groups.Count > 0)
            return;

        try
        {
            var all = await _db.Classifications
                .Include(c => c.Email)
                .Where(c => c.Email != null)
                .ToListAsync();

            var trashedIds = await _db.Actions
                .Where(a => a.Action == "trash")
                .Select(a => a.MessageId)
                .Distinct()
                .ToListAsync();
            var trashedSet = new HashSet<string>(trashedIds);

            bool retroFixed = false;
            foreach (var c in all)
            {
                if (c.ReviewDecision != ReviewDecision.Executed && trashedSet.Contains(c.MessageId))
                {
                    c.ReviewDecision = ReviewDecision.Executed;
                    retroFixed = true;
                }
            }
            if (retroFixed)
                await _db.SaveChangesAsync();

            var executed = all.Where(c => c.ReviewDecision == ReviewDecision.Executed).ToList();
            _previouslyTrashedCount = executed.Count;
            _previouslyTrashedSize = executed.Sum(c => c.Email?.SizeEstimate ?? 0);

            _allReviewable = all
                .Where(c => c.ReviewDecision != ReviewDecision.Executed)
                .ToList();

            _availableYears = _allReviewable
                .Where(c => c.Email?.Date != null)
                .Select(c => c.Email!.Date!.Value.Year)
                .Distinct()
                .OrderBy(y => y)
                .ToList();

            RebuildGroups();
        }
        catch
        {
        }
    }

    private void RebuildGroups()
    {
        var filtered = _yearFilter.HasValue
            ? _allReviewable.Where(c => c.Email?.Date?.Year == _yearFilter.Value).ToList()
            : _allReviewable;

        _groups = filtered
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new ReviewCategoryGroup
            {
                Category = g.Key,
                Classifications = g.ToList(),
                Decision = ComputeGroupDecision(g)
            })
            .ToList();
    }

    private static ReviewDecision ComputeGroupDecision(IGrouping<ClassificationCategory, Classification> g)
    {
        if (g.All(c => c.ReviewDecision == ReviewDecision.ApproveTrash))
            return ReviewDecision.ApproveTrash;
        if (g.All(c => c.ReviewDecision == ReviewDecision.Keep))
            return ReviewDecision.Keep;
        if (g.All(c => c.ReviewDecision == ReviewDecision.Whitelisted))
            return ReviewDecision.Whitelisted;
        return ReviewDecision.Pending;
    }

    private static string FormatDecision(ReviewDecision d) => d switch
    {
        ReviewDecision.ApproveTrash => "[red]Trash[/]",
        ReviewDecision.Keep => "[green]Keep[/]",
        ReviewDecision.Whitelisted => "[cyan]Whitelisted[/]",
        _ => "[yellow]Pending[/]"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    private void MarkDirty(int count = 1) { _dirty = true; _unsavedActions += count; RequestRender?.Invoke(); }

    private async Task AutoSaveIfNeeded(CancellationToken ct)
    {
        if (_unsavedActions >= AutoSaveThreshold)
        {
            await _db.SaveChangesAsync(ct);
            _unsavedActions = 0;
        }
    }

    private async Task SaveAsync(CancellationToken ct) { await _db.SaveChangesAsync(ct); _unsavedActions = 0; }

    // ── Dashboard ──────────────────────────────────────────────────

    private IRenderable BuildDashboard()
    {
        var parts = new List<IRenderable>();

        var yearLabel = _yearFilter.HasValue ? $"[bold cyan]{_yearFilter.Value}[/]" : "[dim]All years[/]";
        var headerLine = $"[bold blue]Review — Categories[/]  Year: {yearLabel}";
        parts.Add(new Rule(headerLine).LeftJustified());

        if (_availableYears.Count > 1)
            parts.Add(BuildYearBreakdown());

        parts.Add(BuildCategoryTable());

        var totalRemaining = _groups.Sum(g => g.Classifications.Count);
        var totalSize = _groups.Sum(g => g.Classifications.Sum(c => c.Email?.SizeEstimate ?? 0));
        var pending = _groups.Sum(g => g.Classifications.Count(c => c.ReviewDecision == ReviewDecision.Pending));
        var trash = _groups.Sum(g => g.Classifications.Count(c => c.ReviewDecision == ReviewDecision.ApproveTrash));
        var keep = _groups.Sum(g => g.Classifications.Count(c => c.ReviewDecision == ReviewDecision.Keep));

        parts.Add(new Markup($"  [bold]Total:[/] {totalRemaining:N0} emails ({FormatSize(totalSize)})  │  [yellow]Pending: {pending:N0}[/]  [red]Trash: {trash:N0}[/]  [green]Keep: {keep:N0}[/]"));
        if (_previouslyTrashedCount > 0)
            parts.Add(new Markup($"  [dim]Already executed: {_previouslyTrashedCount:N0} ({FormatSize(_previouslyTrashedSize)})[/]"));
        if (_dirty)
            parts.Add(new Markup($"  [dim italic]Unsaved: {_unsavedActions} actions[/]"));

        return new Rows(parts);
    }

    private static readonly string[] _emailsHeader = ["[bold]Emails[/]"];
    private static readonly string[] _sizeHeader = ["[bold]Size[/]"];

    private IRenderable BuildYearBreakdown()
    {
        var yearBreakdown = _allReviewable
            .Where(c => c.Email?.Date != null)
            .GroupBy(c => c.Email!.Date!.Value.Year)
            .OrderBy(g => g.Key)
            .Select(g => new { Year = g.Key, Count = g.Count(), Size = g.Sum(c => c.Email?.SizeEstimate ?? 0) })
            .ToList();

        if (yearBreakdown.Count <= 1)
            return new Markup("");

        var table = new Table().Border(TableBorder.Minimal).Expand();
        table.AddColumn("[bold]Year[/]");
        foreach (var yb in yearBreakdown)
        {
            var highlight = _yearFilter == yb.Year ? "[bold cyan]" : "[dim]";
            table.AddColumn(new TableColumn($"{highlight}{yb.Year}[/]").RightAligned());
        }
        table.AddRow(_emailsHeader.Concat(yearBreakdown.Select(yb =>
        {
            var highlight = _yearFilter == yb.Year ? "[bold cyan]" : "[dim]";
            return $"{highlight}{yb.Count:N0}[/]";
        })).ToArray());
        table.AddRow(_sizeHeader.Concat(yearBreakdown.Select(yb =>
        {
            var highlight = _yearFilter == yb.Year ? "[bold cyan]" : "[dim]";
            return $"{highlight}{FormatSize(yb.Size)}[/]";
        })).ToArray());
        return table;
    }

    private IRenderable BuildCategoryTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
            .AddColumn("[bold]Category[/]")
            .AddColumn(new TableColumn("[bold]Emails[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Senders[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn("[bold]Top Domain[/]")
            .AddColumn("[bold]Progress[/]");

        for (int i = 0; i < _groups.Count; i++)
        {
            var g = _groups[i];
            var count = g.Classifications.Count;
            var senderCount = g.Classifications.Select(c => c.Email?.From).Distinct().Count();
            var size = FormatSize(g.Classifications.Sum(c => c.Email?.SizeEstimate ?? 0));
            var topDomain = g.Classifications
                .GroupBy(c => c.Email?.FromDomain ?? "unknown")
                .OrderByDescending(sg => sg.Count())
                .FirstOrDefault()?.Key ?? "-";
            var decided = g.Classifications.Count(c => c.ReviewDecision != ReviewDecision.Pending);
            var progress = decided == count
                ? g.Decision switch
                {
                    ReviewDecision.ApproveTrash => "[red]\u2717 Trash[/]",
                    ReviewDecision.Keep => "[green]\u2713 Keep[/]",
                    ReviewDecision.Whitelisted => "[cyan]\u2713 Whitelisted[/]",
                    _ => "[green]\u2713 Done[/]"
                }
                : decided > 0 ? $"[yellow]{decided}/{count}[/]"
                : "[dim]Not started[/]";

            table.AddRow(
                $"[bold]{i + 1}[/]",
                $"[bold]{g.Category}[/]",
                count.ToString("N0"),
                senderCount.ToString("N0"),
                size,
                Markup.Escape(topDomain.Length > 25 ? topDomain[..22] + "..." : topDomain),
                progress);
        }

        return table;
    }

    private async Task HandleDashboardInput(ConsoleKeyInfo key, CancellationToken ct)
    {
        var upper = char.ToUpperInvariant(key.KeyChar);

        if (key.Key == ConsoleKey.Escape || upper == 'B')
        {
            return;
        }

        if (upper == 'Y')
        {
            EnterYearSelect();
            return;
        }

        if (upper == 'S')
        {
            if (_dirty)
                await SaveAsync(ct);
            return;
        }

        if (int.TryParse(upper.ToString(), out var num) && num >= 1 && num <= _groups.Count)
        {
            _selectedCategory = num - 1;
            _activeCategoryGroup = _groups[_selectedCategory];
            _categoryPage = 0;
            _hidingDecided = true;
            _lastSelectedSenderIdx = -1;
            BuildCategorySenderList();
            _subView = SubView.Category;
        }
    }

    // ── Year Select ────────────────────────────────────────────────

    private IRenderable BuildYearSelect()
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Select Year[/]\n").Centered(),
        };

        var choices = new List<string> { "All years" };
        choices.AddRange(_availableYears.Select(y => y.ToString(CultureInfo.InvariantCulture)));

        for (int i = 0; i < choices.Count; i++)
        {
            var prefix = i == _yearSelectIndex ? "[bold cyan]›[/] " : "  ";
            var style = i == _yearSelectIndex ? "[bold cyan]" : "";
            var end = i == _yearSelectIndex ? "[/]" : "";
            content.Add(new Markup($"{prefix}{style}{i + 1}. {choices[i]}{end}"));
        }

        var digitHint = _yearInputDigits.Length > 0
            ? $" [bold cyan]Enter: {_yearInputDigits}[/]"
            : "  Type number then Enter, or use ↑↓ arrows";

        content.Add(new Markup($"\n{digitHint}"));
        content.Add(new Markup("  [dim]Enter[/] select  [dim]↑↓[/] move  [bold]Esc[/]/[bold]B[/] cancel"));

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private Task HandleYearSelectInput(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Key == ConsoleKey.UpArrow)
        {
            _yearInputDigits = "";
            if (_yearSelectIndex > 0)
                _yearSelectIndex--;
            return Task.CompletedTask;
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            _yearInputDigits = "";
            var maxIndex = _availableYears.Count;
            if (_yearSelectIndex < maxIndex)
                _yearSelectIndex++;
            return Task.CompletedTask;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            if (_yearInputDigits.Length > 0
                && int.TryParse(_yearInputDigits, out var parsed)
                && parsed >= 1 && parsed <= _availableYears.Count + 1)
            {
                _yearSelectIndex = parsed - 1;
            }
            ApplyYearSelection();
            return Task.CompletedTask;
        }

        var upper = char.ToUpperInvariant(key.KeyChar);

        if (upper == 'B' || key.Key == ConsoleKey.Escape)
        {
            _yearInputDigits = "";
            _subView = SubView.Dashboard;
            return Task.CompletedTask;
        }

        if (char.IsDigit(upper))
        {
            _yearInputDigits += upper;
            if (_yearInputDigits.Length >= 4)
                _yearInputDigits = _yearInputDigits[^4..];
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    private void EnterYearSelect()
    {
        _yearSelectIndex = _yearFilter.HasValue
            ? _availableYears.IndexOf(_yearFilter.Value) + 1
            : 0;
        _yearInputDigits = "";
        _subView = SubView.YearSelect;
    }

    private void ApplyYearSelection()
    {
        if (_yearSelectIndex == 0)
        {
            _yearFilter = null;
        }
        else
        {
            var yearIdx = _yearSelectIndex - 1;
            if (yearIdx >= 0 && yearIdx < _availableYears.Count)
                _yearFilter = _availableYears[yearIdx];
        }
        RebuildGroups();
        _subView = SubView.Dashboard;
        _yearInputDigits = "";
    }

    // ── Category View ──────────────────────────────────────────────

    private void BuildCategorySenderList()
    {
        if (_activeCategoryGroup == null)
            return;

        _categorySenders = _activeCategoryGroup.Classifications
            .GroupBy(c => new { Domain = c.Email?.FromDomain ?? "unknown", Email = c.Email?.From ?? "unknown" })
            .OrderByDescending(g => g.Count())
            .Select(g => new ReviewSenderGroup
            {
                From = g.Key.Email,
                Domain = g.Key.Domain,
                Classifications = g.ToList(),
                Decision = g.All(c => c.ReviewDecision == ReviewDecision.ApproveTrash) ? ReviewDecision.ApproveTrash
                         : g.All(c => c.ReviewDecision == ReviewDecision.Keep) ? ReviewDecision.Keep
                         : g.All(c => c.ReviewDecision == ReviewDecision.Whitelisted) ? ReviewDecision.Whitelisted
                         : ReviewDecision.Pending
            })
            .ToList();
    }

    private IRenderable BuildCategory()
    {
        var parts = new List<IRenderable>();

        if (_activeCategoryGroup == null)
        {
            parts.Add(new Markup("[red]No category selected[/]"));
            return new Rows(parts);
        }

        BuildCategorySenderList();

        var filtered = _hidingDecided
            ? _categorySenders.Where(s => s.Decision == ReviewDecision.Pending).ToList()
            : _categorySenders;

        var totalPages = Math.Max(1, (int)Math.Ceiling((double)filtered.Count / PageSize));
        _categoryPage = Math.Clamp(_categoryPage, 0, totalPages - 1);
        var start = _categoryPage * PageSize;
        var pageSenders = filtered.Skip(start).Take(PageSize).ToList();

        var yearInfo = _yearFilter.HasValue ? $" [cyan]({_yearFilter.Value})[/]" : "";
        var totalDecided = _categorySenders.Count(s => s.Decision != ReviewDecision.Pending);
        var pct = _categorySenders.Count > 0 ? totalDecided * 100 / _categorySenders.Count : 100;
        var bar = new string('█', pct / 5) + new string('░', 20 - pct / 5);

        parts.Add(new Rule($"[bold blue]{_activeCategoryGroup.Category}[/]{yearInfo} — {_activeCategoryGroup.Classifications.Count:N0} emails, {_categorySenders.Count} senders").LeftJustified());
        parts.Add(new Markup($"  [green]{bar}[/] {totalDecided}/{_categorySenders.Count} reviewed ({pct}%)"));

        if (filtered.Count == 0)
        {
            parts.Add(new Markup(_hidingDecided && totalDecided > 0
                ? "\n  [green]All senders reviewed![/] Press [blue]H[/] to show all, [yellow]B[/] to go back."
                : "\n  [dim]No senders to display.[/]"));
        }
        else
        {
            var filterLabel = _hidingDecided ? "[green]pending only[/]" : "[dim]all senders[/]";
            parts.Add(new Markup($"  {start + 1}–{Math.Min(start + PageSize, filtered.Count)} of {filtered.Count} senders ({filterLabel})"));
            if (totalPages > 1)
                parts.Add(new Markup($"  Page [bold]{_categoryPage + 1}[/] of {totalPages}"));

            var table = new Table()
                .Border(TableBorder.Simple)
                .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
                .AddColumn("[bold]Sender[/]")
                .AddColumn(new TableColumn("[bold]Emails[/]").RightAligned())
                .AddColumn("[bold]Domain[/]")
                .AddColumn("[bold]Status[/]");

            for (int i = 0; i < pageSenders.Count; i++)
            {
                var s = pageSenders[i];
                var from = s.From.Length > 40 ? s.From[..37] + "..." : s.From;
                var displayNum = start + i + 1;
                var marker = (start + i) == _lastSelectedSenderIdx ? "[bold cyan]›[/]" : " ";
                table.AddRow(
                    $"{marker}{displayNum}",
                    Markup.Escape(from),
                    s.Classifications.Count.ToString("N0"),
                    Markup.Escape(s.Domain),
                    FormatDecision(s.Decision));
            }

            parts.Add(table);
        }

        return new Rows(parts);
    }

    private async Task HandleCategoryInput(ConsoleKeyInfo key, CancellationToken ct)
    {
        var upper = char.ToUpperInvariant(key.KeyChar);

        if (_pendingCatCmd.HasValue)
        {
            await HandlePendingCatCommand(upper, ct);
            return;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            if (_activeCategoryGroup != null)
                UpdateCategoryDecision();
            _subView = SubView.Dashboard;
            return;
        }

        if (upper == 'B')
        {
            if (_activeCategoryGroup != null)
                UpdateCategoryDecision();
            _subView = SubView.Dashboard;
            return;
        }

        if (upper == 'N')
        {
            var nFiltered = GetFilteredSenders();
            var totalPages = Math.Max(1, (int)Math.Ceiling((double)nFiltered.Count / PageSize));
            if (_categoryPage < totalPages - 1)
                _categoryPage++;
            return;
        }

        if (upper == 'P')
        {
            if (_categoryPage > 0)
                _categoryPage--;
            return;
        }

        if (upper == 'H')
        {
            _hidingDecided = !_hidingDecided;
            _categoryPage = 0;
            _lastSelectedSenderIdx = -1;
            return;
        }

        if (upper == 'Y')
        {
            EnterYearSelect();
            return;
        }

        if (upper == 'T' || upper == 'K')
        {
            _pendingCatCmd = upper;
            return;
        }

        BuildCategorySenderList();
        var filtered = GetFilteredSenders();

        if (int.TryParse(upper.ToString(), out var num) && num >= 1 && num <= filtered.Count)
        {
            _lastSelectedSenderIdx = num - 1;
            var sender = filtered[_lastSelectedSenderIdx];
            _activeSender = sender;
            _senderPage = 0;
            _senderEmails = sender.Classifications.OrderByDescending(c => c.Email?.Date).ToList();
            _subView = SubView.Sender;
        }
    }

    private async Task HandlePendingCatCommand(char upper, CancellationToken ct)
    {
        var cmd = _pendingCatCmd!.Value;
        _pendingCatCmd = null;

        if (upper == 'B' || upper == (char)27)
            return;

        BuildCategorySenderList();
        var filtered = GetFilteredSenders();

        if (upper == 'A')
        {
            var decision = cmd == 'T' ? ReviewDecision.ApproveTrash : ReviewDecision.Keep;
            await ApplyBulkDecision(decision, ct);
            return;
        }

        if (int.TryParse(upper.ToString(), out var num) && num >= 1 && num <= filtered.Count)
        {
            var target = filtered[num - 1];
            var decision = cmd == 'T' ? ReviewDecision.ApproveTrash : ReviewDecision.Keep;
            foreach (var c in target.Classifications)
                c.ReviewDecision = decision;
            target.Decision = decision;
            MarkDirty(target.Classifications.Count);
            await AutoSaveIfNeeded(ct);
            _lastSelectedSenderIdx = FindNextPendingIndex(filtered, num - 1);
            _categoryPage = _lastSelectedSenderIdx >= 0 ? _lastSelectedSenderIdx / PageSize : _categoryPage;
        }
    }

    private List<ReviewSenderGroup> GetFilteredSenders()
    {
        return _hidingDecided
            ? _categorySenders.Where(s => s.Decision == ReviewDecision.Pending).ToList()
            : _categorySenders;
    }

    private async Task ApplyBulkDecision(ReviewDecision decision, CancellationToken ct)
    {
        if (_activeCategoryGroup == null)
            return;
        var pending = _activeCategoryGroup.Classifications.Where(c => c.ReviewDecision == ReviewDecision.Pending).ToList();
        if (pending.Count == 0)
            return;
        foreach (var c in pending)
            c.ReviewDecision = decision;
        MarkDirty(pending.Count);
        await AutoSaveIfNeeded(ct);
        BuildCategorySenderList();
    }

    private static int FindNextPendingIndex(List<ReviewSenderGroup> senders, int current)
    {
        for (int i = current + 1; i < senders.Count; i++)
            if (senders[i].Decision == ReviewDecision.Pending)
                return i;
        for (int i = 0; i < current; i++)
            if (senders[i].Decision == ReviewDecision.Pending)
                return i;
        return -1;
    }

    private void UpdateCategoryDecision()
    {
        if (_activeCategoryGroup == null)
            return;
        _activeCategoryGroup.Decision = _activeCategoryGroup.Classifications switch
        {
            var c when c.All(x => x.ReviewDecision == ReviewDecision.ApproveTrash) => ReviewDecision.ApproveTrash,
            var c when c.All(x => x.ReviewDecision == ReviewDecision.Keep) => ReviewDecision.Keep,
            var c when c.All(x => x.ReviewDecision == ReviewDecision.Whitelisted) => ReviewDecision.Whitelisted,
            _ => ReviewDecision.Pending,
        };
    }

    // ── Sender View ────────────────────────────────────────────────

    private IRenderable BuildSender()
    {
        var parts = new List<IRenderable>();

        if (_activeSender == null)
        {
            parts.Add(new Markup("[red]No sender selected[/]"));
            return new Rows(parts);
        }

        var ordered = _senderEmails;
        var total = ordered.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling((double)total / SenderPageSize));
        _senderPage = Math.Clamp(_senderPage, 0, totalPages - 1);

        parts.Add(new Rule($"[bold blue]{Markup.Escape(_activeSender.From)}[/] — {total} emails").LeftJustified());
        parts.Add(new Markup($"  Domain: [cyan]{Markup.Escape(_activeSender.Domain)}[/]"));

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("[bold]Date[/]")
            .AddColumn("[bold]Subject[/]")
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned());

        var pageItems = ordered.Skip(_senderPage * SenderPageSize).Take(SenderPageSize).Select(c => c.Email).ToList();
        foreach (var email in pageItems)
        {
            var date = email?.Date?.ToString("yyyy-MM-dd") ?? "-";
            var subject = email?.Subject ?? "-";
            subject = subject.Length > 70 ? subject[..67] + "..." : subject;
            var size = FormatSize(email?.SizeEstimate ?? 0);
            table.AddRow(date, Markup.Escape(subject), size);
        }

        if (totalPages > 1)
            table.Caption($"[dim]Page {_senderPage + 1}/{totalPages} — {total} emails[/]");

        parts.Add(table);

        return new Rows(parts);
    }

    private async Task HandleSenderInput(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_activeSender == null)
            return;

        if (key.Key == ConsoleKey.LeftArrow)
        {
            var totalPages = Math.Max(1, (int)Math.Ceiling((double)_senderEmails.Count / SenderPageSize));
            if (_senderPage > 0)
                _senderPage--;
            return;
        }

        if (key.Key == ConsoleKey.RightArrow)
        {
            var totalPages = Math.Max(1, (int)Math.Ceiling((double)_senderEmails.Count / SenderPageSize));
            if (_senderPage < totalPages - 1)
                _senderPage++;
            return;
        }

        var upper = char.ToUpperInvariant(key.KeyChar);

        if (upper == 'B' || key.Key == ConsoleKey.Escape)
        {
            _subView = SubView.Category;
            return;
        }

        if (upper == 'T')
        {
            ApplySenderDecision(ReviewDecision.ApproveTrash);
            _subView = SubView.Category;
            return;
        }

        if (upper == 'K')
        {
            ApplySenderDecision(ReviewDecision.Keep);
            _subView = SubView.Category;
            return;
        }

        if (upper == 'W')
        {
            await WhitelistSenderDomainAsync(ct);
            _subView = SubView.Category;
        }
    }

    private void ApplySenderDecision(ReviewDecision decision)
    {
        if (_activeSender == null)
            return;
        foreach (var c in _activeSender.Classifications)
            c.ReviewDecision = decision;
        _activeSender.Decision = decision;
        MarkDirty(_activeSender.Classifications.Count);
    }

    private async Task WhitelistSenderDomainAsync(CancellationToken ct)
    {
        if (_activeSender == null)
            return;
        var existing = await _db.Whitelist
            .AnyAsync(w => w.Pattern == _activeSender.Domain, ct);
        if (!existing)
        {
            _db.Whitelist.Add(new WhitelistEntry
            {
                Pattern = _activeSender.Domain,
                PatternType = "domain",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        ApplySenderDecision(ReviewDecision.Whitelisted);
    }
}

// ── Data types ─────────────────────────────────────────────────────

internal sealed class ReviewCategoryGroup
{
    public ClassificationCategory Category { get; set; }
    public List<Classification> Classifications { get; set; } = [];
    public ReviewDecision Decision { get; set; }
}

internal sealed class ReviewSenderGroup
{
    public string From { get; set; } = "";
    public string Domain { get; set; } = "";
    public List<Classification> Classifications { get; set; } = [];
    public ReviewDecision Decision { get; set; }
}
