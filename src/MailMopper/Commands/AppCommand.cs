using MailMopper.Services;
using Spectre.Console.Cli;

namespace MailMopper.Commands;

public class AppCommand(Tui.MailMopperApp app, AppCancellation cancellation) : AsyncCommand
{
    private readonly Tui.MailMopperApp _app = app ?? throw new ArgumentNullException(nameof(app));
    private readonly AppCancellation _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        await _app.RunAsync(_cancellation.Token);
        return 0;
    }
}
