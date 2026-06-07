using MailMopper.Commands;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Tests.Commands;

public class RepairDatesCommandTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppCancellation _cancellation;
    private readonly GmailAuthService _authService;
    private readonly GmailFetchService _fetchService;

    public RepairDatesCommandTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _cancellation = new AppCancellation();

        var settings = new AppSettings();
        var session = new GmailSession();
        _authService = new GmailAuthService(settings, session);
        _fetchService = new GmailFetchService(session, _db, settings);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public void Constructor_NullAuthService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RepairDatesCommand(null!, _fetchService, _db, _cancellation));
        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullFetchService_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RepairDatesCommand(_authService, null!, _db, _cancellation));
        Assert.Equal("fetchService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new RepairDatesCommand(_authService, _fetchService, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }
}
