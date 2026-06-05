using System.Reflection;
using MailMopper.Commands;
using MailMopper.Data;
using MailMopper.Infrastructure;
using MailMopper.Services;
using MailMopper.Tui;
using MailMopper.Tui.Views;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0";

// Build service collection
var services = new ServiceCollection();

var cancellation = new AppCancellation();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.Error.WriteLine("\nCancellation requested. Finishing current operation...");
    cancellation.Source.Cancel();
};

var appSettings = CommandHelper.LoadSettings();
services.AddSingleton(appSettings);
services.AddSingleton(cancellation);
services.AddSingleton<GmailSession>();
services.AddTransient(_ => new AppDbContext());
services.AddTransient<GmailAuthService>();
services.AddTransient<RuleClassifier>();
services.AddTransient<DatabaseService>();
services.AddTransient<ModelTrainerService>();
services.AddTransient<ReviewService>();
services.AddTransient<ReviewApp>();
services.AddTransient<GmailFetchService>();
services.AddTransient<GmailServices>();
services.AddTransient<IGmailApi, GmailApiWrapper>();
services.AddTransient<ActionService>();

services.AddSingleton<HomeView>();
services.AddSingleton<FetchView>();
services.AddSingleton<ClassifyView>();
services.AddSingleton<ExecuteView>();
services.AddSingleton<UndoView>();
services.AddSingleton<ReviewView>();
services.AddSingleton<MailMopperApp>();

var app = new CommandApp(new TypeRegistrar(services));

app.Configure(config =>
{
    config.SetApplicationName("mail-mopper");
    config.SetApplicationVersion(version);

    config.AddCommand<AuthCommand>("auth")
        .WithDescription("Set up Gmail OAuth2 authentication");

    config.AddCommand<FetchCommand>("fetch")
        .WithDescription("Fetch/sync email metadata from Gmail");

    config.AddCommand<ClassifyCommand>("classify")
        .WithDescription("Run classification pipeline (rules + ML)");

    config.AddCommand<TrainCommand>("train")
        .WithDescription("Train ML classifier on rule-classified emails");

    config.AddCommand<ReviewCommand>("review")
        .WithDescription("Open TUI to review classifications");

    config.AddCommand<ExecuteCommand>("execute")
        .WithDescription("Trash approved emails");

    config.AddCommand<StatsCommand>("stats")
        .WithDescription("Show email statistics and category breakdown");

    config.AddCommand<UndoCommand>("undo")
        .WithDescription("Undo a previous trash session");

    config.AddCommand<RepairDatesCommand>("repair-dates")
        .WithDescription("Fix emails with incorrect dates (re-fetch from Gmail)");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Full pipeline: fetch → classify → review → execute");

    config.AddCommand<ResetCommand>("reset")
        .WithDescription("Wipe all local data and start fresh");

    config.AddCommand<AppCommand>("app")
        .WithDescription("Launch the unified TUI");
});

if (args.Length == 0)
{
    var provider = services.BuildServiceProvider();
    var appInstance = provider.GetRequiredService<MailMopperApp>();
    await appInstance.RunAsync(cancellation.Token);
    return 0;
}

return await app.RunAsync(args);
