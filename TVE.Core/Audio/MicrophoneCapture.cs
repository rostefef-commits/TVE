using NAudio.Wave;
using System.Timers;
using TVE.Core.Services;

namespace TVE.Core.Audio;

/// <summary>
/// Захватывает аудио с микрофона в формате 16kHz mono float.
/// VAD собирает сегменты речи; watchdog отслеживает потерю/возврат устройства.
/// Параллельно пишет статус в ModuleStatus и события в EventBus.
/// </summary>
public class MicrophoneCapture : IDisposable
{
    private WaveInEvent? _waveIn;
    private readonly List<float> _buffer = new();
    private DateTime _lastSpeechTime = DateTime.UtcNow;
    private bool _isSpeaking;
    private bool _isRunning;
    private bool _disposed;
    private bool _deviceAvailable = true;
    private System.Timers.Timer? _watchdog;
    private string _currentDeviceName = "—";

    public const int SampleRate = 16000;
    private const float SilenceThreshold = 0.01f;
    private const int SilenceTimeoutMs = 800;
    private const int MinSpeechMs = 300;
    private const int WatchdogIntervalMs = 2000;

    public event Action<float[]>? SpeechSegmentReady;
    public event Action<Exception?>? MicrophoneLost;
    public event Action? MicrophoneRestored;

    public bool IsRunning => _isRunning;
    public bool IsDeviceAvailable => _deviceAvailable;

    public MicrophoneCapture()
    {
        _deviceAvailable = CheckDeviceAvailable();
        ModuleStatus.SetMic(_deviceAvailable, false);

        if (_deviceAvailable)
            EventBus.Ok("Mic", "Микрофон доступен, ожидание записи");
        else
            EventBus.Warn("Mic", "Устройства ввода не найдено");

        StartWatchdog();
    }

    public void Start(int? deviceNumber = null)
    {
        if (_isRunning) return;

        if (!CheckDeviceAvailable())
        {
            _deviceAvailable = false;
            ModuleStatus.SetMic(false, false);
            EventBus.Error("Mic", "Не удалось начать запись: устройство недоступно");
            MicrophoneLost?.Invoke(null);
            return;
        }

        try
        {
            int deviceCount = WaveInEvent.DeviceCount;
            int chosen = deviceNumber ?? 0;
            if (chosen < 0 || chosen >= deviceCount)
                chosen = 0;

            try
            {
                var caps = WaveInEvent.GetCapabilities(chosen);
                _currentDeviceName = caps.ProductName;
                ModuleStatus.SetMicDevice(_currentDeviceName);
            }
            catch { _currentDeviceName = $"Устройство #{chosen}"; }

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3,
                DeviceNumber = chosen
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            _isRunning = true;
            _deviceAvailable = true;

            ModuleStatus.SetMic(true, true);
            EventBus.Ok("Mic", $"Запись начата ({_currentDeviceName})");
        }
        catch (Exception ex)
        {
            _waveIn?.Dispose();
            _waveIn = null;
            _isRunning = false;
            _deviceAvailable = false;
            ModuleStatus.SetMic(false, false);
            EventBus.Error("Mic", $"Ошибка запуска записи: {ex.Message}");
            MicrophoneLost?.Invoke(ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float[] samples = new float[e.BytesRecorded / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            samples[i] = sample / 32768f;
        }

        float rms = CalculateRms(samples);
        ModuleStatus.SetMicRms(rms);

        bool isSpeech = rms > SilenceThreshold;

        if (isSpeech)
        {
            _buffer.AddRange(samples);
            _lastSpeechTime = DateTime.UtcNow;
            _isSpeaking = true;
        }
        else if (_isSpeaking)
        {
            if ((DateTime.UtcNow - _lastSpeechTime).TotalMilliseconds > SilenceTimeoutMs)
            {
                int durationMs = _buffer.Count * 1000 / SampleRate;
                if (durationMs >= MinSpeechMs)
                {
                    var segment = _buffer.ToArray();
                    _buffer.Clear();
                    _isSpeaking = false;

                    EventBus.Info("VAD", $"Сегмент готов: {durationMs} мс, {segment.Length} сэмплов");
                    SpeechSegmentReady?.Invoke(segment);
                }
                else
                {
                    _buffer.Clear();
                    _isSpeaking = false;
                }
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _isRunning = false;
            _deviceAvailable = false;
            ModuleStatus.SetMic(false, false);
            EventBus.Error("Mic", $"Запись остановлена с ошибкой: {e.Exception.Message}");
            MicrophoneLost?.Invoke(e.Exception);
        }
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            _buffer.Clear();
            _isSpeaking = false;
            return;
        }

        int durationMs = _buffer.Count * 1000 / SampleRate;
        if (durationMs >= MinSpeechMs)
        {
            var segment = _buffer.ToArray();
            _buffer.Clear();
            _isSpeaking = false;
            try { SpeechSegmentReady?.Invoke(segment); }
            catch { }
        }
        else
        {
            _buffer.Clear();
            _isSpeaking = false;
        }

        try { _waveIn?.StopRecording(); } catch { }
        _waveIn?.Dispose();
        _waveIn = null;
        _isRunning = false;

        ModuleStatus.SetMic(_deviceAvailable, false);
        ModuleStatus.SetMicRms(0);
        EventBus.Info("Mic", "Запись остановлена");
    }

    private void StartWatchdog()
    {
        _watchdog = new System.Timers.Timer(WatchdogIntervalMs) { AutoReset = true };
        _watchdog.Elapsed += OnWatchdogTick;
        _watchdog.Start();
    }

    private void OnWatchdogTick(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        bool available = CheckDeviceAvailable();
        if (available == _deviceAvailable) return;

        _deviceAvailable = available;
        if (!available)
        {
            Stop();
            ModuleStatus.SetMic(false, false);
            EventBus.Warn("Mic", "Устройство ввода потеряно");
            MicrophoneLost?.Invoke(null);
        }
        else
        {
            ModuleStatus.SetMic(true, _isRunning);
            EventBus.Ok("Mic", "Устройство ввода снова доступно");
            MicrophoneRestored?.Invoke();
        }
    }

    private static bool CheckDeviceAvailable()
    {
        try { return WaveInEvent.DeviceCount > 0; }
        catch { return false; }
    }

    private static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        float sum = 0;
        foreach (var s in samples) sum += s * s;
        return MathF.Sqrt(sum / samples.Length);
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog?.Stop();
        _watchdog?.Dispose();
        _watchdog = null;
        Stop();
    }
}