using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using MailMopper.Tui.Views;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui;

public enum AppTab { Home, Fetch, Classify, Review, Execute, Undo }

public sealed class MailMopperApp
{
    private readonly GmailSession _session;
    private readonly AppCancellation _cancellation;
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly IAppView[] _views;

    private AppTab _currentTab = AppTab.Home;
    private bool _running = true;
    private bool _showHelp;
    private string _authStatus = "";
    private string _authUrl = "";
    private Task? _authTask;
    private volatile bool _needsRender = true;
    private DateTime _lastRenderTime = DateTime.MinValue;
    private string? _cachedUserEmail;
    private bool _userEmailFetched;
    private AppSnapshot _cachedStats = new(0, 0, 0, 0, 0);
    private bool _statsDirty = true;
    private int _lastWindowWidth;
    private int _lastWindowHeight;

    private sealed record AppSnapshot(int TotalEmails, int Classified, int Approved, int Trashed, long TotalSize);

    private static readonly (AppTab Tab, string Label, string Hotkey)[] _tabs =
    [
        (AppTab.Home, "Home", "1"),
        (AppTab.Fetch, "Fetch", "2"),
        (AppTab.Classify, "Classify", "3"),
        (AppTab.Review, "Review", "4"),
        (AppTab.Execute, "Execute", "5"),
        (AppTab.Undo, "Undo", "6"),
    ];

    public MailMopperApp(
        GmailSession session,
        AppCancellation cancellation,
        AppDbContext db,
        AppSettings settings,
        HomeView homeView,
        FetchView fetchView,
        ClassifyView classifyView,
        ExecuteView executeView,
        UndoView undoView,
        ReviewView reviewView)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _views = new IAppView[(int)AppTab.Undo + 1];
        _views[(int)AppTab.Home] = homeView ?? throw new ArgumentNullException(nameof(homeView));
        _views[(int)AppTab.Fetch] = fetchView ?? throw new ArgumentNullException(nameof(fetchView));
        _views[(int)AppTab.Classify] = classifyView ?? throw new ArgumentNullException(nameof(classifyView));
        _views[(int)AppTab.Review] = reviewView ?? throw new ArgumentNullException(nameof(reviewView));
        _views[(int)AppTab.Execute] = executeView ?? throw new ArgumentNullException(nameof(executeView));
        _views[(int)AppTab.Undo] = undoView ?? throw new ArgumentNullException(nameof(undoView));

        foreach (var view in _views)
        {
            view.RequestRender = RequestRender;
            view.RequestRenderImmediate = RequestRenderImmediate;
        }
    }

    internal void RequestRender()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRenderTime).TotalMilliseconds >= 1000)
            _needsRender = true;
    }

    internal void RequestRenderImmediate() => _needsRender = true;

    private bool TryDetectResize()
    {
        try
        {
            var w = Console.WindowWidth;
            var h = Console.WindowHeight;
            if (w != _lastWindowWidth || h != _lastWindowHeight)
            {
                _lastWindowWidth = w;
                _lastWindowHeight = h;
                return true;
            }
        }
        catch (IOException) { }
        return false;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]The unified TUI requires a real terminal (not piped/redirected).[/]");
            AnsiConsole.MarkupLine("[dim]Use individual commands instead: mail-mopper fetch, mail-mopper classify, etc.[/]");
            return;
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var capturedOutput = new StringWriter();
        Console.SetOut(capturedOutput);
        Console.SetError(capturedOutput);

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            await _db.Database.EnsureCreatedAsync(ct);

            var authService = new GmailAuthService(_settings, _session);
            var restored = await authService.TryRestoreSessionAsync(ct);
            if (restored)
                _needsRender = true;

            while (_running)
            {
                ct.ThrowIfCancellationRequested();

                if (TryDetectResize())
                    _needsRender = true;

                if (_needsRender)
                {
                    _needsRender = false;
                    RenderFrame();
                    _lastRenderTime = DateTime.UtcNow;
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(100);
                while (!_needsRender && !Console.KeyAvailable && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(16, ct);
                    if (TryDetectResize())
                        _needsRender = true;
                }

                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    _needsRender = true;
                    await HandleKeyAsync(key, ct);
                }
            }

            FinalRender();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private void RenderFrame()
    {
        try
        {
            _statsDirty = true;
            AnsiConsole.Clear();
            AnsiConsole.Write(BuildLayout());
        }
        catch (IOException)
        {
        }
    }

    private static void FinalRender()
    {
        try
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold blue]MailMopper[/]");
            AnsiConsole.MarkupLine("[dim]Goodbye![/]");
        }
        catch (IOException)
        {
        }
    }

    private Layout BuildLayout()
    {
        var header = BuildHeader();
        var tabs = BuildTabBar();
        var content = BuildContent();
        var footer = BuildFooter();

        var headerLayout = new Layout("Header").Size(4);
        headerLayout.Update(header);

        var tabsLayout = new Layout("Tabs").Size(1);
        tabsLayout.Update(tabs);

        var footerLayout = new Layout("Footer").Size(3);
        footerLayout.Update(footer);

        var contentLayout = new Layout("Content");
        contentLayout.Update(content);

        return new Layout("Root")
            .SplitRows(headerLayout, tabsLayout, contentLayout, footerLayout);
    }

    private IRenderable BuildHeader()
    {
        var authenticated = _session.IsAuthenticated;
        var emailLabel = authenticated ? "[bold green]●[/] Authenticated" : "[bold red]○[/] Not authenticated";
        var userInfo = authenticated ? GetUserEmail() ?? "Gmail" : "Not connected";

        if (!string.IsNullOrEmpty(_authStatus))
            emailLabel = $"[yellow]⟳ {Markup.Escape(_authStatus)}[/]";

        var logoLines = new[]
        {
            "██▄  ▄██  ▄▄▄  ▄▄ ▄▄      ██▄  ▄██  ▄▄▄  ▄▄▄▄  ▄▄▄▄  ▄▄▄▄▄ ▄▄▄▄  ",
            "██ ▀▀ ██ ██▀██ ██ ██      ██ ▀▀ ██ ██▀██ ██▄█▀ ██▄█▀ ██▄▄  ██▄█▄ ",
            "██    ██ ██▀██ ██ ██▄▄▄   ██    ██ ▀███▀ ██    ██    ██▄▄▄ ██ ██ ",
        };

        var table = new Table()
            .HideHeaders()
            .NoBorder()
            .Expand()
            .AddColumn(new TableColumn("L"))
            .AddColumn(new TableColumn("R").RightAligned());

        table.AddRow(
            new Markup($"[bold blue]{logoLines[0]}[/]"),
            new Markup(""));
        table.AddRow(
            new Markup($"[bold blue]{logoLines[1]}[/]"),
            new Markup($"[dim]{Markup.Escape(userInfo)}  {emailLabel}[/]"));
        table.AddRow(
            new Markup($"[bold blue]{logoLines[2]}[/]"),
            new Markup(GetStatsLine()));

        return new Rows(table, new Rule());
    }

    private string GetUserEmail()
    {
        if (_userEmailFetched)
            return _cachedUserEmail ?? "Gmail";

        try
        {
            var profile = _session.Service?.Users.GetProfile("me").ExecuteAsync(CancellationToken.None).Result;
            _cachedUserEmail = profile?.EmailAddress;
            _userEmailFetched = true;
            return _cachedUserEmail ?? "Gmail";
        }
        catch
        {
            _cachedUserEmail = null;
            _userEmailFetched = true;
            return "Gmail";
        }
    }

    private string GetStatsLine()
    {
        if (_statsDirty)
        {
            try
            {
                var totalEmails = _db.Emails.Count();
                var classified = _db.Classifications.Select(c => c.MessageId).Distinct().Count();
                var approved = _db.Classifications.Count(c => c.ReviewDecision == Models.ReviewDecision.ApproveTrash);
                var trashed = _db.Actions.Count(a => a.Action == "trash");
                var totalSize = _db.Emails.Sum(e => e.SizeEstimate);
                _cachedStats = new AppSnapshot(totalEmails, classified, approved, trashed, totalSize);
                _statsDirty = false;
            }
            catch
            {
                _cachedStats = new AppSnapshot(0, 0, 0, 0, 0);
            }
        }

        var s = _cachedStats;
        return $"{s.TotalEmails:N0} emails  │  {s.Classified:N0} classified  │  {s.Approved:N0} approved  │  {s.Trashed:N0} trashed  │  {FormatSize(s.TotalSize)}";
    }

    private IRenderable BuildTabBar()
    {
        var cols = new List<IRenderable>();
        foreach (var (tab, label, hotkey) in _tabs)
        {
            var isActive = tab == _currentTab;
            var style = isActive ? "[bold invert]" : "[dim]";
            var suffix = "[/]";
            cols.Add(new Markup($"{style} [[{hotkey}]] {label} {suffix}"));
        }
        return new Columns(cols).Padding(1, 1).Expand();
    }

    private IRenderable BuildContent()
    {
        if (!string.IsNullOrEmpty(_authStatus))
        {
            var rows = new List<IRenderable>
            {
                new Markup($"[yellow]⟳ {Markup.Escape(_authStatus)}[/]"),
            };

            if (!string.IsNullOrEmpty(_authUrl))
            {
                rows.Add(new Markup(""));
                rows.Add(new Markup("  [bold cyan]Click this URL to authenticate:[/]"));
                rows.Add(new Markup($"  [underline blue]{_authUrl}[/]"));
                rows.Add(new Markup(""));
                rows.Add(new Markup("  [dim]If the link doesn't work, copy and paste it into your browser.[/]"));
                rows.Add(new Markup("  [dim]The URL is also printed to the console for copying.[/]"));
            }

            return Align.Center(new Rows(rows), VerticalAlignment.Middle);
        }

        if (_showHelp)
            return HelpView.GetContent();

        var view = _views[(int)_currentTab];
        var contentHeight = Console.WindowHeight - 8;
        return view.GetContent(contentHeight);
    }

    private IRenderable BuildFooter()
    {
        var hints = new List<string> { "[bold]Tab[/]/[bold]Shift+Tab[/]:Cycle tabs", "[bold]Q[/]:Quit", "[bold]F1[/]:Help" };

        var view = _views[(int)_currentTab];
        var viewHints = view.GetFooterHints();

        var rows = new List<IRenderable>
        {
            new Rule(),
            new Markup($"[dim]{string.Join("  │  ", hints)}[/]"),
        };

        if (!string.IsNullOrEmpty(viewHints))
            rows.Add(new Markup($"[dim]{viewHints}[/]"));

        return new Rows(rows);
    }

    private async Task HandleKeyAsync(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (_showHelp)
        {
            if (key.Key is ConsoleKey.F1 || key.KeyChar is '?' or '/')
            {
                _showHelp = false;
                return;
            }
            _showHelp = false;
            return;
        }

        if (key.Key is ConsoleKey.F1 || key.KeyChar is '?' or '/')
        {
            _showHelp = true;
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.Tab:
                _currentTab = key.Modifiers.HasFlag(ConsoleModifiers.Shift)
                    ? PreviousTab()
                    : NextTab();
                return;
        }

        if (char.ToUpperInvariant(key.KeyChar) == 'Q')
        {
            _running = false;
            return;
        }

        var view = _views[(int)_currentTab];
        var command = await view.HandleInputAsync(key, ct);

        switch (command)
        {
            case ViewCommand.Quit:
                _running = false;
                break;
            case ViewCommand.GoToHome:
                _currentTab = AppTab.Home;
                break;
            case ViewCommand.GoToFetch:
                _currentTab = AppTab.Fetch;
                break;
            case ViewCommand.GoToClassify:
                _currentTab = AppTab.Classify;
                break;
            case ViewCommand.GoToReview:
                _currentTab = AppTab.Review;
                break;
            case ViewCommand.GoToExecute:
                _currentTab = AppTab.Execute;
                break;
            case ViewCommand.GoToUndo:
                _currentTab = AppTab.Undo;
                break;
            case ViewCommand.OpenHelp:
                _showHelp = true;
                break;
            case ViewCommand.RequestAuth:
                await HandleAuthAsync(ct);
                break;
            case ViewCommand.RequestLogout:
                HandleLogout();
                break;
            case ViewCommand.RequestClassify:
                _currentTab = AppTab.Classify;
                break;
            case ViewCommand.MarkDirty:
                break;
        }
    }

    private async Task HandleAuthAsync(CancellationToken ct)
    {
        if (_authTask != null && !_authTask.IsCompleted)
            return;

        _authUrl = "";
        _authStatus = "Generating authentication URL...";
        _needsRender = true;
        var authService = new GmailAuthService(_settings, _session);

        _authTask = Task.Run(async () =>
        {
            try
            {
                await authService.AuthenticateAsync(ct, onAuthUrl: url =>
                {
                    _authUrl = url;
                    _authStatus = "Authentication in progress — open the URL below in your browser:";
                    _needsRender = true;
                });
                _authStatus = "[green]✓ Authenticated![/]";
                _authUrl = "";
                _needsRender = true;
            }
            catch (Exception ex)
            {
                _authStatus = $"[red]Auth failed: {ex.Message}[/]";
                _authUrl = "";
                _needsRender = true;
            }
            await Task.Delay(2000, ct);
            _authStatus = "";
            _needsRender = true;
        }, ct);
    }

    private void HandleLogout()
    {
        var authService = new GmailAuthService(_settings, _session);
        authService.Logout();
        _needsRender = true;
    }

    private AppTab NextTab()
    {
        var current = (int)_currentTab;
        var count = _tabs.Length;
        return (AppTab)((current + 1) % count);
    }

    private AppTab PreviousTab()
    {
        var current = (int)_currentTab;
        var count = _tabs.Length;
        return (AppTab)((current - 1 + count) % count);
    }

    private static string FormatSize(long bytes) => ReviewService.FormatSize(bytes);
}
