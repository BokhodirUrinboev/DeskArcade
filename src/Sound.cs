using System;
using System.Collections.Generic;
using System.Threading;
using DeskArcade.Platform;

namespace DeskArcade;

/// <summary>
/// Tiny software mixer so short sounds can overlap with low latency. All clips are synthesized;
/// the platform only supplies a raw 16-bit mono output (waveOut on Windows, PulseAudio on Linux).
/// </summary>
public sealed partial class Sound : IDisposable
{
    const int Rate = 44100;
    const int BufSamples = 512;

    readonly Dictionary<string, float[]> _clips = new();
    readonly List<Voice> _voices = new();
    readonly object _lock = new();
    readonly Random _rng = new(7);
    readonly short[] _mix = new short[BufSamples];
    readonly IAudioOutput? _out;
    readonly Thread? _thread;
    volatile bool _running;

    public bool Enabled { get; set; } = true;
    public double Volume { get; set; } = 0.6;

    sealed class Voice { public float[] Clip = Array.Empty<float>(); public double Pos; public float Vol; public double Rate; }

    public Sound(IDesktopPlatform platform)
    {
        Synthesize();
        try { _out = platform.OpenAudio(Rate); }
        catch { _out = null; } // missing audio library: play silently
        if (_out == null) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "DeskArcade.Sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Play(string name, double vol = 1, double pitch = 1)
    {
        if (!Enabled || !_running || !_clips.TryGetValue(name, out var clip)) return;
        lock (_lock)
        {
            if (_voices.Count > 16) _voices.RemoveAt(0);
            _voices.Add(new Voice { Clip = clip, Vol = (float)Math.Clamp(vol, 0, 1.5), Rate = pitch });
        }
    }

    /// <summary>The synthesized samples of a clip (for tests), or null if there is no such clip.</summary>
    public float[]? Samples(string name) => _clips.TryGetValue(name, out var clip) ? clip : null;

    void Loop()
    {
        try
        {
            while (_running)
            {
                if (_out!.CanWrite)
                {
                    MixInto(_mix);
                    _out.Write(_mix);
                }
                else
                {
                    Thread.Sleep(3);
                }
            }
        }
        catch
        {
            _running = false; // device vanished: go quiet rather than crash
        }
    }

    void MixInto(short[] outBuf)
    {
        Span<float> acc = stackalloc float[BufSamples];
        lock (_lock)
        {
            float master = (float)Volume;
            for (int v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                var clip = voice.Clip;
                for (int s = 0; s < BufSamples; s++)
                {
                    int idx = (int)voice.Pos;
                    if (idx >= clip.Length - 1) { voice.Pos = clip.Length; break; }
                    double frac = voice.Pos - idx;
                    acc[s] += (float)(clip[idx] + (clip[idx + 1] - clip[idx]) * frac) * voice.Vol * master;
                    voice.Pos += voice.Rate;
                }
                if (voice.Pos >= clip.Length - 1) _voices.RemoveAt(v);
            }
        }
        for (int s = 0; s < BufSamples; s++)
        {
            float x = acc[s];
            x /= 1f + MathF.Abs(x) * 0.6f; // soft clip
            outBuf[s] = (short)(Math.Clamp(x, -1f, 1f) * 30000);
        }
    }

    // ---------------- synthesis ----------------

    float[] Buf(double seconds) => new float[(int)(Rate * seconds)];
    float Noise() => (float)(_rng.NextDouble() * 2 - 1);

    void Synthesize()
    {
        _clips["bounce"] = Render(0.12, t =>
        {
            double f = 55 + 110 * Math.Exp(-t * 40);
            return Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 32) * 0.9 + Noise() * Math.Exp(-t * 300) * 0.15;
        });
        _clips["rim"] = Render(0.35, t =>
            (Math.Sin(2 * Math.PI * 620 * t) * Math.Exp(-t * 14) +
             Math.Sin(2 * Math.PI * 1047 * t) * Math.Exp(-t * 18) * 0.7 +
             Math.Sin(2 * Math.PI * 1712 * t) * Math.Exp(-t * 26) * 0.5 +
             Math.Sin(2 * Math.PI * 2390 * t) * Math.Exp(-t * 34) * 0.35) * 0.35);
        _clips["board"] = Render(0.15, t =>
            Math.Sin(2 * Math.PI * 170 * t) * Math.Exp(-t * 30) * 0.6 + Noise() * Math.Exp(-t * 90) * 0.3);
        _clips["swish"] = Filtered(0.32, 0.18, 0.55, t => Math.Min(1, t / 0.03) * Math.Exp(-t * 9) * 0.8);

        _clips["score"] = Notes(new[] { 784.0, 1046.5 }, 0.09, 0.45, 0.4);
        _clips["fire"] = Notes(new[] { 523.3, 659.3, 784.0, 1046.5 }, 0.07, 0.4, 0.45);
        _clips["best"] = Notes(new[] { 523.3, 659.3, 784.0, 1046.5, 1318.5 }, 0.08, 0.6, 0.45);
        _clips["done"] = Notes(new[] { 523.3, 659.3, 784.0, 1046.5 }, 0.13, 0.9, 0.5);
        _clips["attention"] = Notes(new[] { 880.0, 659.3 }, 0.14, 0.4, 0.45);
        _clips["star"] = Render(0.35, t =>
            (Math.Sin(2 * Math.PI * 1318 * t) + Math.Sin(2 * Math.PI * 1976 * t) * 0.5) * Math.Exp(-t * 12) * 0.35);
        _clips["buzzer"] = Render(0.5, t =>
            (Math.Sign(Math.Sin(2 * Math.PI * 185 * t)) * 0.5 + Math.Sign(Math.Sin(2 * Math.PI * 188 * t)) * 0.5)
            * (t < 0.45 ? 1 : (0.5 - t) / 0.05) * 0.18);

        _clips["twang"] = Pluck(118, 0.45, 0.55);
        _clips["thunk"] = Render(0.14, t =>
            Math.Sin(2 * Math.PI * 240 * t) * Math.Exp(-t * 45) * 0.6 + Noise() * Math.Exp(-t * 120) * 0.45);
        _clips["pop"] = Filtered(0.08, 0.6, 0.9, t => Math.Exp(-t * 60) * 1.1);
        _clips["kick"] = Render(0.12, t =>
        {
            double f = 80 + 160 * Math.Exp(-t * 50);
            return Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 28) * 0.8 + Noise() * Math.Exp(-t * 200) * 0.25;
        });
        _clips["whoosh"] = Filtered(0.22, 0.25, 0.7, t => Math.Sin(Math.PI * t / 0.22) * 0.35);

        SynthesizeAnimals();
        SynthesizePaperToss();
        SynthesizeFishing();
        SynthesizePool();
    }

    float[] Render(double seconds, Func<double, double> fn)
    {
        var b = Buf(seconds);
        for (int i = 0; i < b.Length; i++) b[i] = (float)fn((double)i / Rate);
        return b;
    }

    float[] Filtered(double seconds, double hp, double lp, Func<double, double> env)
    {
        var b = Buf(seconds);
        double low = 0, prev = 0, high = 0;
        for (int i = 0; i < b.Length; i++)
        {
            double n = Noise();
            low += (n - low) * lp;           // one-pole lowpass
            high = hp * (high + low - prev);  // one-pole highpass
            prev = low;
            b[i] = (float)(high * env((double)i / Rate) * 2.2);
        }
        return b;
    }

    float[] Notes(double[] freqs, double step, double tail, double vol)
    {
        var b = Buf(step * freqs.Length + tail);
        for (int n = 0; n < freqs.Length; n++)
        {
            int start = (int)(n * step * Rate);
            for (int i = start; i < b.Length; i++)
            {
                double t = (double)(i - start) / Rate;
                double f = freqs[n];
                double s = Math.Sin(2 * Math.PI * f * t) + Math.Sin(4 * Math.PI * f * t) * 0.25 + Math.Sin(6 * Math.PI * f * t) * 0.08;
                b[i] += (float)(s * Math.Min(1, t / 0.005) * Math.Exp(-t * 6) * vol / freqs.Length * 2);
            }
        }
        return b;
    }

    float[] Pluck(double freq, double seconds, double vol)
    {
        var b = Buf(seconds);
        int period = (int)(Rate / freq);
        var ring = new double[period];
        for (int i = 0; i < period; i++) ring[i] = Noise();
        for (int i = 0; i < b.Length; i++)
        {
            int k = i % period;
            ring[k] = (ring[k] + ring[(k + 1) % period]) * 0.5 * 0.996;
            b[i] = (float)(ring[k] * vol * Math.Min(1, i / 40.0));
        }
        return b;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(300);
        _out?.Dispose();
    }
}
