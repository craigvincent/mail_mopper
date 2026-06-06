using System.Globalization;
using MailMopper.Services;
using Spectre.Console;

namespace MailMopper.Tui;

public partial class ReviewApp(ReviewService review)
{
    private readonly ReviewService _review = review ?? throw new ArgumentNullException(nameof(review));

    internal const int PageSize = 30;

    public async Task RunAsync(CancellationToken ct)
    {
        await _review.LoadDataAsync(ct);

        if (_review.Groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No classified emails to review. Run 'classify' first.[/]");
            return;
        }

        var running = true;
        while (running)
        {
            ct.ThrowIfCancellationRequested();
            running = await ShowDashboardAsync(ct);
        }

        if (_review.IsDirty)
        {
            await _review.SaveAsync(ct);
            AnsiConsole.MarkupLine("[green]✓ Decisions saved.[/]");
        }
    }

    private async Task AutoSaveIfNeeded(CancellationToken ct) => await _review.AutoSaveIfNeededAsync(ct);

    private void MarkDirty(int actionCount = 1) => _review.MarkDirty(actionCount);

    private static string FormatSize(long bytes) => ReviewService.FormatSize(bytes);

    // ── Input helpers ─────────────────────────────────────────────────

    private static string ReadCommand(string prompt, string instantKeys, string? defaultValue = null)
    {
        AnsiConsole.Markup(prompt);

        var firstKey = Console.ReadKey(intercept: true);

        if (firstKey.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return defaultValue ?? "";
        }
        if (firstKey.Key == ConsoleKey.Escape)
        {
            Console.WriteLine();
            return "B";
        }

        var c = char.ToUpper(firstKey.KeyChar, CultureInfo.InvariantCulture);

        if (instantKeys.Contains(c))
        {
            Console.WriteLine();
            return c.ToString();
        }

        Console.Write(firstKey.KeyChar);
        var rest = Console.ReadLine() ?? "";
        return firstKey.KeyChar + rest;
    }

    private void PromptYearFilter()
    {
        var yearChoices = new List<string> { "All years" };
        yearChoices.AddRange(_review.AvailableYears.Select(y => y.ToString(CultureInfo.InvariantCulture)));
        var yearPick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a year:")
                .PageSize(15)
                .AddChoices(yearChoices));

        if (yearPick == "All years")
            _review.YearFilter = null;
        else if (int.TryParse(yearPick, out var y))
            _review.YearFilter = y;
    }

    private void RenderYearBreakdown()
    {
        var yearBreakdown = _review.AllReviewable
            .Where(c => c.Email?.Date != null)
            .GroupBy(c => c.Email!.Date!.Value.Year)
            .OrderBy(g => g.Key)
            .Select(g => new { Year = g.Key, Count = g.Count(), Size = g.Sum(c => c.Email?.SizeEstimate ?? 0) })
            .ToList();

        if (yearBreakdown.Count <= 1)
            return;

        var yearTable = new Table().Border(TableBorder.Minimal).Expand();
        yearTable.AddColumn("[bold]Year[/]");
        foreach (var yb in yearBreakdown)
        {
            var highlight = _review.YearFilter == yb.Year ? "[bold cyan]" : "[dim]";
            yearTable.AddColumn(new TableColumn($"{highlight}{yb.Year}[/]").RightAligned());
        }
        var emailsRow = new List<string> { "[bold]Emails[/]" }
                .Concat(yearBreakdown.Select(yb =>
                {
                    var highlight = _review.YearFilter == yb.Year ? "[bold cyan]" : "[dim]";
                    return $"{highlight}{yb.Count:N0}[/]";
                }))
                .ToArray();
        yearTable.AddRow(emailsRow);
        var sizeRow = new List<string> { "[bold]Size[/]" }
                .Concat(yearBreakdown.Select(yb =>
                {
                    var highlight = _review.YearFilter == yb.Year ? "[bold cyan]" : "[dim]";
                    return $"{highlight}{FormatSize(yb.Size)}[/]";
                }))
                .ToArray();
        yearTable.AddRow(sizeRow);
        AnsiConsole.Write(yearTable);
        AnsiConsole.WriteLine();
    }
}
