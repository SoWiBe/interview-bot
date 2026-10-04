namespace InterviewBot.Core.Topics;

public enum TopicStatus
{
    New,
    Gap,
    Repeat,
    Understood,
}

/// <summary>Кнопки под теорией: ✅ Понял · 🔁 Повторить · ❌ Пробел.</summary>
public enum TopicFeedback
{
    Understood,
    Repeat,
    Gap,
}
