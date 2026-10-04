using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace InterviewBot.Infrastructure.GitHub;

public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Personal access token (только чтение публичных репозиториев). Без него лимит — 60 запросов в час.</summary>
    public string? Token { get; init; }

    [Range(1, 8)]
    public int MaxParallelRequests { get; init; } = 2;

    /// <summary>Общий бюджет времени на поиск фрагмента: не уложились — набор уходит без него.</summary>
    [Range(10, 600)]
    public int SnippetTimeoutSeconds { get; init; } = 180;
}

public sealed record SourceFile(string Repo, string Path, string CommitSha, string Content);

/// <summary>Тонкий клиент GitHub REST API: только то, что нужно для permalink-фрагментов.</summary>
public sealed class GitHubClient(HttpClient http)
{
    public async Task<SourceFile> GetFileAtHeadAsync(string repo, string path, CancellationToken cancellationToken)
    {
        var repository = await http.GetFromJsonAsync<RepositoryInfo>($"repos/{repo}", cancellationToken)
            ?? throw new InvalidOperationException($"Repository {repo} not found");

        // SHA коммита фиксируем сразу: и содержимое, и permalink будут указывать на один и тот же коммит
        using var shaRequest = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo}/commits/{Uri.EscapeDataString(repository.DefaultBranch)}");
        shaRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.sha"));
        var sha = (await SendForStringAsync(shaRequest, cancellationToken)).Trim();

        using var fileRequest = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo}/contents/{path}?ref={sha}");
        fileRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
        var content = await SendForStringAsync(fileRequest, cancellationToken);

        return new SourceFile(repo, path, sha, content);
    }

    private async Task<string> SendForStringAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private sealed record RepositoryInfo([property: JsonPropertyName("default_branch")] string DefaultBranch);
}
