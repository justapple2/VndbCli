using Vndb.Core;

namespace Vndb.Contracts;

public static class PersonalMapping
{
    public static PersonalDetails ToPersonal(this PersonalPlayData value) => new(value.Title, value.NovelId, value.Status,
        value.StartedOn, value.FinishedOn, value.DurationMinutes, value.Score, value.Review, value.Notes, value.ExtraNotes, value.Language);
}
