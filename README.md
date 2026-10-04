# interview-bot

Личный Telegram-бот для ежедневной подготовки к собеседованиям .NET Middle+/Senior.
Каждое утро присылает теорию по одной теме, фрагмент кода из реального open-source репозитория (permalink на коммит)
и задачи в формате live coding; делает ревью решений и ведёт интервальное повторение.

## Запуск

```bash
cp .env.example .env   # заполнить: токен бота, свой Telegram ID, ключ OpenRouter (GitHub-токен — по желанию)
docker compose up -d --build
```

Затем в Telegram: `/start`. Набор приходит в 07:00 Asia/Omsk, сразу — `/today`.

Health checks: `http://localhost:8080/health/live`, `http://localhost:8080/health/ready` (БД + живой polling).

Локально без Docker для бота: `docker compose up -d postgres`, секреты через user-secrets
(`dotnet user-secrets set "OpenAICompatible:ApiKey" "..." --project src/InterviewBot.Host`), затем
`dotnet run --project src/InterviewBot.Host`.

## Команды

| Команда | Что делает |
|---|---|
| `/today` | утренний набор сейчас (повторно не отправляет) |
| `/next` | ещё задача по текущей теме |
| `/topic <название>` | внеочередная тема |
| `/giveup` | решение задачи (reply на задачу или последняя открытая) |
| `/stats` | темы по статусам, серия, % задач с уточняющими вопросами |
| `/pause <дней>` | пауза без потери серии, `/pause 0` — снять |
| `/settings` | `time 07:30`, `weekend review \| full \| off` |

Решение задачи — reply на сообщение с задачей: уточняющие вопросы + код.

## Программа

`seed/curriculum.yaml` — темы, приоритеты, начальные статусы и ссылки на код (`codeRefs`).
Применяется при старте; в Docker папка смонтирована, так что после правки достаточно `docker compose restart bot`.
Прогресс по темам хранится в БД и правкой файла не затирается.

## Как устроено

- `src/InterviewBot.Core` — чистая логика без инфраструктуры, покрыта тестами:
  интервальное повторение (`SpacedRepetition`), выбор темы (`TopicSelector`), серия (`StreakCalculator`),
  план дня (`DailyPlanner`), идемпотентная доставка (`DigestDelivery`).
- `src/InterviewBot.Infrastructure` — EF Core/PostgreSQL, Telegram, генерация контента (structured outputs), GitHub, Quartz.

Генерация контента выбирается в `Content:Provider`: `OpenAICompatible` — любой OpenAI-совместимый API
(по умолчанию OpenRouter, модель `deepseek/deepseek-v4-pro-0813`; подходят и DeepSeek, Ollama) или `Anthropic`.
Промпты, схемы ответов и проверка JSON общие (`StructuredContentGenerator`), провайдер отвечает только за вызов.
- `src/InterviewBot.Host` — Generic Host: long polling, health checks, Serilog.

Ключевые решения:

- **Без двойной отправки.** Факт отправки каждой части набора захватывается атомарным
  `UPDATE ... WHERE x_sent_at IS NULL` до отправки (at-most-once); при ошибке Telegram захват отпускается.
  Уникальный индекс «один утренний набор на дату» — последний рубеж.
- **Код не выдумывается.** Модель получает реальный файл с номерами строк и возвращает только диапазон;
  сам код вырезается из ответа GitHub API на зафиксированном коммите.
- **Quartz с персистентным хранилищем** в той же PostgreSQL; триггер не пересоздаётся при старте, чтобы
  пропущенный во время простоя запуск выполнился (misfire → fire and proceed). Ошибка генерации — до 3 попыток через 15 минут.
- Контент дня генерируется один раз и хранится готовым HTML.

Секреты — только через переменные окружения (`.env`) или user-secrets, в репозиторий не коммитятся.
