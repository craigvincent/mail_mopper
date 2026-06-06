using System.ComponentModel;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class ExecuteSettings : CommandSettings
{
    [CommandOption("--dry-run")]
    [Description("Preview what would be trashed without actually trashing")]
    public bool DryRun { get; set; }

    [CommandOption("--force")]
    [Description("Skip confirmation prompt")]
    public bool Force { get; set; }
}

public class ExecuteCommand(GmailAuthService authService, ActionService actionService, AppDbContext dbContext, AppCancellation cancellation) : AsyncCommand<ExecuteSettings>
{
    private readonly GmailAuthService _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    private readonly ActionService _actionService = actionService ?? throw new ArgumentNullException(nameof(actionService));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context, ExecuteSettings settings)
    {
        try
        {
            var ct = _cancellation.Token;

            AnsiConsole.MarkupLine("[bold blue]Execute Actions[/]");

            await _dbContext.Database.EnsureCreatedAsync(ct);

            // Count approved-for-trash classifications
            var approvedCount = await _dbContext.Classifications
                .Where(c => c.ReviewDecision == Models.ReviewDecision.ApproveTrash)
                .CountAsync(ct);

            if (approvedCount == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No emails approved for trash.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"\n[bold]Preview:[/] {approvedCount} email(s) approved for trash");

            // If dry-run, stop here
            if (settings.DryRun)
            {
                AnsiConsole.MarkupLine("\n[yellow]Dry-run mode: no emails were trashed.[/]");
                return 0;
            }

            // Confirm if not force
            if (!settings.Force)
            {
                var confirm = AnsiConsole.Confirm("\n[bold yellow]Proceed with trashing these emails?[/]", false);
                if (!confirm)
                {
                    AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                    return 0;
                }
            }

            // Authenticate
            await AnsiConsole.Status()
                .StartAsync("Authenticating with Gmail...", async ctx =>
                {
                    await _authService.AuthenticateAsync(ct);
                });

            // Execute with progress
            ActionSummary? result = null;

            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[bold green]Trashing emails[/]");
                    var progress = new Progress<(int processed, int total)>(p =>
                    {
                        if (p.total > 0)
                            task.Value = (double)p.processed / p.total * 100;
                    });
                    result = await _actionService.TrashApprovedAsync(settings.DryRun, progress, ct);
                });

            AnsiConsole.MarkupLine("[green]✓ Execution complete![/]");
            AnsiConsole.MarkupLine($"  [bold]Emails trashed:[/] {result?.EmailsTrashed}");
            AnsiConsole.MarkupLine($"  [bold]Errors:[/] {result?.Errors}");
            AnsiConsole.MarkupLine($"  [bold]Session ID:[/] {result?.SessionId}");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during execution: {ex.Message}[/]");
            return 1;
        }
    }
}
