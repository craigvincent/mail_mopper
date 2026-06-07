using System.ComponentModel;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using MailMopper.Tui;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class RunSettings : CommandSettings
{
    [CommandOption("--skip-ml")]
    [Description("Skip ML classification, use rules only")]
    public bool SkipMl { get; set; }

    [CommandOption("--dry-run")]
    [Description("Preview what would be trashed without actually trashing")]
    public bool DryRun { get; set; }
}

public class RunCommand(GmailServices gmailServices, RuleClassifier ruleClassifier, ReviewApp reviewApp, ActionService actionService, AppDbContext dbContext, AppSettings appSettings, AppCancellation cancellation) : AsyncCommand<RunSettings>
{
    private readonly GmailServices _gmailServices = gmailServices ?? throw new ArgumentNullException(nameof(gmailServices));
    private readonly RuleClassifier _ruleClassifier = ruleClassifier ?? throw new ArgumentNullException(nameof(ruleClassifier));
    private readonly ReviewApp _reviewApp = reviewApp ?? throw new ArgumentNullException(nameof(reviewApp));
    private readonly ActionService _actionService = actionService ?? throw new ArgumentNullException(nameof(actionService));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppSettings _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings)
    {
        try
        {
            var ct = _cancellation.Token;

            AnsiConsole.MarkupLine("[bold blue]Gmail Cleanup - Full Pipeline[/]");

            await _dbContext.Database.EnsureCreatedAsync(ct);

            // Step 1: Authenticate
            AnsiConsole.MarkupLine("\n[bold]Step 1: Authentication[/]");
            await AnsiConsole.Status()
                .StartAsync("Authenticating with Gmail...", async ctx =>
                {
                    await _gmailServices.Auth.AuthenticateAsync(ct);
                });

            // Step 2: Fetch
            AnsiConsole.MarkupLine("\n[bold]Step 2: Fetching Emails[/]");
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
                    totalFetched = await _gmailServices.Fetch.FetchIncrementalAsync(progress, ct);
                });

            AnsiConsole.MarkupLine($"[green]✓ Fetched {totalFetched} email(s)[/]");

            // Step 3: Classify
            AnsiConsole.MarkupLine("\n[bold]Step 3: Classifying Emails[/]");

            MlClassifier? mlClassifier = null;
            if (!settings.SkipMl)
            {
                var modelPath = _appSettings.Ml?.ModelPath ?? ModelTrainerService.GetDefaultModelPath();
                if (File.Exists(modelPath))
                    mlClassifier = new MlClassifier(_appSettings, modelPath);
            }
            using var mlCleanup = mlClassifier; // ensure disposal

            var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier, _dbContext, _appSettings);

            ClassificationSummary? summary = null;
            Console.WriteLine("Step 2: Classifying emails...");
            summary = await pipeline.RunAsync(
                settings.SkipMl,
                onStatus: msg => Console.WriteLine($"  {msg}"),
                ct);

            AnsiConsole.MarkupLine($"[green]✓ Classified {(summary?.RuleClassified ?? 0) + (summary?.MlClassified ?? 0)} email(s)[/]");

            // Show classification results
            var categoryBreakdown = await _dbContext.Classifications
                .GroupBy(c => c.Category)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync(ct);

            var table = new Table();
            table.AddColumn("[bold]Category[/]");
            table.AddColumn("[bold]Count[/]", col => col.RightAligned());
            var total = categoryBreakdown.Sum(x => x.Count);
            foreach (var item in categoryBreakdown)
            {
                var percentage = total > 0 ? (item.Count * 100.0 / total) : 0;
                table.AddRow(item.Category.ToString(), $"{item.Count} ({percentage:F1}%)");
            }
            AnsiConsole.Write(table);

            // Step 4: Review
            AnsiConsole.MarkupLine("\n[bold]Step 4: Review (Interactive)[/]");
            AnsiConsole.MarkupLine("[yellow]Starting review interface...[/]");
            await _reviewApp.RunAsync(ct);

            AnsiConsole.MarkupLine("[green]✓ Review complete![/]");

            // Step 5: Execute
            AnsiConsole.MarkupLine("\n[bold]Step 5: Executing Actions[/]");

            var approvedCount = await _dbContext.Classifications
                .Where(c => c.ReviewDecision == Models.ReviewDecision.ApproveTrash)
                .CountAsync(ct);

            if (approvedCount == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No actions to execute.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[yellow]Ready to trash {approvedCount} email(s)[/]");

            if (settings.DryRun)
            {
                AnsiConsole.MarkupLine("[yellow]Dry-run mode: skipping email deletion[/]");
                return 0;
            }

            var confirm = AnsiConsole.Confirm("[bold yellow]Proceed with trashing emails?[/]", false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                return 0;
            }

            ActionSummary? actionResult = null;

            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[bold green]Trashing emails[/]");
                    var progress = new Progress<(int processed, int total)>(p =>
                    {
                        if (p.total > 0)
                            task.Value = (double)p.processed / p.total * 100;
                    });
                    actionResult = await _actionService.TrashApprovedAsync(false, progress, ct);
                });

            AnsiConsole.MarkupLine("[green]✓ Pipeline complete![/]");
            AnsiConsole.MarkupLine($"[bold]Summary:[/]");
            AnsiConsole.MarkupLine($"  Fetched: {totalFetched}");
            AnsiConsole.MarkupLine($"  Classified: {(summary?.RuleClassified ?? 0) + (summary?.MlClassified ?? 0)}");
            AnsiConsole.MarkupLine($"  Trashed: {actionResult?.EmailsTrashed}");
            AnsiConsole.MarkupLine($"  Session ID: {actionResult?.SessionId}");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during pipeline: {ex.Message}[/]");
            return 1;
        }
    }
}
