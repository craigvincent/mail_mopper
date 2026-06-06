using MailMopper.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MailMopper.Data;

public class AppDbContext : DbContext
{
    public DbSet<EmailRecord> Emails => Set<EmailRecord>();
    public DbSet<Classification> Classifications => Set<Classification>();
    public DbSet<ActionRecord> Actions => Set<ActionRecord>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<WhitelistEntry> Whitelist => Set<WhitelistEntry>();

    private readonly string _dbPath;

    public AppDbContext(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailMopper", "mail_mopper.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        _dbPath = "unused"; // path comes from options
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath
            }.ToString();
            optionsBuilder.UseSqlite(connectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailRecord>(entity =>
        {
            entity.HasKey(e => e.MessageId);
            entity.HasIndex(e => e.FromDomain);
            entity.HasIndex(e => e.From);
            entity.HasIndex(e => e.Date);
            entity.HasIndex(e => e.GmailCategory);
            entity.HasIndex(e => e.HasListUnsubscribe);
        });

        modelBuilder.Entity<Classification>(entity =>
        {
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.ReviewDecision);
            entity.HasOne(e => e.Email)
                  .WithMany()
                  .HasForeignKey(e => e.MessageId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActionRecord>(entity =>
        {
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.MessageId);
        });

        modelBuilder.Entity<WhitelistEntry>(entity => entity.HasIndex(e => e.Pattern).IsUnique());
    }
}
