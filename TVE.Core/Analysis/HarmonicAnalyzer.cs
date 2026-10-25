using System.Numerics;
using TVE.Core.Services;

namespace TVE.Core.Analysis;

/// <summary>
/// Результат анализа гармонической и формантной структуры
/// голосового сигнала. Заполняется методом
/// <see cref="HarmonicAnalyzer.Analyze(float[])"/>.
/// </summary>
public class HarmonicAnalysisResult
{
    public float F0 { get; set; }
    public float F0Cepstrum { get; set; }
    public float F0Hps { get; set; }
    public float F0Autocorrelation { get; set; }
    public bool F0Agreed { get; set; }

    public float[] HarmonicFrequencies { get; set; } = Array.Empty<float>();
    public float[] HarmonicAmplitudes { get; set; } = Array.Empty<float>();
    public float[] HarmonicAmplitudesDb { get; set; } = Array.Empty<float>();

    public float[] Formants { get; set; } = Array.Empty<float>();

    public float Rms { get; set; }
    public float Snr { get; set; }
}

/// <summary>
/// Спектрограмма сигнала: матрица [частотный бин, кадр] в дБ.
/// </summary>
public class SpectrogramResult
{
    public double[,] Data { get; set; } = new double[0, 0];
    public int FreqBins { get; set; }
    public int FrameCount { get; set; }
    public double BinHz { get; set; }
    public double FrameSec { get; set; }
    public double TotalSeconds { get; set; }
    public double MaxFreqHz => (FreqBins - 1) * BinHz;
}

/// <summary>
/// Результат быстрого анализа — только F0 (без гармоник и формант).
/// Используется в пакетном анализе по кадрам.
/// </summary>
public class F0OnlyResult
{
    public float F0 { get; set; }
    public float F0Cepstrum { get; set; }
    public float F0Hps { get; set; }
    public float F0Autocorrelation { get; set; }
    public bool F0Agreed { get; set; }
    public float Rms { get; set; }
    public float Snr { get; set; }
}

/// <summary>
/// Анализатор гармонической и формантной структуры голоса.
/// Работает на классическом DSP: FFT (вручную), кепстр, HPS,
/// автокорреляция, LPC. Без внешних математических библиотек.
/// </summary>
public static class HarmonicAnalyzer
{
    public const int SampleRate = 16000;
    private const int FrameLength = 1024;
    private const int FftSize = 2048;
    private const float MinF0 = 60f;
    private const float MaxF0 = 500f;
    private const float PreEmphasis = 0.97f;

    /// <summary>
    /// Порядок LPC. Формула Саито: N ≈ SR/1000 + 4 = 20 для 16 кГц.
    /// Ставим 22 с запасом — хватает на 4–5 формант.
    /// </summary>
    private const int LpcOrder = 22;

    private const int HarmonicCount = 8;
    private const int FormantCount = 4;

    // ============================================================
    // ===== ГЛАВНЫЙ МЕТОД =====
    // ============================================================

    public static HarmonicAnalysisResult Analyze(float[] samples)
    {
        var result = new HarmonicAnalysisResult();

        if (samples == null || samples.Length < FrameLength)
        {
            EventBus.Warn("Harmonic",
                $"Сигнал слишком короткий: {samples?.Length ?? 0} сэмплов " +
                $"(нужно минимум {FrameLength})");
            return result;
        }

        try
        {
            // Шаг 1. RMS и SNR — сначала, чтобы отсеять тишину
            result.Rms = ComputeRms(samples);
            result.Snr = EstimateSnr(samples);

            const float MinRmsForAnalysis = 0.005f;
            if (result.Rms < MinRmsForAnalysis)
            {
                EventBus.Warn("Harmonic",
                    $"Сигнал слишком тихий: RMS={result.Rms:F5} < {MinRmsForAnalysis}");
                result.HarmonicFrequencies = new float[HarmonicCount];
                result.HarmonicAmplitudes = new float[HarmonicCount];
                result.HarmonicAmplitudesDb = new float[HarmonicCount];
                result.Formants = new float[FormantCount];
                return result;
            }

            // Шаг 2. Самый громкий кадр
            float[] frame = FindLoudestFrame(samples);

            // Шаг 3. Pre-emphasis + окно Ханна
            float[] prepared = PrepareFrame(frame);

            // Шаг 4. FFT и амплитудный спектр
            Complex[] spectrum = ComputeSpectrum(prepared);
            float[] magnitude = ComputeMagnitude(spectrum);

            // Шаг 5. Три оценки F0
            result.F0Cepstrum = EstimateF0Cepstrum(spectrum);
            result.F0Hps = EstimateF0Hps(magnitude);
            // АКФ работает по СЫРОМУ кадру — pre-emphasis внутри метода.
            result.F0Autocorrelation = EstimateF0Autocorrelation(frame);

            // Шаг 6. Голосование с фиксом октавной ошибки
            (float f0, bool agreed) = VoteF0(
                result.F0Cepstrum,
                result.F0Hps,
                result.F0Autocorrelation);
            result.F0 = f0;
            result.F0Agreed = agreed;

            // Шаг 7. Гармоники
            if (f0 > 0)
                ExtractHarmonics(magnitude, f0, result);
            else
            {
                result.HarmonicFrequencies = new float[HarmonicCount];
                result.HarmonicAmplitudes = new float[HarmonicCount];
                result.HarmonicAmplitudesDb = new float[HarmonicCount];
            }

            // Шаг 8. Форманты (LPC)
            ExtractFormants(prepared, result);

            EventBus.Info("Harmonic",
                $"F0: cepstrum={result.F0Cepstrum:F1}, hps={result.F0Hps:F1}, " +
                $"autocorr={result.F0Autocorrelation:F1} -> итог={f0:F1} Гц " +
                $"(согласие: {(agreed ? "да" : "нет")})");
        }
        catch (Exception ex)
        {
            EventBus.Error("Harmonic", $"Ошибка анализа: {ex.Message}");
        }

        return result;
    }

    // ============================================================
    // ===== БЫСТРЫЙ АНАЛИЗ (только F0) =====
    // ============================================================

    /// <summary>
    /// Быстрый анализ: только F0 (кепстр + HPS + автокорреляция + голосование).
    /// Без извлечения гармоник и формант — в 3–4 раза быстрее полного Analyze.
    /// Используется в пакетном анализе по кадрам (см. HarmonicBatchAnalysis).
    /// </summary>
    public static F0OnlyResult AnalyzeF0Only(float[] frame)
    {
        var result = new F0OnlyResult();
        if (frame == null || frame.Length < FrameLength)
            return result;

        result.Rms = ComputeRms(frame);
        const float MinRmsForAnalysis = 0.005f;
        if (result.Rms < MinRmsForAnalysis)
            return result;

        result.Snr = EstimateSnr(frame);

        float[] prepared = PrepareFrame(frame);
        Complex[] spectrum = ComputeSpectrum(prepared);
        float[] magnitude = ComputeMagnitude(spectrum);

        result.F0Cepstrum = EstimateF0Cepstrum(spectrum);
        result.F0Hps = EstimateF0Hps(magnitude);
        result.F0Autocorrelation = EstimateF0Autocorrelation(frame);

        (float f0, bool agreed) = VoteF0(
            result.F0Cepstrum,
            result.F0Hps,
            result.F0Autocorrelation);
        result.F0 = f0;
        result.F0Agreed = agreed;

        return result;
    }

    // ============================================================
    // ===== СПЕКТРОГРАММА (для UI) =====
    // ============================================================

    /// <summary>
    /// Считает спектрограмму: матрицу [частотный бин, кадр] в дБ.
    /// </summary>
    public static SpectrogramResult ComputeSpectrogramDb(
        float[] samples,
        int frameLength = 1024,
        int hopLength = 256,
        int fftSize = 1024)
    {
        var result = new SpectrogramResult();
        if (samples == null || samples.Length < frameLength)
            return result;

        int frameCount = (samples.Length - frameLength) / hopLength + 1;
        int halfFft = fftSize / 2 + 1;
        double[,] data = new double[halfFft, frameCount];

        double binHz = (double)SampleRate / fftSize;
        double frameSec = (double)hopLength / SampleRate;

        float[] preemph = new float[samples.Length];
        preemph[0] = samples[0];
        for (int i = 1; i < samples.Length; i++)
            preemph[i] = samples[i] - PreEmphasis * samples[i - 1];

        Complex[] buffer = new Complex[fftSize];

        for (int f = 0; f < frameCount; f++)
        {
            int start = f * hopLength;

            Array.Clear(buffer, 0, buffer.Length);
            for (int i = 0; i < frameLength; i++)
            {
                double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (frameLength - 1)));
                buffer[i] = new Complex(preemph[start + i] * w, 0);
            }

            Fft(buffer);

            for (int k = 0; k < halfFft; k++)
            {
                double mag = buffer[k].Magnitude;
                data[k, f] = 20.0 * Math.Log10(mag + 1e-10);
            }
        }

        result.Data = data;
        result.FreqBins = halfFft;
        result.FrameCount = frameCount;
        result.BinHz = binHz;
        result.FrameSec = frameSec;
        result.TotalSeconds = (double)samples.Length / SampleRate;
        return result;
    }

    // ============================================================
    // ===== ВЫБОР КАДРА =====
    // ============================================================

    private static float[] FindLoudestFrame(float[] samples)
    {
        int hop = FrameLength / 2;
        int bestStart = 0;
        double bestRms = -1.0;

        for (int start = 0; start + FrameLength <= samples.Length; start += hop)
        {
            double sum = 0;
            for (int i = 0; i < FrameLength; i++)
            {
                float s = samples[start + i];
                sum += s * s;
            }
            double rms = Math.Sqrt(sum / FrameLength);
            if (rms > bestRms) { bestRms = rms; bestStart = start; }
        }

        float[] frame = new float[FrameLength];
        Array.Copy(samples, bestStart, frame, 0, FrameLength);
        return frame;
    }

    private static float[] PrepareFrame(float[] frame)
    {
        float[] result = new float[FrameLength];
        result[0] = frame[0];
        for (int i = 1; i < FrameLength; i++)
            result[i] = frame[i] - PreEmphasis * frame[i - 1];

        for (int i = 0; i < FrameLength; i++)
        {
            double w = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FrameLength - 1)));
            result[i] *= (float)w;
        }
        return result;
    }

    // ============================================================
    // ===== FFT =====
    // ============================================================

    private static void Fft(Complex[] buffer)
    {
        int n = buffer.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            Complex wlen = new Complex(Math.Cos(ang), Math.Sin(ang));

            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                int half = len / 2;
                for (int j = 0; j < half; j++)
                {
                    Complex u = buffer[i + j];
                    Complex v = buffer[i + j + half] * w;
                    buffer[i + j] = u + v;
                    buffer[i + j + half] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    private static void Ifft(Complex[] buffer)
    {
        int n = buffer.Length;
        for (int i = 0; i < n; i++)
            buffer[i] = Complex.Conjugate(buffer[i]);
        Fft(buffer);
        for (int i = 0; i < n; i++)
            buffer[i] = Complex.Conjugate(buffer[i]) / n;
    }

    private static Complex[] ComputeSpectrum(float[] frame)
    {
        Complex[] buffer = new Complex[FftSize];
        for (int i = 0; i < FrameLength; i++)
            buffer[i] = new Complex(frame[i], 0);
        Fft(buffer);
        return buffer;
    }

    private static float[] ComputeMagnitude(Complex[] spectrum)
    {
        int half = FftSize / 2 + 1;
        float[] mag = new float[half];
        for (int i = 0; i < half; i++)
            mag[i] = (float)spectrum[i].Magnitude;
        return mag;
    }

    // ============================================================
    // ===== МЕТОД 1: КЕПСТР =====
    // ============================================================

    private static float EstimateF0Cepstrum(Complex[] spectrum)
    {
        int n = FftSize;
        int half = n / 2;
        const float eps = 1e-10f;

        Complex[] logMag = new Complex[n];
        for (int i = 0; i <= half; i++)
        {
            double m = spectrum[i].Magnitude;
            logMag[i] = new Complex(Math.Log(m + eps), 0);
        }
        for (int i = 1; i < half; i++)
            logMag[n - i] = logMag[i];

        Ifft(logMag);

        int qMin = (int)(SampleRate / MaxF0);
        int qMax = (int)(SampleRate / MinF0);
        if (qMax >= half) qMax = half - 1;

        float[] real = new float[half];
        for (int i = 0; i < half; i++) real[i] = (float)logMag[i].Real;

        int peakIdx = ArgMax(real, qMin, qMax);
        if (peakIdx < 0) return 0f;

        float refined = ParabolicRefine(real, peakIdx);
        if (refined < 1f) return 0f;

        return SampleRate / refined;
    }

    // ============================================================
    // ===== МЕТОД 2: HPS =====
    // ============================================================

    private static float EstimateF0Hps(float[] magnitude)
    {
        const int MaxHarmonics = 5;
        int half = magnitude.Length;
        int hpsSize = half / MaxHarmonics;
        float[] hps = new float[hpsSize];

        for (int i = 0; i < hpsSize; i++)
        {
            float prod = 1f;
            for (int h = 1; h <= MaxHarmonics; h++)
            {
                int idx = i * h;
                if (idx >= half) { prod = 0f; break; }
                prod *= magnitude[idx];
            }
            hps[i] = prod;
        }

        float binHz = (float)SampleRate / FftSize;
        int minBin = (int)(MinF0 / binHz);
        int maxBin = (int)(MaxF0 / binHz);
        if (maxBin >= hpsSize) maxBin = hpsSize - 1;

        int peakIdx = ArgMax(hps, minBin, maxBin);
        if (peakIdx < 0) return 0f;

        float refined = ParabolicRefine(hps, peakIdx);
        if (refined < 1f) return 0f;

        return refined * binHz;
    }

    // ============================================================
    // ===== МЕТОД 3: АВТОКОРРЕЛЯЦИЯ =====
    // ============================================================

    /// <summary>
    /// Оценка F0 через нормированную автокорреляционную функцию.
    ///
    /// Алгоритм:
    ///   1. Pre-emphasis на копии кадра.
    ///   2. АКФ в диапазоне лагов [SR/MaxF0 .. SR/MinF0].
    ///   3. Нормировка на r[0].
    ///   4. Проверка качества: глобальный пик &lt; 0.30 → невокализован.
    ///   5. Первый локальный пик ≥ 30 % от глобального — защита от октавы.
    ///   6. Параболическое уточнение.
    /// </summary>
    private static float EstimateF0Autocorrelation(float[] frame)
    {
        // 1. Pre-emphasis на копии кадра
        float[] pe = new float[frame.Length];
        pe[0] = frame[0];
        for (int i = 1; i < frame.Length; i++)
            pe[i] = frame[i] - PreEmphasis * frame[i - 1];

        // 2. Границы поиска
        int minLag = (int)(SampleRate / MaxF0);
        int maxLag = (int)(SampleRate / MinF0);

        const int MinWindowMs = 40;
        int minWindowSamples = SampleRate * MinWindowMs / 1000;
        int safeMaxLag = pe.Length - minWindowSamples;

        if (maxLag > safeMaxLag) maxLag = safeMaxLag;
        if (maxLag <= minLag)
        {
            EventBus.Warn("Harmonic",
                $"АКФ: недостаточно данных (frame={pe.Length}, safeMaxLag={safeMaxLag})");
            return 0f;
        }

        // 3. Расчёт АКФ
        double[] r = new double[maxLag + 1];
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            int count = pe.Length - lag;
            for (int i = 0; i < count; i++)
                sum += pe[i] * pe[i + lag];
            r[lag] = sum / count;
        }

        if (r[0] < 1e-10) return 0f;

        double r0 = r[0];
        for (int i = 0; i <= maxLag; i++) r[i] /= r0;

        float[] rF = new float[maxLag + 1];
        for (int i = 0; i <= maxLag; i++) rF[i] = (float)r[i];

        // 4. Оценка потолка
        int globalPeak = ArgMax(rF, minLag, maxLag);
        if (globalPeak < 0) return 0f;

        float globalPeakValue = rF[globalPeak];

        const float QualityThreshold = 0.30f;
        if (globalPeakValue < QualityThreshold)
        {
            EventBus.Info("Harmonic",
                $"АКФ: пик {globalPeakValue:F3} < {QualityThreshold} — " +
                $"сигнал невокализован");
            return 0f;
        }

        // 5. Первый локальный пик >= 30% от глобального
        const float RelativeThreshold = 0.30f;
        float minAcceptableHeight = globalPeakValue * RelativeThreshold;

        int firstGoodPeak = -1;
        for (int i = minLag; i <= maxLag; i++)
        {
            bool isLocalPeak =
                (i == 0 || rF[i] >= rF[i - 1]) &&
                (i == maxLag || rF[i] >= rF[i + 1]);

            if (isLocalPeak && rF[i] >= minAcceptableHeight)
            {
                firstGoodPeak = i;
                break;
            }
        }

        int peakIdx = firstGoodPeak >= 0 ? firstGoodPeak : globalPeak;

        // 6. Уточнение пика
        float refined = ParabolicRefine(rF, peakIdx);
        if (refined < 1f) return 0f;

        float f0 = SampleRate / refined;

        EventBus.Info("Harmonic",
            $"АКФ: пик={peakIdx}, r={rF[peakIdx]:F3}, " +
            $"global={globalPeakValue:F3}, F0={f0:F1} Гц");

        return f0;
    }

    // ============================================================
    // ===== ГОЛОСОВАНИЕ С ФИКСОМ ОКТАВЫ =====
    // ============================================================

    private static (float f0, bool agreed) VoteF0(
        float cepstrum, float hps, float autocorr)
    {
        List<float> valid = new List<float>(3);
        if (cepstrum > 0f) valid.Add(cepstrum);
        if (hps > 0f) valid.Add(hps);
        if (autocorr > 0f) valid.Add(autocorr);

        if (valid.Count == 0) return (0f, false);
        if (valid.Count == 1) return (valid[0], false);

        const float Tolerance = 0.10f;

        if (valid.Count == 3)
        {
            float maxV = valid.Max();
            float minV = valid.Min();
            if ((maxV - minV) / maxV < Tolerance)
                return ((cepstrum + hps + autocorr) / 3f, true);
        }

        for (int i = 0; i < valid.Count; i++)
        {
            for (int j = i + 1; j < valid.Count; j++)
            {
                float maxV = MathF.Max(valid[i], valid[j]);
                float minV = MathF.Min(valid[i], valid[j]);
                if ((maxV - minV) / maxV < Tolerance)
                    return ((valid[i] + valid[j]) / 2f, true);
            }
        }

        if (cepstrum > 0f && hps > 0f)
        {
            float ratio = hps / cepstrum;
            if (MathF.Abs(ratio - 2f) < 0.10f || MathF.Abs(ratio - 0.5f) < 0.10f)
            {
                EventBus.Info("Harmonic",
                    $"Октавная ошибка HPS: hps={hps:F1} ≈ 2×cepstrum={cepstrum:F1} " +
                    $"— берём cepstrum");
                return (cepstrum, false);
            }
        }

        if (cepstrum > 0f) return (cepstrum, false);
        if (autocorr > 0f) return (autocorr, false);
        return (hps, false);
    }

    // ============================================================
    // ===== ГАРМОНИКИ =====
    // ============================================================

    private static void ExtractHarmonics(
        float[] magnitude, float f0, HarmonicAnalysisResult result)
    {
        float[] freqs = new float[HarmonicCount];
        float[] amps = new float[HarmonicCount];
        float[] ampsDb = new float[HarmonicCount];

        float binHz = (float)SampleRate / FftSize;
        const int SearchWindow = 3;

        for (int k = 1; k <= HarmonicCount; k++)
        {
            float targetFreq = f0 * k;
            freqs[k - 1] = targetFreq;

            if (targetFreq >= SampleRate / 2f)
            {
                amps[k - 1] = 0f;
                ampsDb[k - 1] = -120f;
                continue;
            }

            int centerBin = (int)MathF.Round(targetFreq / binHz);
            int lo = Math.Max(0, centerBin - SearchWindow);
            int hi = Math.Min(magnitude.Length - 1, centerBin + SearchWindow);

            float maxAmp = 0f;
            for (int i = lo; i <= hi; i++)
                if (magnitude[i] > maxAmp) maxAmp = magnitude[i];

            amps[k - 1] = maxAmp;
            ampsDb[k - 1] = maxAmp > 0f ? 20f * MathF.Log10(maxAmp) : -120f;
        }

        result.HarmonicFrequencies = freqs;
        result.HarmonicAmplitudes = amps;
        result.HarmonicAmplitudesDb = ampsDb;
    }

    // ============================================================
    // ===== ФОРМАНТЫ (LPC) =====
    // ============================================================

    private static void ExtractFormants(
        float[] preparedFrame, HarmonicAnalysisResult result)
    {
        double[] r = new double[LpcOrder + 1];
        for (int lag = 0; lag <= LpcOrder; lag++)
        {
            double sum = 0;
            int count = FrameLength - lag;
            for (int i = 0; i < count; i++)
                sum += preparedFrame[i] * preparedFrame[i + lag];
            r[lag] = sum;
        }

        if (r[0] < 1e-12)
        {
            result.Formants = new float[FormantCount];
            return;
        }

        double[] a = LevinsonDurbin(r, LpcOrder);

        const int GridSize = 1024;
        float[] env = new float[GridSize];

        for (int g = 0; g < GridSize; g++)
        {
            double f = g * (SampleRate / 2.0) / GridSize;
            double omega = 2.0 * Math.PI * f / SampleRate;

            Complex denom = Complex.One;
            for (int k = 1; k <= LpcOrder; k++)
                denom += a[k] * Complex.Exp(new Complex(0, -omega * k));

            double mag = denom.Magnitude;
            env[g] = mag > 1e-12 ? (float)(1.0 / mag) : 0f;
        }

        float envMax = 0f;
        for (int i = 0; i < GridSize; i++)
            if (env[i] > envMax) envMax = env[i];

        if (envMax <= 0f)
        {
            result.Formants = new float[FormantCount];
            return;
        }

        const float MinHeightRatio = 0.05f;
        float minHeight = envMax * MinHeightRatio;

        int prominenceWindow = (int)(300.0 / (SampleRate / 2.0) * GridSize);

        List<(float Freq, float Prominence)> candidates = new();

        for (int i = 1; i < GridSize - 1; i++)
        {
            if (env[i] <= env[i - 1] || env[i] <= env[i + 1]) continue;
            if (env[i] < minHeight) continue;

            float freq = i * (SampleRate / 2f) / GridSize;
            if (freq < 90f || freq > 5500f) continue;

            float minLeft = env[i];
            for (int j = Math.Max(0, i - prominenceWindow); j < i; j++)
                if (env[j] < minLeft) minLeft = env[j];

            float minRight = env[i];
            for (int j = i + 1; j <= Math.Min(GridSize - 1, i + prominenceWindow); j++)
                if (env[j] < minRight) minRight = env[j];

            float prominence = env[i] - Math.Max(minLeft, minRight);
            if (prominence <= 0f) continue;

            candidates.Add((freq, prominence));
        }

        var top = candidates
            .OrderByDescending(c => c.Prominence)
            .Take(FormantCount)
            .OrderBy(c => c.Freq)
            .ToList();

        float[] result4 = new float[FormantCount];
        for (int i = 0; i < FormantCount; i++)
            result4[i] = i < top.Count ? top[i].Freq : 0f;

        result.Formants = result4;

        if (top.Count > 0)
        {
            EventBus.Info("Harmonic",
                $"Форманты: {string.Join(", ",
                    result4.Select(f => f > 0f ? $"{f:F0}" : "-"))} Гц " +
                $"(найдено {top.Count} из {FormantCount})");
        }
    }

    private static double[] LevinsonDurbin(double[] r, int order)
    {
        double[] a = new double[order + 1];
        double[] aPrev = new double[order + 1];
        a[0] = 1.0;

        double error = r[0];
        for (int i = 1; i <= order; i++)
        {
            double k = r[i];
            for (int j = 1; j < i; j++) k -= a[j] * r[i - j];

            if (Math.Abs(error) < 1e-12) break;
            k /= error;

            for (int j = 1; j < i; j++) aPrev[j] = a[j] - k * a[i - j];
            aPrev[i] = k;
            for (int j = 1; j <= i; j++) a[j] = aPrev[j];

            error *= (1.0 - k * k);
            if (error <= 1e-12) break;
        }
        return a;
    }

    // ============================================================
    // ===== RMS / SNR =====
    // ============================================================

    private static float ComputeRms(float[] samples)
    {
        if (samples.Length == 0) return 0f;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float EstimateSnr(float[] samples)
    {
        int hop = FrameLength / 2;
        List<float> frameRms = new List<float>();
        for (int start = 0; start + FrameLength <= samples.Length; start += hop)
        {
            double sum = 0;
            for (int i = 0; i < FrameLength; i++)
            {
                float s = samples[start + i];
                sum += s * s;
            }
            frameRms.Add((float)Math.Sqrt(sum / FrameLength));
        }

        if (frameRms.Count < 4) return 0f;
        frameRms.Sort();

        int low = Math.Max(1, frameRms.Count / 5);
        int high = frameRms.Count - low;

        float noise = 0f, signal = 0f;
        for (int i = 0; i < low; i++) noise += frameRms[i];
        for (int i = high; i < frameRms.Count; i++) signal += frameRms[i];

        noise /= low;
        signal /= Math.Max(1, frameRms.Count - high);

        if (noise < 1e-10f) return 60f;
        return 20f * MathF.Log10(signal / noise);
    }

    // ============================================================
    // ===== ВСПОМОГАТЕЛЬНОЕ =====
    // ============================================================

    private static int ArgMax(float[] arr, int lo, int hi)
    {
        if (arr.Length == 0) return -1;
        if (lo < 0) lo = 0;
        if (hi >= arr.Length) hi = arr.Length - 1;
        if (lo > hi) return -1;

        int best = lo;
        float bestVal = arr[lo];
        for (int i = lo + 1; i <= hi; i++)
            if (arr[i] > bestVal) { bestVal = arr[i]; best = i; }
        return best;
    }

    private static float ParabolicRefine(float[] arr, int idx)
    {
        if (idx <= 0 || idx >= arr.Length - 1) return idx;

        float a = arr[idx - 1];
        float b = arr[idx];
        float c = arr[idx + 1];

        float denom = a - 2f * b + c;
        if (MathF.Abs(denom) < 1e-10f) return idx;

        float delta = 0.5f * (a - c) / denom;
        if (delta > 1f) delta = 1f;
        if (delta < -1f) delta = -1f;

        return idx + delta;
    }
}