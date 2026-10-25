using System.Collections.Generic;
using NAudio.Wave;

namespace TVE.Core.Audio;

/// <summary>
/// Информация об устройстве ввода (микрофоне).
/// </summary>
public record InputDeviceInfo(int Number, string Name);

/// <summary>
/// Сервис для получения списка доступных микрофонов через NAudio.
/// </summary>
public static class AudioDeviceService
{
    /// <summary>
    /// Возвращает список всех подключённых устройств ввода.
    /// </summary>
    public static List<InputDeviceInfo> GetInputDevices()
    {
        var result = new List<InputDeviceInfo>();
        int count = WaveInEvent.DeviceCount;
        for (int i = 0; i < count; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            result.Add(new InputDeviceInfo(i, caps.ProductName));
        }
        return result;
    }

    /// <summary>
    /// Возвращает количество доступных устройств ввода.
    /// </summary>
    public static int DeviceCount => WaveInEvent.DeviceCount;
}