# interview-bot

Личный Telegram-бот для ежедневной подготовки к собеседованиям .NET Middle+/Senior:
утром присылает теорию, фрагмент кода из реального open-source репозитория и две задачи на live coding.

## Запуск

```bash
cp .env.example .env   # заполнить секреты
docker compose up -d --build
```

Health checks: `http://localhost:8080/health/live`, `http://localhost:8080/health/ready`.

Локально без Docker для бота: `docker compose up -d postgres`, затем `dotnet run --project src/InterviewBot.Host`.

## Структура

- `src/InterviewBot.Core` — доменная логика (выбор темы, интервальное повторение), без инфраструктурных зависимостей
- `src/InterviewBot.Infrastructure` — EF Core/PostgreSQL, Telegram, Anthropic, GitHub
- `src/InterviewBot.Host` — Generic Host: long polling, Quartz, health checks
- `tests/InterviewBot.Core.Tests` — NUnit

Секреты — только через переменные окружения (`.env`) или user-secrets, в репозиторий не коммитятся.
