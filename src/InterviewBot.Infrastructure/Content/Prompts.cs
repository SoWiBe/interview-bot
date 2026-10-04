namespace InterviewBot.Infrastructure.Content;

internal static class Prompts
{
    public const string System = """
        Ты — наставник, который готовит backend-разработчика к техническим собеседованиям уровня Middle+/Senior
        на позиции .NET / ASP.NET Core в продуктовые компании (Ozon, МТС Pay, ПИК Digital и похожие).

        О студенте:
        - 4 года 8 месяцев коммерческого опыта на .NET. Стек в проде: C#, ASP.NET Core, .NET 8, PostgreSQL, EF Core,
          MS SQL, Redis, Docker, Quartz.NET, Keycloak, Serilog, Health checks.
        - Не работал в проде с Kafka, RabbitMQ, Kibana, Grafana, Sentry, OpenTelemetry.
        - Принцип обучения: «сначала сам — потом ИИ». Помогай думать, а не думай за него.

        Подтверждённые пробелы (по реальному собеседованию в Ozon):
        1. Не использует свойства входных данных: массив отсортирован, а он пишет O(n) или сортирует, вместо бинарного поиска.
        2. Не задаёт уточняющих вопросов: сразу пишет код, хотя ответы меняют решение
           (сколько будет запросов, что делать при ошибках, включительны ли границы).
        3. Многопоточность и async: Monitor.Enter внутри async-метода, Monitor.Exit в catch вместо finally,
           неуверенность в lock / Interlocked / SemaphoreSlim.
        4. Параллельные вызовы: забывает передать CancellationToken, не делает таймаут через linked CTS,
           одна ошибка роняет весь метод, Min() на пустой коллекции.
        5. SQL: путает WHERE/HAVING, забывает COUNT(DISTINCT) при размножении строк в JOIN, слабо знает оконные
           функции и «топ-N в группе».
        6. Стек из вакансий без опыта: RabbitMQ, Kafka, распределённые блокировки в Redis, мониторинг,
           жизненный цикл HTTP-запроса в ASP.NET Core, снятие и разбор дампов.

        Правила оформления:
        - Язык — русский, термины — на английском (SemaphoreSlim, offset, consumer group).
        - Текст читают с телефона: короткие абзацы, без воды, без вступлений «давайте разберём».
        - Разметка внутри текстовых полей: только `inline code` и **жирный**. Блоки кода — только в отдельных полях,
          без ``` внутри них. Никаких заголовков #, таблиц и HTML.
        - Код — современный C# (.NET 8+) или PostgreSQL. Не выдумывай API: если не уверен, что метод существует, не используй его.
        """;

    public static string DailyContent(TopicContext topic, IReadOnlyList<TaskRequest> tasks) => $"""
        Подготовь утренний набор по теме «{topic.Title}» (модуль «{topic.ModuleTitle}»).

        1. Теория (3–5 минут чтения):
           - essence: суть в 2–3 предложениях;
           - analogy: бытовая аналогия, 1–3 предложения;
           - productionCase: продакшн-кейс — как это ломается или используется в реальном высоконагруженном сервисе, 3–6 предложений;
           - codeExample: короткий пример кода, не больше 15 строк, без ```; codeLanguage — его язык;
           - selfCheckQuestions: ровно 3 вопроса для самопроверки вслух, как их задают на собеседовании.

        2. Задачи — ровно {tasks.Count} шт., в таком порядке:
        {DescribeTasks(tasks)}

        {TaskRules}
        """;

    public static string Tasks(TopicContext topic, IReadOnlyList<TaskRequest> tasks, IReadOnlyList<string> previousTitles) => $"""
        Тема: «{topic.Title}» (модуль «{topic.ModuleTitle}»).
        Сгенерируй задачи — ровно {tasks.Count} шт., в таком порядке:
        {DescribeTasks(tasks)}
        {(previousTitles.Count > 0 ? "Не повторяй уже выданные задачи: " + string.Join("; ", previousTitles) : "")}

        {TaskRules}
        """;

    private const string TaskRules = """
        Требования к задачам (формат секции live coding):
        - statement: условие намеренно скупое, со скрытыми неоднозначностями, как на настоящем собеседовании.
          Пример стиля: «Вернуть количество отгрузок за период» — без указания, включительны ли границы и сколько будет запросов.
          Для SQL дай схему таблиц (CREATE TABLE или перечень колонок) прямо в statement.
          Не пиши в условии подсказок вроде «учтите краевые случаи» и не перечисляй неоднозначности.
        - expectedQuestions: 2–5 уточняющих вопросов, которые стоило задать, и почему ответ на каждый меняет решение
          (в одной строке: «вопрос — как влияет на решение»).
        - referenceSolution: эталонное решение (только код, без ```), с проверками краевых случаев.
        - solutionExplanation: разбор решения в 3–6 предложениях: идея, краевые случаи, типичные ошибки.
        - complexity: сложность по времени и памяти, одной строкой.
        - Задачи решаются за 15 минут, прицельно бьют в пробелы студента: сортированные входные данные,
          отмена и таймауты, COUNT(DISTINCT) в JOIN, блокировки в async.
        """;

    private static string DescribeTasks(IReadOnlyList<TaskRequest> tasks) =>
        string.Join("\n", tasks.Select((t, i) => $"   {i + 1}) язык {t.Language}: {t.Focus}"));

    public static string SnippetSelection(TopicContext topic, string repo, string path, string numberedWindow) => $"""
        Тема дня: «{topic.Title}».
        Ниже — реальный фрагмент файла {path} из репозитория {repo}, с номерами строк исходного файла.

        Выбери непрерывный диапазон строк (не больше 40 строк), который лучше всего иллюстрирует тему
        и содержит приём, полезный для собеседования. Диапазон должен быть цельным: начинаться и заканчиваться
        на границе метода, блока или логического шага, а не посреди выражения.
        Если в окне нет ничего полезного по теме — верни suitable=false и любые числа.

        - startLine, endLine — номера строк из левой колонки, включительно;
        - whatIsGood: что здесь сделано хорошо и почему, 3–5 предложений;
        - takeaway: какой приём стоит забрать себе, 1–2 предложения.
        Не пересказывай код построчно и не приводи код, которого нет во фрагменте.

        <file>
        {numberedWindow}
        </file>
        """;

    public static string AttemptReview(PracticeTaskView task, string answer) => $"""
        Сделай ревью попытки студента решить задачу — как интервьюер после секции live coding.

        <task language="{task.Language}">
        {task.Statement}
        </task>

        <expected_questions>
        {string.Join("\n", task.ExpectedQuestions.Select(q => "- " + q))}
        </expected_questions>

        <reference_solution>
        {task.ReferenceSolution}
        </reference_solution>

        <student_attempt>
        {answer}
        </student_attempt>

        Поля ответа:
        - askedClarifyingQuestions: true, только если в попытке явно записаны хотя бы два уточняющих вопроса
          (или явные допущения вида «считаю, что границы включительны»), относящиеся к неоднозначностям условия;
        - clarifyingFeedback: какие вопросы задал, насколько они по делу;
        - missedQuestions: какие важные вопросы не задал (пусто, если всё задал);
        - correctness: корректно ли решение, есть ли баги — конкретно, со ссылкой на место в коде;
        - edgeCases: какие краевые случаи обработаны и какие нет;
        - complexity: сложность решения студента по времени и памяти и можно ли лучше;
        - improvements: как улучшить, по пунктам;
        - improvedCode: исправленная версия кода студента (только код, без ```); пустая строка, если исправлять нечего;
        - score: оценка от 1 до 5, как поставил бы интервьюер (3 — «прошёл с замечаниями»).
        Будь прямым: не хвали за то, чего нет. Не показывай эталонное решение целиком — его студент откроет сам.
        """;
}

/// <summary>Данные задачи, нужные для ревью.</summary>
public sealed record PracticeTaskView(string Language, string Statement, IReadOnlyList<string> ExpectedQuestions, string ReferenceSolution);
