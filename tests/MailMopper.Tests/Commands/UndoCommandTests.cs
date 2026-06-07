using MailMopper.Commands;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MailMopper.Tests.Commands;

public class UndoCommandTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppCancellation _cancellation;
    private readonly DatabaseService _databaseService;
    private readonly GmailAuthService _authService;
    private readonly ActionService _actionService;

    public UndoCommandTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _cancellation = new AppCancellation();
        _databaseService = new DatabaseService(_db);

        var settings = new MailMopper.Config.AppSettings();
        var session = new GmailSession();
        _authService = new GmailAuthService(settings, session);
        _actionService = new ActionService(Substitute.For<IGmailApi>(), _db, settings);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public void Constructor_NullDatabaseService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(null!, _authService, _actionService, _db, _cancellation));
        Assert.Equal("databaseService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullAuthService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(_databaseService, null!, _actionService, _db, _cancellation));
        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullActionService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(_databaseService, _authService, null!, _db, _cancellation));
        Assert.Equal("actionService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new UndoCommand(_databaseService, _authService, _actionService, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void UndoSettings_DefaultSessionId_IsNull()
    {
        var settings = new UndoSettings();
        Assert.Null(settings.SessionId);
    }
}
