using System.ComponentModel;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class FetchSettings : CommandSettings
{
    [CommandOption("--full")]
    [Description("Force full fetch instead of incremental")]
    public bool FullFetch { get; set; }
}

public class FetchCommand(GmailAuthService authService, GmailFetchService fetchService, AppDbContext dbContext, AppCancellation cancellation) : AsyncCommand<FetchSettings>
{
    private readonly GmailAuthService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    private readonly GmailFetchService _fetchService = fetchService ?? throw new ArgumentNullException(nameof(fetchService));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context, FetchSettings settings)
    {
        try
        {
            AnsiConsole.MarkupLine("[bold blue]Gmail Email Fetch[/]");

            var ct = _cancellation.Token;

            // Authenticate
            await AnsiConsole.Status()
                .StartAsync("Authenticating with Gmail...", async ctx =>
                {
                    await _authService.AuthenticateAsync(ct);
                });

            // Ensure database is created
            await _dbContext.Database.EnsureCreatedAsync(ct);

            // Determine fetch strategy
            var isFullFetch = settings.FullFetch;
            if (!isFullFetch)
            {
                var lastSync = await _dbContext.SyncStates
                    .FirstOrDefaultAsync(s => s.Key == "default", cancellationToken: ct);

                isFullFetch = lastSync?.LastSyncAt == null;
            }

            AnsiConsole.MarkupLine(isFullFetch
                ? "[yellow]Performing full fetch...[/]"
                : "[yellow]Performing incremental fetch...[/]");

            var startTime = DateTime.UtcNow;

            // Fetch with progress
            var totalFetched = 0;
            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[bold green]Fetching emails[/]");
                    var progress = new Progress<(int fetched, int total)>(p =>
                    {
                        if (p.total > 0)
                            task.Value = (double)p.fetched / p.total * 100;
                    });
                    totalFetched = isFullFetch
                        ? await _fetchService.FetchAllAsync(progress, ct)
                        : await _fetchService.FetchIncrementalAsync(progress, ct);
                });

            var duration = DateTime.UtcNow - startTime;

            AnsiConsole.MarkupLine("[green]✓ Fetch complete![/]");
            AnsiConsole.MarkupLine($"  [bold]Total fetched:[/] {totalFetched}");
            AnsiConsole.MarkupLine($"  [bold]Time taken:[/] {duration.TotalSeconds:F1}s");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during fetch: {ex.Message}[/]");
            return 1;
        }
    }
}
