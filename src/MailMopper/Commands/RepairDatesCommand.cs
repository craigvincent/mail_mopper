using MailMopper.Data;
using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class RepairDatesCommand(GmailAuthService authService, GmailFetchService fetchService, AppDbContext dbContext, AppCancellation cancellation) : AsyncCommand
{
    private readonly GmailAuthService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    private readonly GmailFetchService _fetchService = fetchService ?? throw new ArgumentNullException(nameof(fetchService));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            var ct = _cancellation.Token;

            AnsiConsole.MarkupLine("[bold blue]Repair Email Dates[/]");
            AnsiConsole.MarkupLine("[dim]Fixes emails whose date was incorrectly set to fetch time.[/]");
            AnsiConsole.WriteLine();

            await _dbContext.Database.EnsureCreatedAsync(ct);

            await AnsiConsole.Status()
                .StartAsync("Authenticating with Gmail...", async ctx =>
                {
                    await _authService.AuthenticateAsync(ct);
                });

            var repaired = await _fetchService.RepairDatesAsync(
                onStatus: msg => AnsiConsole.MarkupLine($"  {Markup.Escape(msg)}"),
                ct);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]✓ Repaired {repaired} email date(s).[/]");

            if (repaired > 0)
                AnsiConsole.MarkupLine("[dim]Year filters in review will now show correct dates.[/]");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
