using System.Globalization;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class StatsCommand(DatabaseService databaseService, AppDbContext dbContext, AppCancellation cancellation) : AsyncCommand
{
    private readonly DatabaseService _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            var ct = _cancellation.Token;

            AnsiConsole.MarkupLine("[bold blue]Statistics[/]");

            await _dbContext.Database.EnsureCreatedAsync(ct);

            // Get stats and category summary
            var stats = await _databaseService.GetStatsAsync(ct);
            var categorySummary = await _databaseService.GetCategorySummaryAsync(ct);

            // Display overall stats
            var statsTable = new Table
            {
                Title = new TableTitle("[bold]Overall Statistics[/]")
            };
            statsTable.AddColumn("[bold]Metric[/]");
            statsTable.AddColumn("[bold]Value[/]", col => col.RightAligned());

            statsTable.AddRow("Total Emails", stats.TotalEmails.ToString(CultureInfo.InvariantCulture));
            statsTable.AddRow("Classified", stats.Classified.ToString(CultureInfo.InvariantCulture));
            statsTable.AddRow("Unclassified", stats.Unclassified.ToString(CultureInfo.InvariantCulture));
            statsTable.AddRow("Approved for Trash", stats.ApprovedForTrash.ToString(CultureInfo.InvariantCulture));
            statsTable.AddRow("Trashed", stats.Trashed.ToString(CultureInfo.InvariantCulture));
            statsTable.AddRow("Total Size", $"{stats.TotalSize / 1_048_576.0:F2} MB");

            AnsiConsole.Write(statsTable);

            // Display category breakdown
            AnsiConsole.MarkupLine("\n");
            var categoryTable = new Table
            {
                Title = new TableTitle("[bold]Category Breakdown[/]")
            };
            categoryTable.AddColumn("[bold]Category[/]");
            categoryTable.AddColumn("[bold]Count[/]", col => col.RightAligned());
            categoryTable.AddColumn("[bold]Percentage[/]", col => col.RightAligned());
            categoryTable.AddColumn("[bold]Est. Size[/]", col => col.RightAligned());

            var total = categorySummary.Sum(x => x.Count);
            foreach (var category in categorySummary.OrderByDescending(x => x.Count))
            {
                var percentage = total > 0 ? (category.Count * 100.0 / total) : 0;
                categoryTable.AddRow(
                    category.Category.ToString(),
                    category.Count.ToString(CultureInfo.InvariantCulture),
                    $"{percentage:F1}%",
                    $"{category.TotalSize / 1_048_576.0:F2} MB"
                );
            }

            AnsiConsole.Write(categoryTable);

            // Display top senders
            AnsiConsole.MarkupLine("\n");
            var topSenders = await _dbContext.Emails
                .Where(e => e.From != null)
                .GroupBy(e => e.From)
                .Select(g => new { Sender = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .Take(10)
                .ToListAsync(ct);

            if (topSenders.Count > 0)
            {
                var sendersTable = new Table
                {
                    Title = new TableTitle("[bold]Top 10 Senders[/]")
                };
                sendersTable.AddColumn("[bold]Email[/]");
                sendersTable.AddColumn("[bold]Count[/]", col => col.RightAligned());

                foreach (var sender in topSenders)
                {
                    sendersTable.AddRow(Markup.Escape(sender.Sender ?? "Unknown"), sender.Count.ToString(CultureInfo.InvariantCulture));
                }

                AnsiConsole.Write(sendersTable);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error retrieving statistics: {ex.Message}[/]");
            return 1;
        }
    }
}
