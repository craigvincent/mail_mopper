using MailMopper.Data;
using MailMopper.Services;
using MailMopper.Tui;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class ReviewCommand(ReviewApp reviewApp, AppDbContext dbContext, AppCancellation cancellation) : AsyncCommand
{
    private readonly ReviewApp _reviewApp = reviewApp ?? throw new ArgumentNullException(nameof(reviewApp));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            AnsiConsole.MarkupLine("[bold blue]Email Review[/]");

            await _dbContext.Database.EnsureCreatedAsync(_cancellation.Token);

            await _reviewApp.RunAsync(_cancellation.Token);

            AnsiConsole.MarkupLine("[green]✓ Review complete![/]");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during review: {ex.Message}[/]");
            return 1;
        }
    }
}
