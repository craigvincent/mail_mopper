using MailMopper.Data;
using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public sealed class HomeView(AppDbContext db, GmailSession session) : IAppView
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly GmailSession _session = session ?? throw new ArgumentNullException(nameof(session));

    public Action? RequestRender { get; set; }
    public Action? RequestRenderImmediate { get; set; }

    public IRenderable GetContent(int availableHeight)
    {
        var stats = GetStats();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("")
            .AddColumn(new TableColumn("Count").RightAligned())
            .AddColumn("")
            .HideHeaders();

        table.AddRow("[bold]Total emails[/]", stats.TotalEmails.ToString("N0"), FormatSize(stats.TotalSize));
        table.AddRow("[bold]Classified[/]", stats.Classified.ToString("N0"), "");
        table.AddRow("[bold]Unclassified[/]", stats.Unclassified.ToString("N0"), "");
        table.AddRow("[bold]Approved for trash[/]", stats.ApprovedForTrash.ToString("N0"), "");
        table.AddRow("[bold]Already trashed[/]", stats.Trashed.ToString("N0"), "");

        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Welcome to MailMopper[/]\n").Centered(),
            new Rule().LeftJustified(),
            table,
            new Rule().LeftJustified(),
        };

        var nextSteps = BuildNextSteps(stats);
        if (nextSteps.Count > 0)
        {
            content.Add(new Markup("\n[bold]Next Steps:[/]"));
            foreach (var step in nextSteps)
                content.Add(new Markup($"\n  [blue]→[/] {step}"));
        }

        content.Add(new Markup($"\n\n[dim]Use Tab/Shift+Tab to navigate, Q to quit, F1 for help[/]"));

        var rows = new Rows(content);
        return Align.Center(rows, VerticalAlignment.Middle);
    }

    public string GetFooterHints()
    {
        return _session.IsAuthenticated
            ? "F:Fetch C:Classify R:Review E:Execute U:Undo L:Logout"
            : "[dim]A: Authenticate  F:Fetch C:Classify R:Review E:Execute U:Undo[/]";
    }

    public Task<ViewCommand> HandleInputAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        var upper = char.ToUpperInvariant(key.KeyChar);
        return Task.FromResult(upper switch
        {
            'A' when !_session.IsAuthenticated => ViewCommand.RequestAuth,
            'L' when _session.IsAuthenticated => ViewCommand.RequestLogout,
            'F' => ViewCommand.GoToFetch,
            'C' => ViewCommand.GoToClassify,
            'R' => ViewCommand.GoToReview,
            'E' => ViewCommand.GoToExecute,
            'U' => ViewCommand.GoToUndo,
            _ => ViewCommand.None
        });
    }

    private List<string> BuildNextSteps(EmailStats stats) => BuildNextSteps(stats, _session.IsAuthenticated);

    internal static List<string> BuildNextSteps(EmailStats stats, bool isAuthenticated)
    {
        var steps = new List<string>();

        if (!isAuthenticated)
        {
            steps.Add("[yellow]Not authenticated[/] — press [bold]A[/] to authenticate with Gmail");
            return steps;
        }

        if (stats.TotalEmails == 0)
            steps.Add("[yellow]No emails fetched[/] — go to [bold]Fetch[/] tab (press F) to download your emails");
        else if (stats.Unclassified > 0)
            steps.Add($"[yellow]{stats.Unclassified:N0} emails need classification[/] — go to [bold]Classify[/] tab (press C)");
        else if (stats.Classified - stats.ApprovedForTrash - stats.Trashed > 0)
            steps.Add($"[yellow]Emails need review decisions[/] — go to [bold]Review[/] tab (press R)");
        else if (stats.ApprovedForTrash > 0)
            steps.Add($"[yellow]{stats.ApprovedForTrash:N0} emails approved for trash[/] — go to [bold]Execute[/] tab (press E)");
        else if (stats.TotalEmails > 0)
            steps.Add("[green]No pending actions![/] Fetch new emails or review the [bold]Stats[/]");

        if (steps.Count == 0 && stats.TotalEmails > 0)
            steps.Add("[dim]All caught up! Use tabs to explore or configure settings.[/]");

        return steps;
    }

    private EmailStats GetStats()
    {
        try
        {
            var totalEmails = _db.Emails.Count();
            var classified = _db.Classifications.Select(c => c.MessageId).Distinct().Count();
            var unclassified = totalEmails - classified;
            var approved = _db.Classifications.Count(c => c.ReviewDecision == Models.ReviewDecision.ApproveTrash);
            var trashed = _db.Actions.Count(a => a.Action == "trash");
            var totalSize = _db.Emails.Sum(e => e.SizeEstimate);
            return new EmailStats(totalEmails, classified, unclassified, approved, trashed, totalSize);
        }
        catch
        {
            return new EmailStats(0, 0, 0, 0, 0, 0);
        }
    }

    private static string FormatSize(long bytes) => ReviewService.FormatSize(bytes);
}
