using System.Globalization;
using MailMopper.Models;
using MailMopper.Services;
using Spectre.Console;

namespace MailMopper.Tui;

/// <summary>
/// Sender detail view — lists individual emails for a sender with pagination and action keys.
/// </summary>
public partial class ReviewApp
{
    private enum SenderAction { Back, Decided }

    private const int SenderPageSize = 25;

    private async Task<SenderAction> ShowSenderAsync(ReviewSenderGroup sender, CancellationToken ct)
    {
        var ordered = sender.Classifications.OrderByDescending(c => c.Email?.Date).ToList();
        var total = ordered.Count;
        var page = 0;
        var totalPages = (int)Math.Ceiling((double)total / SenderPageSize);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            RenderSenderPage(sender, ordered, total, page, totalPages);

            AnsiConsole.Markup("[blue]Action: [/]");
            var key = Console.ReadKey(intercept: true);
            Console.WriteLine();

            if (key.Key == ConsoleKey.LeftArrow && page > 0)
            {
                page--;
                continue;
            }
            if (key.Key == ConsoleKey.RightArrow && page < totalPages - 1)
            {
                page++;
                continue;
            }

            var action = char.ToUpper(key.KeyChar, CultureInfo.InvariantCulture);
            var result = await HandleSenderKeyAsync(action, sender, ct);
            if (result.HasValue)
                return result.Value;
        }
    }

    private static void RenderSenderPage(ReviewSenderGroup sender, IList<Classification> ordered, int total, int page, int totalPages)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold blue]{Markup.Escape(sender.From)}[/] — {total} emails").LeftJustified());
        AnsiConsole.MarkupLine($"  Domain: [cyan]{Markup.Escape(sender.Domain)}[/]");
        AnsiConsole.WriteLine();

        var emailTable = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("[bold]Date[/]")
            .AddColumn("[bold]Subject[/]")
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned());

        foreach (var email in ordered.Skip(page * SenderPageSize).Take(SenderPageSize).Select(c => c.Email))
        {
            var date = email?.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
            var subject = email?.Subject ?? "-";
            subject = subject.Length > 70 ? subject[..67] + "..." : subject;
            var size = ReviewService.FormatSize(email?.SizeEstimate ?? 0);
            emailTable.AddRow(date, Markup.Escape(subject), size);
        }

        if (totalPages > 1)
            emailTable.Caption($"[dim]Page {page + 1}/{totalPages} — {total} emails[/]");

        AnsiConsole.Write(emailTable);
        AnsiConsole.WriteLine();

        var hints = new List<string> { "[red]T[/]=trash", "[green]K[/]=keep", "[cyan]W[/]=whitelist domain", "[yellow]B[/]=back" };
        if (page > 0)
            hints.Add("[dim]←[/]=prev");
        if (page < totalPages - 1)
            hints.Add("[dim]→[/]=next");
        AnsiConsole.MarkupLine($"  {string.Join("  ", hints)}");
    }

    private async Task<SenderAction?> HandleSenderKeyAsync(char action, ReviewSenderGroup sender, CancellationToken ct)
    {
        if (action == 'T')
        {
            ReviewService.ApplySenderDecision(sender, ReviewDecision.ApproveTrash);
            MarkDirty(sender.Classifications.Count);
            return SenderAction.Decided;
        }
        if (action == 'K')
        {
            ReviewService.ApplySenderDecision(sender, ReviewDecision.Keep);
            MarkDirty(sender.Classifications.Count);
            return SenderAction.Decided;
        }
        if (action == 'W')
        {
            await _review.WhitelistDomainAsync(sender.Domain, sender, ct);
            MarkDirty(sender.Classifications.Count);
            return SenderAction.Decided;
        }
        return action == 'B' ? SenderAction.Back : null;
    }
}
