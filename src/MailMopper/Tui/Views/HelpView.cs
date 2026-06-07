using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public static class HelpView
{
    public static IRenderable GetContent()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Key[/]")
            .AddColumn("[bold]Action[/]");

        table.AddRow("[bold]Tab[/] / [bold]Shift+Tab[/]", "Cycle tabs forward/backward");
        table.AddRow("[bold]Q[/]", "Quit MailMopper");
        table.AddRow("[bold]F1[/] / [bold]?[/]", "Toggle this help screen");

        table.AddRow("", "");

        table.AddRow("[bold]Home tab[/]", "");
        table.AddRow("  [bold]A[/]", "Authenticate with Gmail (if needed)");
        table.AddRow("  [bold]F[/]", "Jump to Fetch tab");
        table.AddRow("  [bold]C[/]", "Jump to Classify tab");
        table.AddRow("  [bold]R[/]", "Jump to Review tab");
        table.AddRow("  [bold]E[/]", "Jump to Execute tab");

        table.AddRow("", "");

        table.AddRow("[bold]Review tab[/]", "3-level drill-down: Dashboard → Category → Sender");
        table.AddRow("  [bold]#[/]", "Open category / Select sender");
        table.AddRow("  [bold]T[/]", "Trash all emails from sender");
        table.AddRow("  [bold]K[/]", "Keep all emails from sender");
        table.AddRow("  [bold]W[/]", "Whitelist sender domain");
        table.AddRow("  [bold]TA[/] / [bold]KA[/]", "Trash all / Keep all in category");
        table.AddRow("  [bold]H[/]", "Toggle show/hide decided senders");
        table.AddRow("  [bold]N[/] / [bold]P[/]", "Next / Previous page");
        table.AddRow("  [bold]Y[/]", "Filter by year");
        table.AddRow("  [bold]B[/]", "Go back to previous level");
        table.AddRow("  [bold]Esc[/]", "Go back");

        table.AddRow("", "");

        table.AddRow("[bold]Fetch tab[/]", "");
        table.AddRow("  [bold]F[/]", "Full fetch (download all emails)");
        table.AddRow("  [bold]I[/]", "Incremental fetch (new emails only)");

        table.AddRow("", "");

        table.AddRow("[bold]Classify tab[/]", "");
        table.AddRow("  [bold]C[/]", "Classify (rules + ML)");
        table.AddRow("  [bold]T[/]", "Train ML classifier on rule-labeled data");
        table.AddRow("  [bold]R[/]", "Re-classify with rules only (skip ML)");

        table.AddRow("", "");

        table.AddRow("[bold]Execute tab[/]", "");
        table.AddRow("  [bold]D[/]", "Dry-run preview (show what would be trashed)");
        table.AddRow("  [bold]E[/]", "Execute trash on approved emails");

        table.AddRow("", "");

        table.AddRow("[bold]Undo tab[/]", "");
        table.AddRow("  [bold]#[/]", "Select session to undo");
        table.AddRow("  [bold]U[/]", "Execute undo for selected session");

        return Align.Center(
            new Rows(
                new Markup("[bold blue]Help — Keybindings[/]\n").Centered(),
                table),
            VerticalAlignment.Middle);
    }
}
