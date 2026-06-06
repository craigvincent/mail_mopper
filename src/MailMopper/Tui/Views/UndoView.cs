using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public sealed class UndoView(
    DatabaseService dbService,
    ActionService actionService,
    GmailSession session) : IAppView
{
    private readonly DatabaseService _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
    private readonly ActionService _actionService = actionService ?? throw new ArgumentNullException(nameof(actionService));
    private readonly GmailSession _session = session ?? throw new ArgumentNullException(nameof(session));

    public Action? RequestRender { get; set; }
    public Action? RequestRenderImmediate { get; set; }

    private enum State { Idle, Confirm, Running, Complete, Error }
    private State _state = State.Idle;
    private CancellationTokenSource? _operationCts;
    private int _processed;
    private int _total;
    private string _lastError = "";
    private int _lastUndoneCount;
    private List<SessionInfo> _sessions = [];
    private int _selectedSession = -1;
    private bool _sessionsDirty = true;

    public IRenderable GetContent(int availableHeight)
    {
        return _state switch
        {
            State.Idle => BuildIdleContent(),
            State.Confirm => BuildConfirmContent(),
            State.Running => BuildRunningContent(),
            State.Complete => BuildCompleteContent(),
            State.Error => BuildErrorContent(),
            _ => new Markup(""),
        };
    }

    private IRenderable BuildIdleContent()
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Undo Previous Sessions[/]\n").Centered(),
            new Rule().LeftJustified(),
        };

        if (!_session.IsAuthenticated)
        {
            content.Add(new Markup("\n[red]Not authenticated![/] Return to [bold]Home[/] and press [bold]A[/]"));
            return Align.Center(new Rows(content), VerticalAlignment.Middle);
        }

        if (_sessions.Count == 0)
        {
            content.Add(new Markup("\n  [dim]No previous trash sessions found.[/]"));
        }
        else
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("[bold]#[/]").RightAligned())
                .AddColumn("[bold]Session ID[/]")
                .AddColumn(new TableColumn("[bold]Emails[/]").RightAligned())
                .AddColumn("[bold]Action[/]")
                .AddColumn("[bold]Date[/]");

            var trashSessions = _sessions
                .Where(s => s.Action == "trash")
                .ToList();

            for (var i = 0; i < trashSessions.Count; i++)
            {
                var s = trashSessions[i];
                var prefix = i == _selectedSession ? "[bold cyan]›[/] " : "  ";
                var highlight = i == _selectedSession ? "[bold cyan]" : "";
                var end = i == _selectedSession ? "[/]" : "";
                table.AddRow(
                    $"{prefix}{i + 1}",
                    $"{highlight}{s.SessionId}{end}",
                    $"{highlight}{s.Count:N0}{end}",
                    $"{highlight}{s.Action}{end}",
                    $"{highlight}{s.PerformedAt:yyyy-MM-dd HH:mm}{end}");
            }

            content.Add(table);

            if (_selectedSession >= 0 && _selectedSession < trashSessions.Count)
            {
                var selected = trashSessions[_selectedSession];
                content.Add(new Markup($"\n  Selected: [bold cyan]{selected.SessionId}[/] ({selected.Count:N0} emails)"));
            }
        }

        content.Add(new Markup("\n[bold]Options:[/]"));
        content.Add(new Markup("  [bold]#[/] — Select session by number"));
        content.Add(new Markup("  [bold]U[/] — Undo selected session"));

        return new Rows(content);
    }

    private IRenderable BuildConfirmContent()
    {
        var trashSessions = _sessions
            .Where(s => s.Action == "trash")
            .ToList();
        var selected = _selectedSession >= 0 && _selectedSession < trashSessions.Count
            ? trashSessions[_selectedSession]
            : null;

        var content = new List<IRenderable>
        {
            new Markup("[bold red]Confirm Undo[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  [red]This will restore {selected?.Count ?? 0:N0} emails from trash.[/]"),
            new Markup($"\n  Session: [dim]{selected?.SessionId}[/]"),
            new Markup("\n\n  [bold]Press U to confirm, B to cancel[/]"),
        };

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildRunningContent()
    {
        var pct = _total > 0 ? (int)(_processed * 100.0 / _total) : 0;
        var bar = new string('█', pct / 5) + new string('░', 20 - pct / 5);

        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Restoring from Trash[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  [green]{bar}[/] {pct}%"),
            new Markup($"\n  Processed: [cyan]{_processed:N0}[/] / {_total:N0}"),
            new Markup($"\n\n[dim]Press Esc or Q to cancel...[/]"),
        };

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildCompleteContent()
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Undo Complete[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  [green]✓ Restored {_lastUndoneCount:N0} emails from trash[/]"),
            new Markup("\n[dim]Press any key to continue...[/]"),
        };

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildErrorContent()
    {
        var content = new List<IRenderable>
        {
            new Markup($"[red]Error: {Markup.Escape(_lastError)}[/]"),
            new Markup("\n[dim]Press any key to continue...[/]"),
        };
        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    public string GetFooterHints()
    {
        if (_state == State.Idle)
            return "#: Select  U: Undo";
        if (_state == State.Confirm)
            return "U: Confirm  B: Back";
        return _state == State.Running ? "Esc: Cancel" : "";
    }

    public async Task<ViewCommand> HandleInputAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_state == State.Running)
        {
            if (key.Key == ConsoleKey.Escape || char.ToUpperInvariant(key.KeyChar) == 'Q')
            {
                if (_operationCts != null)
                    await _operationCts.CancelAsync();
                _operationCts?.Dispose();
                _operationCts = null;
                _state = State.Idle;
                return ViewCommand.None;
            }
            return ViewCommand.None;
        }

        if (_state is State.Complete or State.Error)
        {
            _state = State.Idle;
            return ViewCommand.None;
        }

        if (!_session.IsAuthenticated)
            return ViewCommand.None;

        await RefreshSessionsAsync(ct);
        var trashSessions = _sessions.Where(s => s.Action == "trash").ToList();

        var upper = char.ToUpperInvariant(key.KeyChar);

        if (_state == State.Idle)
        {
            if (upper == 'U')
            {
                if (_selectedSession < 0 || _selectedSession >= trashSessions.Count)
                {
                    _lastError = "Please select a session first.";
                    _state = State.Error;
                }
                else
                {
                    _state = State.Confirm;
                }
                return ViewCommand.None;
            }

            if (int.TryParse(upper.ToString(), out var num) && num >= 1 && num <= trashSessions.Count)
            {
                _selectedSession = num - 1;
                return ViewCommand.None;
            }
        }

        if (_state == State.Confirm)
        {
            if (upper == 'U')
            {
                if (_selectedSession >= 0 && _selectedSession < trashSessions.Count)
                {
                    StartUndo(trashSessions[_selectedSession].SessionId, ct);
                }
                return ViewCommand.None;
            }
            if (upper == 'B')
            {
                _state = State.Idle;
                return ViewCommand.None;
            }
        }

        return ViewCommand.None;
    }

    private async Task RefreshSessionsAsync(CancellationToken ct)
    {
        if (!_sessionsDirty)
            return;

        try
        {
            var sessions = await _dbService.GetSessionsAsync(ct);
            _sessions = [.. sessions];
            _sessionsDirty = false;
        }
        catch
        {
            _sessions = [];
        }
    }

    private void StartUndo(string sessionId, CancellationToken appCt)
    {
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var token = _operationCts.Token;
        _state = State.Running;
        _processed = 0;
        _total = _sessions
            .Where(s => s.Action == "trash" && s.SessionId == sessionId)
            .Select(s => s.Count)
            .FirstOrDefault();
        _lastError = "";

        var progress = new Progress<(int processed, int total)>(p =>
        {
            _processed = p.processed;
            _total = p.total;
            RequestRender?.Invoke();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                _lastUndoneCount = await _actionService.UndoSessionAsync(sessionId, progress, token);
                _sessionsDirty = true;
                _state = token.IsCancellationRequested ? State.Idle : State.Complete;
            }
            catch (OperationCanceledException)
            {
                _sessionsDirty = true;
                _state = State.Idle;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _sessionsDirty = true;
                _state = State.Error;
            }
            finally
            {
                _operationCts?.Dispose();
                _operationCts = null;
                RequestRenderImmediate?.Invoke();
            }
        }, token);
    }
}
