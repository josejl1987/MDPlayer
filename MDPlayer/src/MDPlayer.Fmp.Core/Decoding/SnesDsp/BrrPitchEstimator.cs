namespace Fmp.Core.Decoding.SnesDsp;
/// <summary>Classification of why a pitch estimate was produced (§15.2).</summary>
internal enum PitchEstimateReason { Periodic, Harmonic, LowConfidence, Aperiodic, TooShort }
/// <summary>Accuracy for an instrument root (§15.4). The UI must never label an estimated
/// pitch as Exact; estimation only yields Estimated/Relative/Unpitched.</summary>
internal enum PitchAccuracy { Exact, Estimated, Relative, Unpitched }
/// <summary>Pitch estimate: frequency in Hz, 0..1 confidence, and the reason.</summary>
internal readonly record struct PitchEstimate(double FrequencyHz, double Confidence, PitchEstimateReason Reason);
/// <summary>Musical root-frequency estimator for BRR samples (§15). Pipeline per §15.2:
/// decode at 32 kHz, remove DC, detect voiced regions, prefer the stable loop region,
/// exclude the attack transient, window, run a YIN-style difference function plus
/// normalized autocorrelation, evaluate octave candidates, and cross-check with zero
/// crossings and harmonic consistency. Range 25-5000 Hz (§15.3). Deterministic.</summary>
internal static class BrrPitchEstimator
{
    public const int NominalSampleRateHz = BrrDecoder.SampleRateHz; // S-DSP sample clock (§15.1)
    public const double MinFrequencyHz = 25.0;
    public const double MaxFrequencyHz = 5000.0;
    private const double YinThreshold = 0.15;
    private const double AperiodicThresholdR = 0.35;
    private const int MaxAnalysisSamples = 8192;

    /// <summary>Estimate the root of a BRR sample (decoded at nominal rate).</summary>
    public static PitchEstimate Estimate(BrrSample sample) =>
        EstimateFromPcm(SelectAnalysisRegion(sample), NominalSampleRateHz);

    /// <summary>Estimate the root of plain 16-bit mono PCM. Unit-testable core; the BRR path
    /// decodes to PCM and delegates here.</summary>
    public static PitchEstimate EstimateFromPcm(short[] pcm, int sampleRate)
    {
        if (pcm == null || pcm.Length == 0 || sampleRate <= 0)
            return TooShort;
        int n = Math.Min(pcm.Length, MaxAnalysisSamples);
        var x = new double[n];
        double mean = 0;
        for (int i = 0; i < n; i++)
            mean += (x[i] = pcm[i]);
        mean /= n;
        for (int i = 0; i < n; i++)
            x[i] -= mean; // remove DC offset
        int start = 0, end = 0;
        if (!VoicedRegion(x, ref start, ref end))
            return TooShort;
        int len = end - start;
        mean = 0;
        for (int i = start; i < end; i++)
            mean += x[i];
        mean /= len;
        double peak = 0, rms = 0;
        for (int i = start; i < end; i++)
        {
            x[i] -= mean; // re-remove DC inside the voiced window
            peak = Math.Max(peak, Math.Abs(x[i]));
            rms += x[i] * x[i];
        }
        if (peak < 32.0 || Math.Sqrt(rms / len) < 4.0)
            return TooShort;
        int minLag = Math.Max(2, (int)(sampleRate / MaxFrequencyHz));
        int maxLag = Math.Min((int)(sampleRate / MinFrequencyHz), len / 2 - 1);
        if (maxLag < minLag || len < 96)
            return TooShort;
        // YIN difference function + cumulative-mean-normalized difference (cmndf).
        var cmndf = new double[maxLag + 1];
        double runningSum = 0;
        for (int tau = minLag; tau <= maxLag; tau++)
        {
            double sum = 0;
            for (int i = 0; i < len - tau; i++)
            {
                double diff = x[start + i] - x[start + i + tau];
                sum += diff * diff;
            }
            double dtau = sum / (len - tau);
            runningSum += dtau;
            cmndf[tau] = dtau / (runningSum / tau);
        }
        int tauYin = FirstYinDip(cmndf, minLag, maxLag);
        int tauBest = PickBestLag(x, start, len, tauYin, minLag, maxLag, out double harmonic);
        double frequency = sampleRate / (double)tauBest;
        double rBest = Autocorrelation(x, start, len, tauBest);
        double depth = Clamp01(1.0 - cmndf[tauYin]);
        double zc = ZeroCrossingFrequency(x, start, len, sampleRate);
        double zcAgree = zc > 0 ? Clamp01(1.0 - Math.Abs(Math.Log2(zc / frequency))) : 0.0;
        double confidence = Clamp01(0.55 * rBest + 0.20 * depth + 0.15 * harmonic + 0.10 * zcAgree);
        PitchEstimateReason reason = rBest < AperiodicThresholdR ? PitchEstimateReason.Aperiodic
            : rBest >= 0.80 && depth >= 0.85 && zcAgree >= 0.70 ? PitchEstimateReason.Periodic
            : rBest >= 0.55 ? PitchEstimateReason.Harmonic
            : PitchEstimateReason.LowConfidence;
        return new PitchEstimate(frequency, confidence, reason);
    }
    /// <summary>§15.4 accuracy: aperiodic is always Unpitched, too-short is Relative, and
    /// confidence ≥ 0.65 is Estimated (0.65-0.85 renders dotted/warning in the UI).</summary>
    public static PitchAccuracy Classify(PitchEstimate estimate)
    {
        if (estimate.Reason == PitchEstimateReason.Aperiodic)
            return PitchAccuracy.Unpitched;
        if (estimate.Reason == PitchEstimateReason.TooShort)
            return PitchAccuracy.Relative;
        return estimate.Confidence >= 0.65 ? PitchAccuracy.Estimated : PitchAccuracy.Relative;
    }
    /// <summary>Lowercase accuracy string ('estimated' | 'relative' | 'unpitched'); never 'exact'.</summary>
    public static string AccuracyString(PitchEstimate estimate) =>
        Classify(estimate).ToString().ToLowerInvariant();
    // §15.2 region: loop region when looping, else the full sample minus the ~10 ms attack.
    private static short[] SelectAnalysisRegion(BrrSample sample)
    {
        if (sample == null || !sample.Valid || sample.EncodedBlocks == null || sample.EncodedBlocks.Length == 0)
            return Array.Empty<short>();
        if (sample.Loops && BrrDecoder.LoopBlockIndex(sample) >= 0)
            return Trim(BrrDecoder.DecodeLoopRegion(sample));
        short[] full = BrrDecoder.Decode(sample);
        int skip = Math.Min(full.Length / 4, 320);
        int length = full.Length - skip;
        if (length <= 0)
            return Array.Empty<short>();
        var region = new short[length];
        Array.Copy(full, skip, region, 0, length);
        return Trim(region);
    }
    private static short[] Trim(short[] data) =>
        data.Length <= MaxAnalysisSamples ? data : data[..MaxAnalysisSamples];
    // Contiguous voiced region (§15.2 step 3): trim leading/trailing samples below -60 dB of peak.
    private static bool VoicedRegion(double[] x, ref int start, ref int end)
    {
        int n = x.Length;
        double peak = 0;
        for (int i = 0; i < n; i++)
            peak = Math.Max(peak, Math.Abs(x[i]));
        if (peak < 1.0)
            return false;
        double floor = peak / 1000.0;
        int first = 0, last = n - 1;
        while (first < n && Math.Abs(x[first]) < floor)
            first++;
        while (last > first && Math.Abs(x[last]) < floor)
            last--;
        start = first;
        end = last + 1;
        return end - start >= 96;
    }
    // YIN pick: first local minimum of the normalized difference below the threshold, else deepest.
    private static int FirstYinDip(double[] cmndf, int minLag, int maxLag)
    {
        int firstDip = -1, globalTau = minLag;
        double globalMin = double.MaxValue;
        for (int tau = minLag + 1; tau < maxLag; tau++)
        {
            if (cmndf[tau] >= cmndf[tau - 1] || cmndf[tau] > cmndf[tau + 1])
                continue;
            if (cmndf[tau] < globalMin)
            {
                globalMin = cmndf[tau];
                globalTau = tau;
            }
            if (firstDip < 0 && cmndf[tau] < YinThreshold)
                firstDip = tau;
        }
        return firstDip >= 0 ? firstDip : globalTau;
    }
    // Octave candidates scored by autocorrelation plus harmonic support; ties prefer smaller lag.
    private static int PickBestLag(double[] x, int start, int len, int tauYin, int minLag, int maxLag, out double harmonic)
    {
        var candidates = new List<int>();
        foreach (int tau in new[] { tauYin / 4, tauYin / 2, tauYin, tauYin * 2, tauYin * 4 })
        {
            if (tau >= minLag && tau <= maxLag && !candidates.Contains(tau))
                candidates.Add(tau);
        }
        candidates.Sort();
        int bestTau = candidates[0];
        double bestScore = double.NegativeInfinity;
        harmonic = 0;
        foreach (int tau in candidates)
        {
            double r = Autocorrelation(x, start, len, tau);
            int k = Math.Min(6, maxLag / tau);
            double sum = r;
            int count = 1;
            for (int m = 2; m <= k; m++)
            {
                int t = m * tau;
                if (t <= maxLag)
                {
                    sum += Autocorrelation(x, start, len, t);
                    count++;
                }
            }
            double h = sum / count;
            double score = r + 0.15 * h;
            if (score > bestScore)
            {
                bestScore = score;
                bestTau = tau;
                harmonic = h;
            }
        }
        return bestTau;
    }
    // Pearson autocorrelation of the window shifted by `tau` (robust to scaling).
    private static double Autocorrelation(double[] x, int start, int len, int tau)
    {
        int count = len - tau;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < count; i++)
        {
            double a = x[start + i];
            double b = x[start + i + tau];
            sxy += a * b;
            sxx += a * a;
            syy += b * b;
        }
        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
    // Zero-crossing fundamental (DC removed); sanity cross-check only.
    private static double ZeroCrossingFrequency(double[] x, int start, int len, int sampleRate)
    {
        int crossings = 0;
        for (int i = 1; i < len; i++)
            if ((x[start + i - 1] < 0 && x[start + i] >= 0) || (x[start + i - 1] >= 0 && x[start + i] < 0))
                crossings++;
        return crossings * (double)sampleRate / (2.0 * len);
    }
    private static double Clamp01(double value) =>
        value < 0 ? 0 : value > 1 ? 1 : value;
    private static PitchEstimate TooShort => new(0, 0, PitchEstimateReason.TooShort);
}
