using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TVE.Core.Storage;

namespace TVE.Ui.Views;

/// <summary>
/// Обёртка над Speaker для отображения в списке «Голоса в базе».
/// Реализует INotifyPropertyChanged, чтобы UI обновлялся при изменении.
/// </summary>
public class SpeakerRowViewModel : INotifyPropertyChanged
{
    private readonly Speaker _speaker;
    private readonly VoiceDatabase _db;

    public SpeakerRowViewModel(Speaker speaker, VoiceDatabase db)
    {
        _speaker = speaker;
        _db = db;
    }

    public int Id => _speaker.Id;

    /// <summary>
    /// Прямой доступ к модели Speaker. Используется для экспорта
    /// эмбеддинга и других операций, требующих полного объекта.
    /// </summary>
    public Speaker Source => _speaker;

    /// <summary>
    /// Отображаемое имя. Изменения сохраняются в БД.
    /// </summary>
    public string DisplayName
    {
        get => _speaker.DisplayName;
        set
        {
            var newValue = (value ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(newValue)) return;
            if (_speaker.DisplayName == newValue) return;

            _speaker.DisplayName = newValue;
            _db.UpdateSpeaker(_speaker);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Описание. Только для чтения из списка - редактируется в отдельном окне.
    /// </summary>
    public string Description => _speaker.Description;

    public DateTime LastSeen => _speaker.LastSeen;

    public string LastSeenText => _speaker.LastSeen.ToString("dd.MM.yyyy HH:mm");

    /// <summary>
    /// Обновляет поле Description после закрытия окна описания.
    /// </summary>
    public void UpdateDescription(string newDescription)
    {
        _speaker.Description = newDescription ?? string.Empty;
        _db.UpdateSpeaker(_speaker);
        OnPropertyChanged(nameof(Description));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}