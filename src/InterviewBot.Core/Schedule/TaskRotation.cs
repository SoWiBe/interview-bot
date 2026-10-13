namespace InterviewBot.Core.Schedule;

/// <summary>Формат C#-задачи. Чередуется, чтобы практика была разной.</summary>
public enum CSharpCategory
{
    Algorithms,
    Concurrency,
    WriteCode,
    FixBug,
    Collections,
}

public static class TaskRotation
{
    private static readonly CSharpCategory[] Categories = Enum.GetValues<CSharpCategory>();

    /// <summary>
    /// Категория, которая не выпадала дольше всех. <paramref name="recentNewestFirst"/> — категории
    /// прошлых задач, от новых к старым. Ни разу не выпадавшие идут первыми, в порядке объявления.
    /// </summary>
    public static CSharpCategory NextCategory(IReadOnlyList<CSharpCategory> recentNewestFirst)
    {
        var best = Categories[0];
        var bestAge = -1;

        foreach (var category in Categories)
        {
            var index = IndexOf(recentNewestFirst, category);
            var age = index < 0 ? int.MaxValue : index;
            if (age > bestAge)
            {
                best = category;
                bestAge = age;
            }
        }

        return best;
    }

    private static int IndexOf(IReadOnlyList<CSharpCategory> items, CSharpCategory value)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
