using Microsoft.EntityFrameworkCore;

namespace InterviewBot.Infrastructure.Persistence;

public sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options)
{
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<TopicProgress> TopicProgress => Set<TopicProgress>();
    public DbSet<Digest> Digests => Set<Digest>();
    public DbSet<PracticeTask> PracticeTasks => Set<PracticeTask>();
    public DbSet<TaskAttempt> TaskAttempts => Set<TaskAttempt>();
    public DbSet<DayLog> DayLogs => Set<DayLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // enum'ы храним строками: читаемо в psql и не ломается при перестановке значений
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.TelegramUserId);
            e.Property(x => x.TelegramUserId).ValueGeneratedNever();
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
        });

        modelBuilder.Entity<Topic>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100);
            e.OwnsMany(x => x.CodeRefs, b => b.ToJson());
            e.HasOne(x => x.Progress).WithOne().HasForeignKey<TopicProgress>(x => x.TopicId);
        });

        modelBuilder.Entity<TopicProgress>(e =>
        {
            e.HasKey(x => x.TopicId);
        });

        modelBuilder.Entity<Digest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Topic).WithMany().HasForeignKey(x => x.TopicId);
            // Не больше одного утреннего набора на дату — последний рубеж против дублей, даже при гонке
            e.HasIndex(x => x.ForDate).IsUnique().HasFilter("kind = 'Daily'");
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.DigestId);
        });

        modelBuilder.Entity<PracticeTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TelegramMessageId);
            e.HasIndex(x => x.CreatedAt);
            e.HasMany(x => x.Attempts).WithOne(x => x.Task).HasForeignKey(x => x.TaskId);
        });

        modelBuilder.Entity<TaskAttempt>(e => e.HasKey(x => x.Id));

        modelBuilder.Entity<DayLog>(e => e.HasKey(x => x.Date));
    }
}
