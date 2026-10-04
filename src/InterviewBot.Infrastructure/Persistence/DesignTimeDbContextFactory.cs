using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InterviewBot.Infrastructure.Persistence;

/// <summary>Только для `dotnet ef migrations add`: к реальной БД не подключается.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BotDbContext>()
            .UseNpgsql("Host=localhost;Database=interview_bot")
            .UseSnakeCaseNamingConvention()
            .Options);
}
