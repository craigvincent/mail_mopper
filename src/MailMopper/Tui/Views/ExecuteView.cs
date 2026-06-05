using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public sealed class ExecuteView : IAppView
{
    private readonly ActionService _actionService;
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly GmailSession _session;
    private readonly GmailAuthService _authService;

    public Action? RequestRender { get; set; }
    public Action? RequestRenderImmediate { get; set; }

    private enum State { Idle, Preview, Confirm, Running, Complete, Error }
    private State _state = State.Idle;
    private Task? _activeTask;
    private CancellationTokenSource? _operationCts;
    private int _processed;
    private int _total;
    private string _lastError = "";
    private ActionSummary? _lastResult;
    private int _pendingCount;
    private long _pendingSize;
    private bool _pendingDirty = true;

    public ExecuteView(
        ActionService actionService,
        AppDbContext db,
        AppSettings settings,
        GmailSession session,
        GmailAuthService authService)
    {
        _actionService = actionService ?? throw new ArgumentNullException(nameof(actionService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    public IRenderable GetContent(int availableHeight)
    {
        return _state switch
        {
            State.Idle => BuildIdleContent(availableHeight),
            State.Preview => BuildPreviewContent(availableHeight),
            State.Confirm => BuildConfirmContent(availableHeight),
            State.Running => BuildRunningContent(availableHeight),
            State.Complete => BuildCompleteContent(availableHeight),
            State.Error => BuildErrorContent(availableHeight),
            _ => new Markup("")
        };
    }

    private IRenderable BuildIdleContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Execute Actions[/]\n").Centered(),
            new Rule().LeftJustified(),
        };

        if (!_session.IsAuthenticated)
        {
            content.Add(new Markup("\n[red]Not authenticated![/] Return to [bold]Home[/] and press [bold]A[/]"));
            return Align.Center(new Rows(content), VerticalAlignment.Middle);
        }

        RefreshPendingCounts();
        content.Add(new Markup($"\n  Emails approved for trash: [red]{_pendingCount:N0}[/]"));
        content.Add(new Markup($"  Estimated space to free: [cyan]{FormatSize(_pendingSize)}[/]"));

        if (_lastResult != null)
        {
            content.Add(new Markup($"\n  [green]Last execution: {_lastResult.EmailsTrashed:N0} trashed, {_lastResult.Errors} errors[/]"));
            content.Add(new Markup($"  Freed: [cyan]{FormatSize(_lastResult.EstimatedSpaceFreed)}[/]"));
            content.Add(new Markup($"  Session: [dim]{_lastResult.SessionId}[/]"));
        }

        content.Add(new Markup("\n[bold]Options:[/]"));
        content.Add(new Markup("  [bold]D[/] — Dry-run preview (see what would be trashed)"));
        content.Add(new Markup("  [bold]E[/] — Execute trash (move approved emails to trash)"));

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildPreviewContent(int availableHeight)
    {
        RefreshPendingCounts();
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Dry-Run Preview[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  Would trash [red]{_pendingCount:N0}[/] emails ({FormatSize(_pendingSize)})"),
            new Markup($"\n  This is a dry-run — no emails will be affected."),
            new Markup("\n\n  [bold]Proceed with real execution?[/]"),
            new Markup("  [bold]E[/] — Execute    [bold]B[/] — Back"),
        };

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildConfirmContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold red]Confirm Execution[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  [red]This will trash {_pendingCount:N0} emails ({FormatSize(_pendingSize)})[/]"),
            new Markup($"\n  This action is reversible via the [bold]Undo[/] tab for 30 days."),
            new Markup("\n\n  [bold]Press E to confirm, B to cancel[/]"),
        };

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildRunningContent(int availableHeight)
    {
        var pct = _total > 0 ? (int)(_processed * 100.0 / _total) : 0;
        var bar = new string('█', pct / 5) + new string('░', 20 - pct / 5);

        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Trashing Emails[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  [red]{bar}[/] {pct}%"),
            new Markup($"\n  Processed: [cyan]{_processed:N0}[/] / {_total:N0}"),
            new Markup($"\n\n[dim]Press Esc or Q to cancel...[/]"),
        };

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildCompleteContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Execution Complete[/]\n").Centered(),
            new Rule().LeftJustified(),
        };

        if (_lastResult != null)
        {
            content.Add(new Markup($"\n  [green]✓ Trashed {_lastResult.EmailsTrashed:N0} emails[/]"));
            content.Add(new Markup($"  Errors: [yellow]{_lastResult.Errors}[/]"));
            content.Add(new Markup($"  Space freed: [cyan]{FormatSize(_lastResult.EstimatedSpaceFreed)}[/]"));
            content.Add(new Markup($"  Session ID: [dim]{_lastResult.SessionId}[/]"));
        }

        content.Add(new Markup("\n[dim]Press any key to continue...[/]"));

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildErrorContent(int availableHeight)
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
            return "D: Dry-run  E: Execute";
        if (_state == State.Preview || _state == State.Confirm)
            return "E: Execute  B: Back";
        if (_state == State.Running)
            return "Esc: Cancel";
        return "";
    }

    public Task<ViewCommand> HandleInputAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_state == State.Running)
        {
            if (key.Key == ConsoleKey.Escape || char.ToUpperInvariant(key.KeyChar) == 'Q')
            {
                _operationCts?.Cancel();
                _state = State.Idle;
                return Task.FromResult(ViewCommand.None);
            }
            return Task.FromResult(ViewCommand.None);
        }

        if (_state == State.Complete || _state == State.Error)
        {
            _state = State.Idle;
            return Task.FromResult(ViewCommand.None);
        }

        if (!_session.IsAuthenticated)
            return Task.FromResult(ViewCommand.None);

        var upper = char.ToUpperInvariant(key.KeyChar);

        if (_state == State.Idle)
        {
            if (upper == 'D')
            {
                RefreshPendingCounts();
                if (_pendingCount == 0)
                {
                    _lastError = "No emails approved for trash.";
                    _state = State.Error;
                }
                else
                {
                    _state = State.Preview;
                }
                return Task.FromResult(ViewCommand.None);
            }
            if (upper == 'E')
            {
                RefreshPendingCounts();
                if (_pendingCount == 0)
                {
                    _lastError = "No emails approved for trash.";
                    _state = State.Error;
                }
                else
                {
                    _state = State.Confirm;
                }
                return Task.FromResult(ViewCommand.None);
            }
        }

        if (_state == State.Preview)
        {
            if (upper == 'E')
            {
                _state = State.Confirm;
                return Task.FromResult(ViewCommand.None);
            }
            if (upper == 'B')
            {
                _state = State.Idle;
                return Task.FromResult(ViewCommand.None);
            }
        }

        if (_state == State.Confirm)
        {
            if (upper == 'E')
            {
                StartExecute(ct);
                return Task.FromResult(ViewCommand.None);
            }
            if (upper == 'B')
            {
                _state = State.Idle;
                return Task.FromResult(ViewCommand.None);
            }
        }

        return Task.FromResult(ViewCommand.None);
    }

    private void RefreshPendingCounts()
    {
        if (!_pendingDirty)
            return;

        try
        {
            var query = from c in _db.Classifications
                        join e in _db.Emails on c.MessageId equals e.MessageId
                        where c.ReviewDecision == Models.ReviewDecision.ApproveTrash
                        select e.SizeEstimate;

            var sizes = query.ToList();
            _pendingCount = sizes.Count;
            _pendingSize = sizes.Sum();
            _pendingDirty = false;
        }
        catch
        {
            _pendingCount = 0;
            _pendingSize = 0;
        }
    }

    private void StartExecute(CancellationToken appCt)
    {
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var token = _operationCts.Token;
        _state = State.Running;
        _processed = 0;
        _total = _pendingCount;
        _lastError = "";

        var progress = new Progress<(int processed, int total)>(p =>
        {
            _processed = p.processed;
            _total = p.total;
            RequestRender?.Invoke();
        });

        _activeTask = Task.Run(async () =>
        {
            try
            {
                _lastResult = await _actionService.TrashApprovedAsync(false, progress, token);
                _pendingDirty = true;
                _state = token.IsCancellationRequested ? State.Idle : State.Complete;
                RequestRenderImmediate?.Invoke();
            }
            catch (OperationCanceledException)
            {
                _pendingDirty = true;
                _state = State.Idle;
                RequestRenderImmediate?.Invoke();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _pendingDirty = true;
                _state = State.Error;
                RequestRenderImmediate?.Invoke();
            }
        }, token);
    }

    private static string FormatSize(long bytes) => ReviewService.FormatSize(bytes);
}
