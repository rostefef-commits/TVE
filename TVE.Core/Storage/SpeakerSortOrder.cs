namespace TVE.Core.Storage;

/// <summary>
/// Режим сортировки списка говорящих в интерфейсе.
/// </summary>
public enum SpeakerSortOrder
{
    /// <summary>По дате последнего взаимодействия, сначала новые.</summary>
    DateNewFirst = 0,

    /// <summary>По дате последнего взаимодействия, сначала старые.</summary>
    DateOldFirst = 1,

    /// <summary>По имени, А - Я.</summary>
    NameAsc = 2,

    /// <summary>По имени, Я - А.</summary>
    NameDesc = 3,

    /// <summary>По количеству реплик, по убыванию.</summary>
    UtteranceCountDesc = 4
}