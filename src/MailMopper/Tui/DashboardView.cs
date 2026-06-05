using System.Globalization;
using MailMopper.Models;
using MailMopper.Services;
using Spectre.Console;

namespace MailMopper.Tui;

/// <summary>
/// Dashboard view — top-level overview of categories, year filter, statistics.
/// </summary>
public partial class ReviewApp
{
    private async Task<bool> ShowDashboardAsync(CancellationToken ct)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold blue]Gmail Cleanup — Review Dashboard[/]").LeftJustified());

        var yearLabel = _review.YearFilter.HasValue ? $"[bold cyan]{_review.YearFilter.Value}[/]" : "[dim]All years[/]";
        AnsiConsole.MarkupLine($"  Year filter: {yearLabel}");
        AnsiConsole.WriteLine();

        RenderYearBreakdown();
        RenderDashboardTable();
        RenderDashboardSummary();

        var input = ReadCommand("[blue]Dashboard: [/]", "SQY");

        return string.IsNullOrWhiteSpace(input) || await HandleDashboardInputAsync(input, ct);
    }

    private void RenderDashboardTable()
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

        for (var i = 0; i < _review.Groups.Count; i++)
        {
            var g = _review.Groups[i];
            var count = g.Classifications.Count;
            var senderCount = g.Classifications.Select(c => c.Email?.From).Distinct().Count();
            var size = ReviewService.FormatSize(g.Classifications.Sum(c => c.Email?.SizeEstimate ?? 0));
            var topDomain = g.Classifications
                .GroupBy(c => c.Email?.FromDomain ?? "unknown")
                .OrderByDescending(sg => sg.Count())
                .FirstOrDefault()?.Key ?? "-";

            var decided = g.Classifications.Count(c => c.ReviewDecision != ReviewDecision.Pending);
            var progressText = BuildCategoryProgressText(g, decided, count);

            table.AddRow(
                $"[bold]{i + 1}[/]",
                $"[bold]{g.Category}[/]",
                count.ToString("N0", CultureInfo.InvariantCulture),
                senderCount.ToString("N0", CultureInfo.InvariantCulture),
                size,
                Markup.Escape(topDomain.Length > 25 ? topDomain[..22] + "..." : topDomain),
                progressText);
        }

        AnsiConsole.Write(table);
    }

    private static string BuildCategoryProgressText(ReviewCategoryGroup g, int decided, int count)
    {
        if (decided == count)
            return g.Decision switch
            {
                ReviewDecision.ApproveTrash => "[red]\u2717 Trash[/]",
                ReviewDecision.Keep => "[green]\u2713 Keep[/]",
                ReviewDecision.Whitelisted => "[cyan]\u2713 Whitelisted[/]",
                _ => "[green]\u2713 Done[/]"
            };
        if (decided > 0)
            return $"[yellow]{decided}/{count} reviewed[/]";
        return "[dim]Not started[/]";
    }

    private void RenderDashboardSummary()
    {
        var totalRemaining = _review.Groups.Sum(g => g.Classifications.Count);
        var totalRemainingSize = _review.Groups.Sum(static (ReviewCategoryGroup g) => g.Classifications.Sum(static (Classification c) => c.Email?.SizeEstimate ?? 0));
        var totalPendingEmails = _review.Groups.Sum(static (ReviewCategoryGroup g) => g.Classifications.Count(static (Classification c) => c.ReviewDecision == ReviewDecision.Pending));
        var totalTrashEmails = _review.Groups.Sum(static (ReviewCategoryGroup g) => g.Classifications.Count(static (Classification c) => c.ReviewDecision == ReviewDecision.ApproveTrash));
        var totalKeepEmails = _review.Groups.Sum(static (ReviewCategoryGroup g) => g.Classifications.Count(static (Classification c) => c.ReviewDecision == ReviewDecision.Keep));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [bold]Total:[/] {totalRemaining:N0} emails ({ReviewService.FormatSize(totalRemainingSize)})");
        if (_review.PreviouslyTrashedCount > 0)
            AnsiConsole.MarkupLine($"  [dim]Already executed:[/] {_review.PreviouslyTrashedCount:N0} emails ({ReviewService.FormatSize(_review.PreviouslyTrashedSize)})");
        AnsiConsole.MarkupLine($"  [yellow]Pending:[/] {totalPendingEmails:N0}  [red]Trash:[/] {totalTrashEmails:N0}  [green]Keep:[/] {totalKeepEmails:N0}");
        if (_review.IsDirty)
            AnsiConsole.MarkupLine($"  [dim italic]Unsaved changes ({_review.UnsavedActions} actions)[/]");
        AnsiConsole.WriteLine();

        var navHints = new List<string>
        {
            "[blue]#[/]=open category",
            "[cyan]Y[/]=year filter",
            "[bold]S[/]=save & exit",
            "[dim]Q[/]=quit"
        };
        AnsiConsole.MarkupLine($"  {string.Join("  │  ", navHints)}");
    }

    private async Task<bool> HandleDashboardInputAsync(string input, CancellationToken ct)
    {
        if (input.Equals("S", StringComparison.OrdinalIgnoreCase))
        {
            if (_review.IsDirty)
            {
                await _review.SaveAsync(ct);
                AnsiConsole.MarkupLine("[green]✓ Saved.[/]");
            }
            return false;
        }
        if (input.Equals("Q", StringComparison.OrdinalIgnoreCase))
        {
            if (_review.IsDirty && AnsiConsole.Confirm("[yellow]You have unsaved changes. Save before quitting?[/]", defaultValue: true))
            {
                await _review.SaveAsync(ct);
                AnsiConsole.MarkupLine("[green]✓ Saved.[/]");
            }
            return false;
        }
        if (input.Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            PromptYearFilter();
            _review.RebuildGroups();
            return true;
        }
        if (int.TryParse(input, out var catNum) && catNum >= 1 && catNum <= _review.Groups.Count)
        {
            await ShowCategoryAsync(_review.Groups[catNum - 1], ct);
            return true;
        }

        return true;
    }
}
