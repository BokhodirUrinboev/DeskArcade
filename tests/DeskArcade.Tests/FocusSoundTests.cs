using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace DeskArcade.Tests;

/// <summary>
/// The focus sounds: their level, no clipping, no join to hear (they are generated as they play, and generating in
/// buffers gives exactly what one long run gives), fades without a click, and the colour of the noises.
/// </summary>
public class FocusSoundTests
{
    const int Rate = FocusGenerator.Rate;
    readonly ITestOutputHelper _out;

    public FocusSoundTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Kinds => Enum.GetValues<FocusSoundKind>().Select(k => new object[] { k });

    static float[] Generate(FocusSoundKind kind, double seconds, uint seed = 42, int chunk = 512)
    {
        var gen = FocusGenerator.Create(kind, seed);
        var samples = new float[(int)(seconds * Rate)];
        for (int at = 0; at < samples.Length; at += chunk) gen.Fill(samples.AsSpan(at, Math.Min(chunk, samples.Length - at)));
        return samples;
    }

    static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (float x in s) sum += (double)x * x;
        return Math.Sqrt(sum / s.Length);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void LevelsAreSteady_AndNothingClips(FocusSoundKind kind)
    {
        var s = Generate(kind, 60);
        double rms = Rms(s), peak = s.Max(x => Math.Abs(x));
        double limited = s.Count(x => Math.Abs(x) > 0.7f) / (double)s.Length;
        var seconds = Enumerable.Range(0, 60).Select(i => Rms(s.AsSpan(i * Rate, Rate))).ToList();
        _out.WriteLine($"{kind}: rms {rms:0.000} ({20 * Math.Log10(rms):0.0} dBFS), peak {peak:0.000}, limited {limited:P4}, " +
            $"quietest second {seconds.Min():0.000}, loudest {seconds.Max():0.000}");
        Assert.InRange(rms, 0.08, 0.2);            // -22 to -14 dBFS: a background, not a blast
        Assert.True(peak < 0.99, $"peak {peak}");  // never at full scale
        Assert.True(limited < 0.001, $"{limited:P3} of the samples needed the limiter");
        Assert.True(seconds.Min() > rms * 0.35, "a second that drops out");
        Assert.True(seconds.Max() < rms * 2, "a second that jumps out");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void BuffersStitchWithoutAJoin(FocusSoundKind kind)
    {
        // the mixer asks for 512 samples at a time: any size of pieces gives the same sound as one long run
        var whole = Generate(kind, 5, seed: 7, chunk: 5 * Rate);
        Assert.Equal(whole, Generate(kind, 5, seed: 7, chunk: 512));
        Assert.Equal(whole, Generate(kind, 5, seed: 7, chunk: 333));
        // and nowhere does one sample leap from the last (a click), apart from the rain's drops and the café's clinks,
        // which are meant: those leaps stay far below full scale
        double leap = Enumerable.Range(1, whole.Length - 1).Max(i => Math.Abs(whole[i] - whole[i - 1]));
        _out.WriteLine($"{kind}: largest step between samples {leap:0.000}");
        Assert.True(leap < (kind is FocusSoundKind.Brown ? 0.05 : 0.6), $"a step of {leap}");
    }

    /// <summary>Power in the octave bands around 125 Hz to 4 kHz (Welch's method, Hann windows), in dB.</summary>
    static double[] OctaveBands(float[] s)
    {
        const int n = 8192;
        double[] centres = { 125, 250, 500, 1000, 2000, 4000 };
        var power = new double[centres.Length];
        var re = new double[n];
        var im = new double[n];
        for (int at = 0; at + n <= s.Length; at += n / 2)
        {
            for (int i = 0; i < n; i++)
            {
                re[i] = s[at + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
                im[i] = 0;
            }
            Fft(re, im);
            for (int b = 0; b < centres.Length; b++)
            {
                int lo = (int)Math.Ceiling(centres[b] / Math.Sqrt(2) * n / Rate), hi = (int)Math.Floor(centres[b] * Math.Sqrt(2) * n / Rate);
                for (int k = lo; k <= hi; k++) power[b] += re[k] * re[k] + im[k] * im[k];
            }
        }
        return power.Select(p => 10 * Math.Log10(p)).ToArray();
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0; k < len / 2; k++)
                {
                    double wr = Math.Cos(angle * k), wi = Math.Sin(angle * k);
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * wr - im[b] * wi, xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }

    /// <summary>The least-squares slope of the octave bands, in dB per octave.</summary>
    static double Slope(double[] bands)
    {
        double mx = (bands.Length - 1) / 2.0, my = bands.Average();
        double num = 0, den = 0;
        for (int i = 0; i < bands.Length; i++)
        {
            num += (i - mx) * (bands[i] - my);
            den += (i - mx) * (i - mx);
        }
        return num / den;
    }

    [Fact]
    public void BrownFallsOffFasterThanPink()
    {
        // per octave band: white noise rises 3 dB an octave, pink stays level, brown falls 3 dB
        double brown = Slope(OctaveBands(Generate(FocusSoundKind.Brown, 12))), pink = Slope(OctaveBands(Generate(FocusSoundKind.Pink, 12)));
        _out.WriteLine($"brown {brown:0.00} dB/octave, pink {pink:0.00} dB/octave");
        Assert.InRange(pink, -1.0, 1.0);
        Assert.InRange(brown, -4.0, -2.0);
        Assert.True(brown < pink - 2);
    }

    [Fact]
    public void RainAndTheCafeHaveTheirOwnShape()
    {
        // rain is a hiss: more in the kilohertz octaves than at 125 Hz; the café's murmur sits in the voice range
        var rain = OctaveBands(Generate(FocusSoundKind.Rain, 12));
        var cafe = OctaveBands(Generate(FocusSoundKind.Cafe, 12));
        _out.WriteLine("rain " + string.Join(" ", rain.Select(b => b.ToString("0.0"))) + " / café " + string.Join(" ", cafe.Select(b => b.ToString("0.0"))));
        Assert.True(rain[4] > rain[0] + 3, "rain without its hiss");
        Assert.True(cafe[2] > cafe[5] + 6, "the café's murmur is brighter than voices");
    }

    // ------------------------------------------------------------------ fading

    /// <summary>The gain the mix applied, sample by sample, where the raw sound is loud enough to tell.</summary>
    static List<(int At, double Gain)> Gains(float[] mixed, float[] raw) =>
        Enumerable.Range(0, mixed.Length).Where(i => Math.Abs(raw[i]) > 0.02).Select(i => (i, (double)mixed[i] / raw[i])).ToList();

    static float[] Raw(FocusSoundKind kind, uint mixSeed, int layer, int samples)
    {
        var gen = FocusGenerator.Create(kind, (mixSeed + (uint)layer) * 747796405u + 1);
        var raw = new float[samples];
        gen.Fill(raw);
        return raw;
    }

    static float[] Run(FocusMix mix, int samples)
    {
        var buf = new float[samples];
        for (int at = 0; at < samples; at += 512) mix.MixInto(buf.AsSpan(at, Math.Min(512, samples - at)));
        return buf;
    }

    [Fact]
    public void FadesInFromSilence_WithoutAClick()
    {
        var mix = new FocusMix(5);
        Assert.False(mix.Active);
        mix.Set(FocusSoundKind.Pink, 0.8, ducked: false);
        int n = 3 * Rate;
        var mixed = Run(mix, n);
        var gains = Gains(mixed, Raw(FocusSoundKind.Pink, 5, 0, n));
        Assert.True(Math.Abs(mixed[0]) < 0.001);
        AssertRamp(gains, 0.8 / (FocusMix.FadeInSeconds * Rate), up: true); // only ever up, never faster than the fade
        Assert.InRange(gains[^1].Gain, 0.799, 0.801);
        Assert.True(mix.Audible);
        Assert.Equal(FocusSoundKind.Pink, mix.Playing);
    }

    /// <summary>The gain moves one way only, at most <paramref name="perSample"/> a sample (read every 64 samples or more, above float noise).</summary>
    static void AssertRamp(List<(int At, double Gain)> gains, double perSample, bool up)
    {
        var sparse = new List<(int At, double Gain)> { gains[0] };
        foreach (var g in gains)
            if (g.At - sparse[^1].At >= 64) sparse.Add(g);
        for (int i = 1; i < sparse.Count; i++)
        {
            double step = (sparse[i].Gain - sparse[i - 1].Gain) / (sparse[i].At - sparse[i - 1].At);
            if (!up) step = -step;
            Assert.InRange(step, -1e-6, perSample * 1.02);
        }
    }

    [Fact]
    public void FadesOutForAMeeting_StopsRunning_AndComesBack()
    {
        var mix = new FocusMix(9);
        mix.Set(FocusSoundKind.Brown, 1, ducked: false);
        int before = 3 * Rate, fade = (int)(FocusMix.DuckSeconds * Rate) + 512;
        Run(mix, before);
        mix.Set(FocusSoundKind.Brown, 1, ducked: true);
        mix.Set(FocusSoundKind.Brown, 1, ducked: true); // asked again every second: nothing changes
        var fading = Run(mix, fade);
        // the same brown noise carries on underneath, its gain falling smoothly from 1 to 0 over the duck time
        var raw = Raw(FocusSoundKind.Brown, 9, 0, before + fade)[before..];
        var gains = Gains(fading, raw);
        Assert.InRange(gains[0].Gain, 0.99, 1.0);
        AssertRamp(gains, 1 / (FocusMix.DuckSeconds * Rate), up: false);
        Assert.Equal(0, fading[^1]);
        Assert.False(mix.Active); // faded out and dropped: the mixer can sleep
        Assert.All(Run(mix, 512), x => Assert.Equal(0, x));

        mix.Set(FocusSoundKind.Brown, 1, ducked: false); // the meeting is over
        Assert.True(mix.Active);
        var back = Run(mix, 3 * Rate);
        Assert.True(Math.Abs(back[0]) < 0.001);
        Assert.True(Rms(back.AsSpan(back.Length - Rate / 2)) > 0.05);
    }

    [Fact]
    public void ANewSoundCrossfadesWithTheOld()
    {
        var mix = new FocusMix(3);
        mix.Set(FocusSoundKind.Rain, 0.6, ducked: false);
        Run(mix, 3 * Rate);
        mix.Set(FocusSoundKind.Cafe, 0.6, ducked: false);
        Assert.Equal(FocusSoundKind.Cafe, mix.Playing);
        var cross = Run(mix, 3 * Rate);
        var tenths = Enumerable.Range(0, 25).Select(i => Rms(cross.AsSpan(i * Rate / 10, Rate / 10))).ToList();
        Assert.True(tenths.Min() > tenths.Max() * 0.3, "a hole in the middle of the crossfade");
        mix.Set(null, 0.6, ducked: false);
        Run(mix, (int)(FocusMix.FadeOutSeconds * Rate) + 512);
        Assert.False(mix.Active);
    }

    [Fact]
    public void TheSoftLimitIsSmoothAndBounded()
    {
        Assert.Equal(0.5f, FocusGenerator.SoftLimit(0.5f));
        Assert.Equal(-0.7f, FocusGenerator.SoftLimit(-0.7f));
        float last = 0;
        for (float x = 0; x < 20; x += 0.001f)
        {
            float y = FocusGenerator.SoftLimit(x);
            Assert.True(y >= last && y <= 0.98f);
            Assert.True(y - last <= 0.0011f); // the slope never exceeds 1: no kink to hear
            last = y;
        }
    }
}
