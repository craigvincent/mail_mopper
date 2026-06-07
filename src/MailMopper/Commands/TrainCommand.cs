using MailMopper.Data;
using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class TrainCommand(ModelTrainerService trainer, AppDbContext dbContext, AppCancellation cancellation) : AsyncCommand
{
    private readonly ModelTrainerService _trainer = trainer ?? throw new ArgumentNullException(nameof(trainer));
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            var ct = _cancellation.Token;

            AnsiConsole.MarkupLine("[bold blue]=== Train Email Classifier ===[/]");
            AnsiConsole.WriteLine();

            await _dbContext.Database.EnsureCreatedAsync(ct);

            var modelPath = ModelTrainerService.GetDefaultModelPath();

            if (File.Exists(modelPath))
            {
                var fileInfo = new FileInfo(modelPath);
                AnsiConsole.MarkupLine($"[yellow]Existing model found ({fileInfo.Length / 1024.0 / 1024.0:F1} MB, last trained {fileInfo.LastWriteTime:g})[/]");
                if (!AnsiConsole.Confirm("Retrain and overwrite?", defaultValue: true))
                    return 0;
            }

            var result = await _trainer.TrainAsync(
                modelPath,
                onStatus: msg => AnsiConsole.MarkupLine($"  {Markup.Escape(msg)}"),
                ct);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]✓ Training complete![/]");
            AnsiConsole.WriteLine();

            // Summary table
            var table = new Table();
            table.AddColumn("[bold]Metric[/]");
            table.AddColumn("[bold]Value[/]");
            table.AddRow("Training samples", $"{result.TrainingSamples:N0}");
            table.AddRow("Categories", string.Join(", ", result.Categories));
            table.AddRow("Accuracy", $"{result.Accuracy:P1}");
            table.AddRow("Log-loss", $"{result.LogLoss:F3}");
            table.AddRow("Model size", $"{result.ModelSizeBytes / 1024.0 / 1024.0:F1} MB");
            table.AddRow("Model path", result.ModelPath);
            AnsiConsole.Write(table);

            // Per-class metrics
            AnsiConsole.WriteLine();
            var classTable = new Table();
            classTable.AddColumn("[bold]Category[/]");
            classTable.AddColumn("[bold]Precision[/]", c => c.RightAligned());
            classTable.AddColumn("[bold]Recall[/]", c => c.RightAligned());
            classTable.AddColumn("[bold]F1[/]", c => c.RightAligned());

            foreach (var (name, metrics) in result.PerClassMetrics.OrderBy(x => x.Key))
            {
                classTable.AddRow(name, $"{metrics.Precision:P1}", $"{metrics.Recall:P1}", $"{metrics.F1:P1}");
            }

            AnsiConsole.Write(classTable);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Run 'mail-mopper classify' to classify remaining emails using the trained model.[/]");

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during training: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
