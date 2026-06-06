using System.ComponentModel;
using System.Globalization;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class ClassifySettings : CommandSettings
{
    [CommandOption("--skip-ml")]
    [Description("Skip ML classification, use rules only")]
    public bool SkipMl { get; set; }
}

public class ClassifyCommand(RuleClassifier ruleClassifier, AppDbContext dbContext, AppSettings appSettings, AppCancellation cancellation) : AsyncCommand<ClassifySettings>
{
    private readonly RuleClassifier _ruleClassifier = ruleClassifier ?? throw new ArgumentNullException(nameof(ruleClassifier));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppSettings _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context, ClassifySettings settings)
    {
        try
        {
            var ct = _cancellation.Token;

            Console.WriteLine("=== Email Classification ===");

            await _dbContext.Database.EnsureCreatedAsync(ct);

            // Try to load ML model if not skipping
            using MlClassifier? mlClassifier = !settings.SkipMl
                ? TryLoadMlClassifier(_appSettings)
                : null;

            var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier, _dbContext, _appSettings);

            var totalEmails = await _dbContext.Emails.CountAsync(ct);
            var classifiedCount = await _dbContext.Classifications.CountAsync(ct);
            var unclassifiedCount = totalEmails - classifiedCount;

            Console.WriteLine($"  {unclassifiedCount} emails to classify");
            Console.WriteLine();

            // Direct synchronous callback — no Progress<T>, no Spectre widgets
            var summary = await pipeline.RunAsync(
                settings.SkipMl,
                onStatus: msg => Console.WriteLine($"  {msg}"),
                ct);

            Console.WriteLine();

            // Get category breakdown
            var categoryBreakdown = await _dbContext.Classifications
                .GroupBy(c => c.Category)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ToListAsync(ct);

            // Display results with Spectre table (safe — pipeline is done)
            AnsiConsole.MarkupLine("[green]✓ Classification complete![/]");
            AnsiConsole.MarkupLine($"  [bold]Rules:[/] {summary.RuleClassified}  [bold]ML:[/] {summary.MlClassified}  [bold]Unclassified:[/] {summary.Unclassified}");

            var table = new Table();
            table.AddColumn("[bold]Category[/]");
            table.AddColumn("[bold]Count[/]", col => col.RightAligned());
            table.AddColumn("[bold]Percentage[/]", col => col.RightAligned());

            var total = categoryBreakdown.Sum(x => x.Count);
            foreach (var item in categoryBreakdown)
            {
                var percentage = total > 0 ? (item.Count * 100.0 / total) : 0;
                table.AddRow(item.Category.ToString(), item.Count.ToString(CultureInfo.InvariantCulture), $"{percentage:F1}%");
            }

            AnsiConsole.Write(table);

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during classification: {ex.Message}");
            return 1;
        }
    }

    private static MlClassifier? TryLoadMlClassifier(AppSettings appSettings)
    {
        var modelPath = appSettings.Ml?.ModelPath ?? ModelTrainerService.GetDefaultModelPath();
        if (File.Exists(modelPath))
        {
            Console.WriteLine("  ML model loaded");
            return new MlClassifier(appSettings, modelPath);
        }
        Console.WriteLine("  No ML model found. Run 'mail-mopper train' to train, or use --skip-ml.");
        Console.WriteLine("  Proceeding with rules only.");
        return null;
    }
}
