using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public enum ViewCommand
{
    None,
    Quit,
    GoToHome,
    GoToFetch,
    GoToClassify,
    GoToReview,
    GoToExecute,
    GoToUndo,
    OpenHelp,
    RequestAuth,
    RequestClassify,
    RequestLogout,
    MarkDirty,
}

public interface IAppView
{
    Action? RequestRender { get; set; }
    Action? RequestRenderImmediate { get; set; }
    IRenderable GetContent(int availableHeight);
    string GetFooterHints();
    Task<ViewCommand> HandleInputAsync(ConsoleKeyInfo key, CancellationToken ct);
}
