using MailMopper.Config;
using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class ResetCommand(AppSettings appSettings, AppCancellation cancellation) : AsyncCommand
{
    private readonly AppSettings _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var ct = _cancellation.Token;

        AnsiConsole.MarkupLine("[bold red]Wipe All Local Data[/]");
        AnsiConsole.MarkupLine("[dim]This will permanently delete all locally stored data and start fresh.[/]");
        AnsiConsole.WriteLine();

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailMopper", "mail_mopper.db");

        var modelPath = ModelTrainerService.GetDefaultModelPath();

        var tokenPath = _appSettings.Gmail.TokenPath;

        var items = new List<(string Label, string Path)>
        {
            ("Email database (emails, classifications, decisions, whitelist)", dbPath),
            ("ML model file", modelPath),
            ("Gmail auth token (forces re-authentication)", tokenPath),
        };

        var table = new Table().Border(TableBorder.Minimal).AddColumn("Item").AddColumn("Path");
        foreach (var item in items)
        {
            var exists = File.Exists(item.Path) || Directory.Exists(item.Path);
            var marker = exists ? "[red]●[/]" : "[dim]○[/]";
            table.AddRow($"{marker} {item.Label}", Markup.Escape(item.Path));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("[yellow]Are you sure you want to delete everything above?[/]", defaultValue: false))
        {
            AnsiConsole.MarkupLine("[green]Reset cancelled.[/]");
            return 1;
        }

        var (deleted, failed) = await AnsiConsole.Status()
            .StartAsync("Deleting local data...", async _ =>
            {
                int d = 0, f = 0;
                foreach (var (label, path) in items)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                            d++;
                        }
                        else if (File.Exists(path))
                        {
                            File.Delete(path);
                            d++;
                        }
                    }
                    catch (Exception ex)
                    {
                        f++;
                        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(label)}: {Markup.Escape(ex.Message)}");
                    }
                }
                return (d, f);
            });

        AnsiConsole.WriteLine();
        if (failed == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Deleted {deleted} item(s). All local data has been wiped.");
            AnsiConsole.MarkupLine("[dim]Run [yellow]auth[/] then [yellow]fetch[/] to start fresh.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] Deleted {deleted} item(s), {failed} failed. You may need to manually remove some files.");
        }

        return failed > 0 ? 1 : 0;
    }
}
