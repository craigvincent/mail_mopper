using MailMopper.Commands;
using MailMopper.Config;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MailMopper.Tests.Commands;

public class FetchCommandTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;

    public FetchCommandTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _settings = new AppSettings
        {
            Actions = new ActionSettings { TrashBatchSize = 10 }
        };
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public void Constructor_NullAuthService_ThrowsArgumentNullException()
    {
        var mockFetch = Substitute.For<GmailFetchService>(
            new GmailSession(), _db, _settings);

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(null!, mockFetch, _db, new AppCancellation()));

        Assert.Equal("authService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullFetchService_ThrowsArgumentNullException()
    {
        var mockAuth = Substitute.For<GmailAuthService>(_settings, new GmailSession());

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(mockAuth, null!, _db, new AppCancellation()));

        Assert.Equal("fetchService", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullDbContext_ThrowsArgumentNullException()
    {
        var mockAuth = Substitute.For<GmailAuthService>(_settings, new GmailSession());
        var mockFetch = Substitute.For<GmailFetchService>(
            new GmailSession(), _db, _settings);

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(mockAuth, mockFetch, null!, new AppCancellation()));

        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullCancellation_ThrowsArgumentNullException()
    {
        var mockAuth = Substitute.For<GmailAuthService>(_settings, new GmailSession());
        var mockFetch = Substitute.For<GmailFetchService>(
            new GmailSession(), _db, _settings);

        var ex = Assert.Throws<ArgumentNullException>(() =>
            new FetchCommand(mockAuth, mockFetch, _db, null!));

        Assert.Equal("cancellation", ex.ParamName);
    }
}
