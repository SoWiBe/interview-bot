using System.Text;
using InterviewBot.Infrastructure.Content;
using InterviewBot.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InterviewBot.Infrastructure.GitHub;

public sealed record CodeSnippet(
    string Repo,
    string Path,
    string CommitSha,
    int StartLine,
    int EndLine,
    string Code,
    string WhatIsGood,
    string Takeaway)
{
    public string Permalink => $"https://github.com/{Repo}/blob/{CommitSha}/{Path}#L{StartLine}-L{EndLine}";
}

/// <summary>
/// Достаёт фрагмент реального кода по курируемым ссылкам темы.
/// Код всегда берётся из ответа GitHub — модель выбирает только номера строк и пишет разбор,
/// поэтому выдать сгенерированный код за код из репозитория она не может.
/// </summary>
public sealed class CodeSnippetProvider(
    GitHubClient gitHub,
    IContentGenerator generator,
    IOptions<GitHubOptions> options,
    ILogger<CodeSnippetProvider> logger)
{
    public const int MaxSnippetLines = 40;
    private const int LinesBeforeSymbol = 20;
    private const int LinesAfterSymbol = 180;

    public async Task<CodeSnippet?> FindAsync(TopicContext topic, IReadOnlyList<CodeRef> codeRefs, CancellationToken cancellationToken)
    {
        if (codeRefs.Count == 0)
        {
            return null;
        }

        // Linked CTS: отменяется и при остановке сервиса (внешний токен), и по собственному таймауту.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.SnippetTimeoutSeconds));

        try
        {
            var files = await DownloadAsync(codeRefs, timeout.Token);

            for (var i = 0; i < codeRefs.Count; i++)
            {
                if (files[i] is not { } file)
                {
                    continue;
                }

                var snippet = await TrySelectAsync(topic, file, codeRefs[i].Symbol, timeout.Token);
                if (snippet is not null)
                {
                    return snippet;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // сработал наш таймаут, а не остановка сервиса: набор уйдёт без фрагмента
            logger.LogWarning("Snippet search for {Topic} timed out", topic.Title);
        }

        return null;
    }

    /// <summary>
    /// Скачивает файлы параллельно, но не больше MaxParallelRequests одновременно — бережём rate limit GitHub.
    /// Ошибка одного файла не роняет остальные: на его месте будет null.
    /// </summary>
    private async Task<SourceFile?[]> DownloadAsync(IReadOnlyList<CodeRef> codeRefs, CancellationToken cancellationToken)
    {
        var files = new SourceFile?[codeRefs.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, codeRefs.Count),
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallelRequests, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var codeRef = codeRefs[i];
                try
                {
                    // каждый поток пишет в свой индекс — синхронизация не нужна
                    files[i] = await gitHub.GetFileAtHeadAsync(codeRef.Repo, codeRef.Path, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // таймаут HttpClient — тоже OperationCanceledException, но отменяли не нас: это ошибка одного файла
                    logger.LogWarning(ex, "Failed to download {Repo}/{Path}", codeRef.Repo, codeRef.Path);
                }
            });

        return files;
    }

    private async Task<CodeSnippet?> TrySelectAsync(TopicContext topic, SourceFile file, string symbol, CancellationToken cancellationToken)
    {
        var lines = file.Content.ReplaceLineEndings("\n").Split('\n');
        var symbolIndex = Array.FindIndex(lines, l => l.Contains(symbol, StringComparison.Ordinal));
        if (symbolIndex < 0)
        {
            logger.LogWarning("Symbol {Symbol} not found in {Repo}/{Path}", symbol, file.Repo, file.Path);
            return null;
        }

        var windowStart = Math.Max(1, symbolIndex + 1 - LinesBeforeSymbol);
        var windowEnd = Math.Min(lines.Length, symbolIndex + 1 + LinesAfterSymbol);

        var numbered = new StringBuilder();
        for (var line = windowStart; line <= windowEnd; line++)
        {
            numbered.Append(line).Append(": ").AppendLine(lines[line - 1]);
        }

        var selection = await generator.SelectSnippetAsync(topic, file.Repo, file.Path, numbered.ToString(), cancellationToken);

        var length = selection.EndLine - selection.StartLine + 1;
        if (!selection.Suitable
            || selection.StartLine < windowStart
            || selection.EndLine > windowEnd
            || length is < 1 or > MaxSnippetLines)
        {
            logger.LogInformation(
                "Snippet rejected for {Repo}/{Path}: suitable {Suitable}, lines {Start}-{End}",
                file.Repo, file.Path, selection.Suitable, selection.StartLine, selection.EndLine);
            return null;
        }

        var code = Dedent(lines[(selection.StartLine - 1)..selection.EndLine]);

        return new CodeSnippet(
            file.Repo, file.Path, file.CommitSha, selection.StartLine, selection.EndLine, code, selection.WhatIsGood, selection.Takeaway);
    }

    private static string Dedent(string[] lines)
    {
        var indent = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Length - l.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).TrimEnd();
    }
}
