using InterviewBot.Core.Topics;
using InterviewBot.Infrastructure.Common;
using InterviewBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace InterviewBot.Infrastructure.Curriculum;

public sealed class CurriculumOptions
{
    public const string SectionName = "Curriculum";

    /// <summary>Путь к YAML с программой; относительный — от content root.</summary>
    public string Path { get; init; } = "seed/curriculum.yaml";
}

/// <summary>
/// Переносит программу из YAML в БД при старте. Названия, приоритеты и ссылки на код обновляются,
/// прогресс по темам — никогда: статус из YAML применяется только к новой теме.
/// </summary>
public sealed class CurriculumSynchronizer(
    BotDbContext db,
    IOptions<CurriculumOptions> options,
    IHostEnvironment environment,
    BotClock clock,
    ILogger<CurriculumSynchronizer> logger)
{
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(environment.ContentRootPath, options.Value.Path);
        var document = Parse(await File.ReadAllTextAsync(path, cancellationToken));

        var existing = await db.Topics.Include(t => t.Progress).ToDictionaryAsync(t => t.Id, cancellationToken);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var seen = new HashSet<string>();
        var sortOrder = 0;
        var added = 0;

        foreach (var module in document.Modules)
        {
            foreach (var item in module.Topics)
            {
                if (!seen.Add(item.Id))
                {
                    throw new InvalidOperationException($"Duplicate topic id '{item.Id}' in {path}");
                }

                if (!existing.TryGetValue(item.Id, out var topic))
                {
                    topic = new Topic { Id = item.Id, ModuleTitle = module.Title, Title = item.Title };
                    topic.Progress = new TopicProgress
                    {
                        TopicId = item.Id,
                        Status = ParseStatus(item.Status, item.Id),
                        StatusChangedOn = today,
                    };
                    db.Topics.Add(topic);
                    added++;
                }

                topic.Module = module.Number;
                topic.ModuleTitle = module.Title;
                topic.Title = item.Title;
                topic.Priority = item.Priority ?? module.Priority;
                topic.SortOrder = ++sortOrder;
                topic.IsActive = true;
                topic.CodeRefs = item.CodeRefs
                    .Select(r => new CodeRef { Repo = r.Repo, Path = r.Path, Symbol = r.Symbol })
                    .ToList();
            }
        }

        var deactivated = 0;
        foreach (var topic in existing.Values.Where(t => !seen.Contains(t.Id) && t.IsActive))
        {
            topic.IsActive = false;
            deactivated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Curriculum synced: {Total} topics, {Added} added, {Deactivated} deactivated", seen.Count, added, deactivated);
    }

    private static TopicStatus ParseStatus(string? status, string topicId) => status?.ToLowerInvariant() switch
    {
        null or "new" => TopicStatus.New,
        "gap" => TopicStatus.Gap,
        "repeat" => TopicStatus.Repeat,
        "understood" => TopicStatus.Understood,
        _ => throw new InvalidOperationException($"Unknown status '{status}' for topic '{topicId}'"),
    };

    private static CurriculumDocument Parse(string yaml) =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Deserialize<CurriculumDocument>(yaml);

    private sealed class CurriculumDocument
    {
        public List<ModuleItem> Modules { get; set; } = [];
    }

    private sealed class ModuleItem
    {
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public int Priority { get; set; } = 2;
        public List<TopicItem> Topics { get; set; } = [];
    }

    private sealed class TopicItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public int? Priority { get; set; }
        public string? Status { get; set; }
        public List<CodeRefItem> CodeRefs { get; set; } = [];
    }

    private sealed class CodeRefItem
    {
        public string Repo { get; set; } = "";
        public string Path { get; set; } = "";
        public string Symbol { get; set; } = "";
    }
}
