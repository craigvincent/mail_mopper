using MailMopper.Commands;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using MailMopper.Tui;
using MailMopper.Tui.Views;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MailMopper.Tests.Commands;

public class CommandConstructorTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly AppCancellation _cancellation;
    private readonly GmailSession _session;

    private readonly GmailAuthService _authService;
    private readonly GmailFetchService _fetchService;
    private readonly DatabaseService _databaseService;
    private readonly RuleClassifier _ruleClassifier;
    private readonly ModelTrainerService _modelTrainer;
    private readonly ActionService _actionService;
    private readonly ReviewService _reviewService;
    private readonly ReviewApp _reviewApp;
    private readonly GmailServices _gmailServices;

    private readonly HomeView _homeView;
    private readonly FetchView _fetchView;
    private readonly ClassifyView _classifyView;
    private readonly ExecuteView _executeView;
    private readonly UndoView _undoView;
    private readonly ReviewView _reviewView;
    private readonly MailMopperApp _mailMopperAppInstance;

    public CommandConstructorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _settings = new AppSettings();
        _cancellation = new AppCancellation();
        _session = new GmailSession();

        _authService = new GmailAuthService(_settings, _session);
        _fetchService = new GmailFetchService(_session, _db, _settings);
        _databaseService = new DatabaseService(_db);
        _ruleClassifier = new RuleClassifier(_settings);
        _modelTrainer = new ModelTrainerService(_db);
        _actionService = new ActionService(Substitute.For<IGmailApi>(), _db, _settings);
        _reviewService = new ReviewService(_db);
        _reviewApp = new ReviewApp(_reviewService);
        _gmailServices = new GmailServices(_authService, _fetchService);

        _homeView = new HomeView(_db, _session);
        _fetchView = new FetchView(_fetchService, _db, _session);
        _classifyView = new ClassifyView(_ruleClassifier, _db, _settings, _modelTrainer, _session);
        _executeView = new ExecuteView(_actionService, _db, _session);
        _undoView = new UndoView(_databaseService, _actionService, _session);
        _reviewView = new ReviewView(_reviewService);
        _mailMopperAppInstance = new MailMopperApp(_session, _db, _settings, _homeView, _fetchView, _classifyView, _executeView, _undoView, _reviewView);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    #region AuthCommand

    [Fact]
    public void AuthCommand_NullAuthService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new AuthCommand(null!, _session, _settings, _cancellation));
        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void AuthCommand_NullSession_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new AuthCommand(_authService, null!, _settings, _cancellation));
        Assert.Equal("session", ex.ParamName);
    }

    [Fact]
    public void AuthCommand_NullAppSettings_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new AuthCommand(_authService, _session, null!, _cancellation));
        Assert.Equal("appSettings", ex.ParamName);
    }

    [Fact]
    public void AuthCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new AuthCommand(_authService, _session, _settings, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region ClassifyCommand

    [Fact]
    public void ClassifyCommand_NullRuleClassifier_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ClassifyCommand(null!, _db, _settings, _cancellation));
        Assert.Equal("ruleClassifier", ex.ParamName);
    }

    [Fact]
    public void ClassifyCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ClassifyCommand(_ruleClassifier, null!, _settings, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void ClassifyCommand_NullAppSettings_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ClassifyCommand(_ruleClassifier, _db, null!, _cancellation));
        Assert.Equal("appSettings", ex.ParamName);
    }

    [Fact]
    public void ClassifyCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ClassifyCommand(_ruleClassifier, _db, _settings, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region ExecuteCommand

    [Fact]
    public void ExecuteCommand_NullAuthService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ExecuteCommand(null!, _actionService, _db, _cancellation));
        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void ExecuteCommand_NullActionService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ExecuteCommand(_authService, null!, _db, _cancellation));
        Assert.Equal("actionService", ex.ParamName);
    }

    [Fact]
    public void ExecuteCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ExecuteCommand(_authService, _actionService, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void ExecuteCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ExecuteCommand(_authService, _actionService, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region FetchCommand

    [Fact]
    public void FetchCommand_NullAuthService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(null!, _fetchService, _db, _cancellation));
        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void FetchCommand_NullFetchService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(_authService, null!, _db, _cancellation));
        Assert.Equal("fetchService", ex.ParamName);
    }

    [Fact]
    public void FetchCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(_authService, _fetchService, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void FetchCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(_authService, _fetchService, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region RepairDatesCommand

    [Fact]
    public void RepairDatesCommand_NullAuthService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RepairDatesCommand(null!, _fetchService, _db, _cancellation));
        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void RepairDatesCommand_NullFetchService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RepairDatesCommand(_authService, null!, _db, _cancellation));
        Assert.Equal("fetchService", ex.ParamName);
    }

    [Fact]
    public void RepairDatesCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RepairDatesCommand(_authService, _fetchService, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void RepairDatesCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RepairDatesCommand(_authService, _fetchService, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region ResetCommand

    [Fact]
    public void ResetCommand_NullAppSettings_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ResetCommand(null!, _cancellation));
        Assert.Equal("appSettings", ex.ParamName);
    }

    [Fact]
    public void ResetCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ResetCommand(_settings, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region ReviewCommand

    [Fact]
    public void ReviewCommand_NullReviewApp_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ReviewCommand(null!, _db, _cancellation));
        Assert.Equal("reviewApp", ex.ParamName);
    }

    [Fact]
    public void ReviewCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ReviewCommand(_reviewApp, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void ReviewCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ReviewCommand(_reviewApp, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region RunCommand

    [Fact]
    public void RunCommand_NullGmailServices_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RunCommand(null!, _ruleClassifier, _reviewApp, _actionService, _db, _settings, _cancellation));
        Assert.Equal("gmailServices", ex.ParamName);
    }

    [Fact]
    public void RunCommand_NullRuleClassifier_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RunCommand(_gmailServices, null!, _reviewApp, _actionService, _db, _settings, _cancellation));
        Assert.Equal("ruleClassifier", ex.ParamName);
    }

    [Fact]
    public void RunCommand_NullReviewApp_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RunCommand(_gmailServices, _ruleClassifier, null!, _actionService, _db, _settings, _cancellation));
        Assert.Equal("reviewApp", ex.ParamName);
    }

    [Fact]
    public void RunCommand_NullActionService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RunCommand(_gmailServices, _ruleClassifier, _reviewApp, null!, _db, _settings, _cancellation));
        Assert.Equal("actionService", ex.ParamName);
    }

    [Fact]
    public void RunCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RunCommand(_gmailServices, _ruleClassifier, _reviewApp, _actionService, null!, _settings, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void RunCommand_NullAppSettings_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RunCommand(_gmailServices, _ruleClassifier, _reviewApp, _actionService, _db, null!, _cancellation));
        Assert.Equal("appSettings", ex.ParamName);
    }

    [Fact]
    public void RunCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RunCommand(_gmailServices, _ruleClassifier, _reviewApp, _actionService, _db, _settings, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region StatsCommand

    [Fact]
    public void StatsCommand_NullDatabaseService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new StatsCommand(null!, _db, _cancellation));
        Assert.Equal("databaseService", ex.ParamName);
    }

    [Fact]
    public void StatsCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new StatsCommand(_databaseService, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void StatsCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new StatsCommand(_databaseService, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region TrainCommand

    [Fact]
    public void TrainCommand_NullTrainer_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TrainCommand(null!, _db, _cancellation));
        Assert.Equal("trainer", ex.ParamName);
    }

    [Fact]
    public void TrainCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TrainCommand(_modelTrainer, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void TrainCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TrainCommand(_modelTrainer, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region UndoCommand

    [Fact]
    public void UndoCommand_NullDatabaseService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(null!, _authService, _actionService, _db, _cancellation));
        Assert.Equal("databaseService", ex.ParamName);
    }

    [Fact]
    public void UndoCommand_NullAuthService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(_databaseService, null!, _actionService, _db, _cancellation));
        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void UndoCommand_NullActionService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(_databaseService, _authService, null!, _db, _cancellation));
        Assert.Equal("actionService", ex.ParamName);
    }

    [Fact]
    public void UndoCommand_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(_databaseService, _authService, _actionService, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void UndoCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(_databaseService, _authService, _actionService, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion

    #region AppCommand

    [Fact]
    public void AppCommand_NullApp_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new AppCommand(null!, _cancellation));
        Assert.Equal("app", ex.ParamName);
    }

    [Fact]
    public void AppCommand_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new AppCommand(_mailMopperAppInstance, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }

    #endregion
}
