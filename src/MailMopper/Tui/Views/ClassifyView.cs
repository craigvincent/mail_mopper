using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MailMopper.Tui.Views;

public sealed class ClassifyView : IAppView
{
    private readonly RuleClassifier _ruleClassifier;
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly ModelTrainerService _trainerService;
    private readonly GmailSession _session;

    public Action? RequestRender { get; set; }
    public Action? RequestRenderImmediate { get; set; }

    private enum State { Idle, Running, Complete, Error }
    private State _state = State.Idle;
    private CancellationTokenSource? _operationCts;
    private string _status = "";
    private string _lastError = "";
    private TrainingResult? _lastTrainResult;
    private bool _classifyStatsDirty = true;
    private (int classified, int unclassified, int ruleCount, List<(string category, int count, double pct)> categories) _cachedClassifyStats = (0, 0, 0, []);

    public ClassifyView(
        RuleClassifier ruleClassifier,
        AppDbContext db,
        AppSettings settings,
        ModelTrainerService trainerService,
        GmailSession session)
    {
        _ruleClassifier = ruleClassifier ?? throw new ArgumentNullException(nameof(ruleClassifier));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _trainerService = trainerService ?? throw new ArgumentNullException(nameof(trainerService));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public IRenderable GetContent(int availableHeight)
    {
        return _state switch
        {
            State.Idle => BuildIdleContent(availableHeight),
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
            new Markup("[bold blue]Email Classification[/]\n").Centered(),
            new Rule().LeftJustified(),
        };

        if (!_session.IsAuthenticated)
        {
            content.Add(new Markup("\n[red]Not authenticated![/] Return to [bold]Home[/] and press [bold]A[/]"));
            return Align.Center(new Rows(content), VerticalAlignment.Middle);
        }

        var stats = GetClassificationStats();
        content.Add(new Markup($"\n  Total classified: [cyan]{stats.classified:N0}[/]"));
        content.Add(new Markup($"  Unclassified (need classification): [yellow]{stats.unclassified:N0}[/]"));
        content.Add(new Markup($"  Rules loaded: [cyan]{stats.ruleCount}[/]"));

        if (stats.unclassified > 0)
            content.Add(new Markup($"\n  [yellow]{stats.unclassified:N0} emails waiting for classification[/]"));

        if (stats.classified > 0)
        {
            content.Add(new Markup("\n[bold]Category Breakdown:[/]"));
            foreach (var cat in stats.categories)
                content.Add(new Markup($"  {cat.category}: [cyan]{cat.count:N0}[/] ({cat.pct:F1}%)"));
        }

        var modelPath = _settings.Ml?.ModelPath ?? ModelTrainerService.GetDefaultModelPath();
        var hasModel = File.Exists(modelPath);
        content.Add(new Markup($"\n  ML Model: [cyan]{(hasModel ? "Available" : "Not trained")}[/]"));

        if (_lastTrainResult != null)
        {
            content.Add(new Markup($"\n  [green]Last training: {_lastTrainResult.TrainingSamples:N0} samples, Acc={_lastTrainResult.Accuracy:P1}[/]"));
        }

        content.Add(new Markup("\n[bold]Options:[/]"));
        content.Add(new Markup("  [bold]C[/] — Classify (rules + ML)"));
        content.Add(new Markup("  [bold]R[/] — Re-classify (rules only, skip ML)"));
        content.Add(new Markup("  [bold]T[/] — Train ML model on rule-labeled data (need 100+ samples)"));

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildRunningContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Classification in Progress[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  {Markup.Escape(_status)}"),
            new Markup("\n  [dim]Please wait...[/]"),
            new Markup("\n\n[dim]Press Esc or Q to cancel...[/]"),
        };

        return Align.Center(new Rows(content), VerticalAlignment.Middle);
    }

    private IRenderable BuildCompleteContent(int availableHeight)
    {
        var content = new List<IRenderable>
        {
            new Markup("[bold blue]Classification Complete[/]\n").Centered(),
            new Rule().LeftJustified(),
            new Markup($"\n  [green]✓ {Markup.Escape(_status)}[/]"),
        };

        if (_lastTrainResult != null)
        {
            content.Add(new Markup($"\n  [bold]Training Results:[/]"));
            content.Add(new Markup($"  Samples: [cyan]{_lastTrainResult.TrainingSamples:N0}[/]"));
            content.Add(new Markup($"  Accuracy: [cyan]{_lastTrainResult.Accuracy:P1}[/]"));
            content.Add(new Markup($"  Log Loss: [cyan]{_lastTrainResult.LogLoss:F3}[/]"));
            content.Add(new Markup($"  Categories: [cyan]{string.Join(", ", _lastTrainResult.Categories)}[/]"));
            content.Add(new Markup($"  Model size: [cyan]{_lastTrainResult.ModelSizeBytes / 1024.0 / 1024.0:F1} MB[/]"));
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
            return "C: Classify  R: Rules only  T: Train model";
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
            return ViewCommand.None;

        var upper = char.ToUpperInvariant(key.KeyChar);
        if (upper == 'C')
        {
            StartClassify(skipMl: false, ct);
            return ViewCommand.None;
        }
        if (upper == 'R')
        {
            StartClassify(skipMl: true, ct);
            return ViewCommand.None;
        }
        if (upper == 'T')
        {
            StartTrain(ct);
            return ViewCommand.None;
        }

        return ViewCommand.None;
    }

    private void StartClassify(bool skipMl, CancellationToken appCt)
    {
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var token = _operationCts.Token;
        _state = State.Running;
        _status = skipMl ? "Starting rules-only classification..." : "Starting classification pipeline...";
        _lastError = "";
        _lastTrainResult = null;

        _ = Task.Run(async () =>
        {
            try
            {
                MlClassifier? mlClassifier = null;
                if (!skipMl)
                {
                    var modelPath = _settings.Ml?.ModelPath ?? ModelTrainerService.GetDefaultModelPath();
                    if (File.Exists(modelPath))
                        mlClassifier = new MlClassifier(_settings, modelPath);
                }

                using var mlCleanup = mlClassifier;
                var pipeline = new ClassificationPipeline(_ruleClassifier, mlClassifier, _db, _settings);
                var summary = await pipeline.RunAsync(skipMl, onStatus: msg => { _status = msg; RequestRender?.Invoke(); }, token);

                _status = $"Classified {summary.RuleClassified + summary.MlClassified} emails ({summary.Unclassified} remaining)";
                _classifyStatsDirty = true;
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

    private void StartTrain(CancellationToken appCt)
    {
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
        var token = _operationCts.Token;
        _state = State.Running;
        _status = "Loading training data...";
        _lastError = "";
        _lastTrainResult = null;

        var modelPath = _settings.Ml?.ModelPath ?? ModelTrainerService.GetDefaultModelPath();

        _ = Task.Run(async () =>
        {
            try
            {
                _lastTrainResult = await _trainerService.TrainAsync(modelPath, onStatus: msg => { _status = msg; RequestRender?.Invoke(); }, token);
                _status = $"Training complete: {_lastTrainResult.TrainingSamples} samples, Accuracy={_lastTrainResult.Accuracy:P1}";
                _classifyStatsDirty = true;
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

    private (int classified, int unclassified, int ruleCount, List<(string category, int count, double pct)> categories) GetClassificationStats()
    {
        if (!_classifyStatsDirty)
            return _cachedClassifyStats;

        try
        {
            var totalEmails = _db.Emails.Count();
            var classified = _db.Classifications.Select(c => c.MessageId).Distinct().Count();
            var unclassified = totalEmails - classified;
            var ruleCount = _ruleClassifier.RuleCount;

            var cats = _db.Classifications
                .GroupBy(c => c.Category)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToList();

            var totalClassified = cats.Sum(c => c.Count);
            var categoryList = cats
                .OrderByDescending(c => c.Count)
                .Select(c => (
                    category: c.Category.ToString(),
                    count: c.Count,
                    pct: totalClassified > 0 ? c.Count * 100.0 / totalClassified : 0.0))
                .Take(8)
                .ToList();

            _cachedClassifyStats = (classified, unclassified, ruleCount, categoryList);
            _classifyStatsDirty = false;
            return _cachedClassifyStats;
        }
        catch
        {
            _cachedClassifyStats = (0, 0, 0, []);
            return _cachedClassifyStats;
        }
    }
}
