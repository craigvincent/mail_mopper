using MailMopper.Commands;
using MailMopper.Data;
using MailMopper.Services;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Tests.Commands;

public class TrainCommandTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AppCancellation _cancellation;
    private readonly ModelTrainerService _trainer;

    public TrainCommandTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        _db = new AppDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();

        _cancellation = new AppCancellation();
        _trainer = new ModelTrainerService(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public void Constructor_NullTrainer_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TrainCommand(null!, _db, _cancellation));
        Assert.Equal("trainer", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullDbContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TrainCommand(_trainer, null!, _cancellation));
        Assert.Equal("dbContext", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullCancellation_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new TrainCommand(_trainer, _db, null!));
        Assert.Equal("cancellation", ex.ParamName);
    }
}
