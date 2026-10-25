using TVE.Core.Analysis;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// =============================================================
// Тест 1: чистый синус 100 Гц
// =============================================================

Console.WriteLine("=== Тест 1: чистый синус 100 Гц (double для фазы) ===\n");

int sr = HarmonicAnalyzer.SampleRate;
int n = sr; // 1 секунда
float[] signal = new float[n];

// ВАЖНО: фаза считается в double, потом приводится к float.
// Если считать в float — при i=16000 аргумент ~10^7, точность теряется.
for (int i = 0; i < n; i++)
{
    double phase = 2.0 * Math.PI * 100.0 * i / sr;
    signal[i] = (float)(0.5 * Math.Sin(phase));
}

var res = HarmonicAnalyzer.Analyze(signal);

Console.WriteLine($"  Ожидание F0: 100 Гц");
Console.WriteLine($"  Cepstrum:    {res.F0Cepstrum:F2}");
Console.WriteLine($"  HPS:         {res.F0Hps:F2}   (может не совпасть — синус без гармоник)");
Console.WriteLine($"  Autocorr:    {res.F0Autocorrelation:F2}");
Console.WriteLine($"  Итог:        {res.F0:F2} Гц");
Console.WriteLine($"  Согласие:    {res.F0Agreed}");
Console.WriteLine($"  RMS:         {res.Rms:F5}  (ожидание: 0.354)");
Console.WriteLine($"  SNR:         {res.Snr:F1} дБ");
Console.WriteLine();

// =============================================================
// Тест 2: пилообразный 150 Гц (главный тест)
// =============================================================

Console.WriteLine("=== Тест 2: пилообразный 150 Гц (главный тест) ===\n");

float[] saw = new float[n];
for (int i = 0; i < n; i++)
{
    double phase = 150.0 * i / sr;
    double frac = phase - Math.Floor(phase + 0.5);
    saw[i] = (float)(0.4 * 2.0 * frac);
}

res = HarmonicAnalyzer.Analyze(saw);

Console.WriteLine($"  Итог F0:    {res.F0:F2} Гц  (ожидание: 150)");
Console.WriteLine($"  Согласие:   {res.F0Agreed}");
Console.WriteLine();
Console.WriteLine("  Гармоники:");
for (int k = 0; k < res.HarmonicFrequencies.Length; k++)
{
    Console.WriteLine(
        $"    k{k + 1}: {res.HarmonicFrequencies[k],7:F1} Гц  " +
        $"{res.HarmonicAmplitudes[k],8:F4}  {res.HarmonicAmplitudesDb[k],7:F1} дБ");
}
Console.WriteLine();

// =============================================================
// Тест 3: тишина
// =============================================================

Console.WriteLine("=== Тест 3: тишина (должна дать F0=0) ===\n");

float[] silence = new float[sr];
res = HarmonicAnalyzer.Analyze(silence);
Console.WriteLine($"  F0:       {res.F0}  (ожидание: 0)");
Console.WriteLine($"  RMS:      {res.Rms}  (ожидание: 0)");
Console.WriteLine($"  Согласие: {res.F0Agreed}");
Console.WriteLine();

// =============================================================
// Тест 4: белый шум
// =============================================================

Console.WriteLine("=== Тест 4: белый шум (F0 нестабилен) ===\n");

float[] noise = new float[sr];
var rnd = new Random(42);
for (int i = 0; i < noise.Length; i++)
    noise[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.3);

res = HarmonicAnalyzer.Analyze(noise);
Console.WriteLine($"  F0:       {res.F0:F2}");
Console.WriteLine($"  Согласие: {res.F0Agreed}  (ожидание: false)");
Console.WriteLine();

// =============================================================
// Тест 5: диагностика АКФ на синтетическом сигнале 120 Гц
// =============================================================

Console.WriteLine("=== Тест 5: диагностика АКФ (сигнал 120 Гц, 3 гармоники) ===\n");

float[] sig120 = new float[sr];
for (int i = 0; i < sr; i++)
{
    double phase = 2.0 * Math.PI * 120.0 * i / sr;
    sig120[i] = (float)(0.5 * Math.Sin(phase)
                        + 0.3 * Math.Sin(2 * phase)
                        + 0.2 * Math.Sin(3 * phase));
}

var res5 = HarmonicAnalyzer.Analyze(sig120);
Console.WriteLine($"  Ожидание F0: 120 Гц");
Console.WriteLine($"  Autocorr:    {res5.F0Autocorrelation:F2}");
Console.WriteLine($"  Cepstrum:    {res5.F0Cepstrum:F2}");
Console.WriteLine($"  HPS:         {res5.F0Hps:F2}");
Console.WriteLine($"  Итог:        {res5.F0:F2} Гц");
Console.WriteLine($"  Согласие:    {res5.F0Agreed}");
Console.WriteLine();

// =============================================================
// Тест 7: SLERP — математика на единичной сфере
// =============================================================

Console.WriteLine("=== Тест 7: SLERP ===\n");

// Два простых вектора размерности 3.
var v1 = new float[] { 1f, 0f, 0f };
var v2 = new float[] { 0f, 1f, 0f };

var s0 = EmbeddingMath.Slerp(v1, v2, 0.0f);
var s1 = EmbeddingMath.Slerp(v1, v2, 1.0f);
var s5 = EmbeddingMath.Slerp(v1, v2, 0.5f);

Console.WriteLine($"  slerp(v1, v2, 0.0) = [{string.Join(", ", s0.Select(x => x.ToString("F4")))}]  (ожидание: [1, 0, 0])");
Console.WriteLine($"  slerp(v1, v2, 0.5) = [{string.Join(", ", s5.Select(x => x.ToString("F4")))}]  (ожидание: [0.7071, 0.7071, 0])");
Console.WriteLine($"  slerp(v1, v2, 1.0) = [{string.Join(", ", s1.Select(x => x.ToString("F4")))}]  (ожидание: [0, 1, 0])");

float normMid = MathF.Sqrt(s5.Sum(x => x * x));
Console.WriteLine($"  |slerp(v1, v2, 0.5)| = {normMid:F6}  (ожидание: 1.000000)");
Console.WriteLine();

// =============================================================
// Тест 8: SLERP на почти совпадающих векторах
// =============================================================

Console.WriteLine("=== Тест 8: SLERP на почти совпадающих векторах ===\n");

var a = EmbeddingMath.L2Normalize(new float[] { 1f, 0.001f, 0f });
var b = EmbeddingMath.L2Normalize(new float[] { 1f, 0.002f, 0f });
var merged = EmbeddingMath.Slerp(a, b, 0.5f);

float normA = MathF.Sqrt(a.Sum(x => x * x));
float normB = MathF.Sqrt(b.Sum(x => x * x));
float normM = MathF.Sqrt(merged.Sum(x => x * x));

Console.WriteLine($"  |a| = {normA:F6}, |b| = {normB:F6}, |merged| = {normM:F6}  (ожидание: все 1.000000)");
Console.WriteLine($"  merged = [{string.Join(", ", merged.Select(x => x.ToString("F6")))}]");
Console.WriteLine();

// =============================================================
// Тест 9: MeanOnSphere
// =============================================================

Console.WriteLine("=== Тест 9: MeanOnSphere ===\n");

var vecs = new[]
{
    new float[] { 1f, 0f, 0f },
    new float[] { 0f, 1f, 0f },
    new float[] { 0f, 0f, 1f }
};

var mean = EmbeddingMath.MeanOnSphere(vecs);
float meanNorm = MathF.Sqrt(mean.Sum(x => x * x));
Console.WriteLine($"  mean = [{string.Join(", ", mean.Select(x => x.ToString("F4")))}]");
Console.WriteLine($"  |mean| = {meanNorm:F6}  (ожидание: 1.000000)");
Console.WriteLine();

Console.WriteLine("Готово. Нажмите любую клавишу.");
Console.ReadKey();