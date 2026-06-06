using MailMopper.Data;
using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public sealed class FetchView : IAppView
{
    private readonly GmailFetchService _fetchService;
    private readonly AppDbContext _db;
    private readonly GmailSession _session;

    public Action? RequestRender { get; set; }
    public Action? RequestRenderImmediate { get; set; }

    private enum State { Idle, Running, Complete, Error }
    private State _state = State.Idle;
    private CancellationTokenSource? _operationCts;
    private int _fetched;
    private int _total;
    private string _status = "";
    private string _lastError = "";
    private int _lastFetchedCount;
    private bool _syncDirty = true;
    private (string lastSync, int totalEmails, int previouslySynced) _cachedSyncState = ("Loading...", 0, 0);

    public FetchView(GmailFetchService fetchService, AppDbContext db, GmailSession session)
    {
        _fetchService = fetchService ?? throw new ArgumentNullException(nameof(fetchService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public IRenderable GetContent(int availableHeight)
    {
        var content = new List<IRenderable>();

        switch (_state)
        {
            case State.Idle:
                return BuildIdleContent(availableHeight);
            case State.Running:
                return BuildRunningContent(availableHeight);
            case State.Complete:
                return BuildCompleteContent(availableHeight);
            case State.Error:
                content.Add(new Markup($"[red]Error: {Markup.Escape(_lastError)}[/]"));
                content.Add(new Markup("\n[dim]Press any key to continue...[/]"));
                return Align.Center(new Rows(content), VerticalAlignment.Middle);
        }

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildIdleContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Email Fetch[/]\n").Centered(),
            new Rule().LeftJustified(),
        };

        if (!_session.IsAuthenticated)
        {
            content.Add(new Markup("\n[red]Not authenticated![/] Return to [bold]Home[/] and press [bold]A[/]"));
            return Align.Center(new Rows(content), VerticalAlignment.Middle);
        }

        var syncState = GetSyncState();
        content.Add(new Markup($"\n  Last sync: [cyan]{syncState.lastSync}[/]"));
        content.Add(new Markup($"  Total emails in database: [cyan]{syncState.totalEmails:N0}[/]"));
        content.Add(new Markup($"  Previous synced count: [cyan]{syncState.previouslySynced:N0}[/]"));

        if (_lastFetchedCount > 0)
            content.Add(new Markup($"\n  [green]Last fetch: {_lastFetchedCount:N0} new emails[/]"));

        content.Add(new Markup("\n[bold]Options:[/]"));
        content.Add(new Markup("  [bold]F[/] — Full fetch (re-scan all emails)"));
        content.Add(new Markup("  [bold]I[/] — Incremental fetch (new emails only)"));

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildRunningContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Fetching Emails[/]\n").Centered(),
            new Rule().LeftJustified(),
        };

        var pct = _total > 0 ? (int)(_fetched * 100.0 / _total) : 0;
        var bar = new string('█', pct / 5) + new string('░', 20 - pct / 5);
        content.Add(new Markup($"\n  [green]{bar}[/] {pct}%"));
        content.Add(new Markup($"\n  Fetched: [cyan]{_fetched:N0}[/] / {_total:N0}"));
        content.Add(new Markup($"  {Markup.Escape(_status)}"));

        content.Add(new Markup("\n\n[dim]Press Esc or Q to cancel...[/]"));

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildCompleteContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Fetch Complete[/]\n").Centered(),
            new Rule().LeftJustified(),
        };

        if (_lastFetchedCount > 0)
            content.Add(new Markup($"\n  [green]✓ Fetched {_lastFetchedCount:N0} new emails[/]"));
        else
            content.Add(new Markup("\n  [yellow]No new emails to fetch[/]"));

        content.Add(new Markup("\n[dim]Press any key to continue...[/]"));

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    public string GetFooterHints()
    {
        if (_state == State.Idle)
            return "F: Full  I: Incremental";
        if (_state == State.Running)
            return "Esc: Cancel";
        return "";
    }

    public async Task<ViewCommand> HandleInputAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_state == State.Running)
        {
            if (key.Key == ConsoleKey.Escape || char.ToUpperInvariant(key.KeyChar) == 'Q')
            {
                _operationCts?.Cancel();
                _operationCts?.Dispose();
                _operationCts = null;
                _state = State.Idle;
                return ViewCommand.None;
            }
            return ViewCommand.None;
        }

        if (_state == State.Complete || _state == State.Error)
        {
            _state = State.Idle;
            return ViewCommand.None;
        }

        if (!_session.IsAuthenticated)
        {
            if (char.ToUpperInvariant(key.KeyChar) == 'A')
                return ViewCommand.RequestAuth;
            return ViewCommand.None;
        }

        var upper = char.ToUpperInvariant(key.KeyChar);
        if (upper == 'F')
        {
            StartFetch(full: true, ct);
            return ViewCommand.None;
        }
        if (upper == 'I')
        {
            StartFetch(full: false, ct);
            return ViewCommand.None;
        }

        return ViewCommand.None;
    }

    private void StartFetch(bool full, CancellationToken appCt)
    {
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var token = _operationCts.Token;
        _state = State.Running;
        _fetched = 0;
        _total = 1;
        _status = full ? "Starting full fetch..." : "Starting incremental fetch...";
        _lastError = "";

        var progress = new Progress<(int fetched, int total)>(p =>
        {
            _fetched = p.fetched;
            _total = p.total;
            RequestRender?.Invoke();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                int count;
                if (full)
                    count = await _fetchService.FetchAllAsync(progress, token);
                else
                    count = await _fetchService.FetchIncrementalAsync(progress, token);

                _lastFetchedCount = count;
                _syncDirty = true;
                _state = token.IsCancellationRequested ? State.Idle : State.Complete;
            }
            catch (OperationCanceledException)
            {
                _state = State.Idle;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
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

    private (string lastSync, int totalEmails, int previouslySynced) GetSyncState()
    {
        if (!_syncDirty)
            return _cachedSyncState;

        try
        {
            var sync = _db.SyncStates.FirstOrDefault(s => s.Key == "default");
            var total = _db.Emails.Count();
            var count = sync?.TotalMessagesFetched ?? 0;
            var lastSync = sync?.LastSyncAt?.ToString("yyyy-MM-dd HH:mm") ?? "Never";
            _cachedSyncState = (lastSync, total, count);
            _syncDirty = false;
        }
        catch
        {
            _cachedSyncState = ("Unknown", 0, 0);
        }

        return _cachedSyncState;
    }
}
