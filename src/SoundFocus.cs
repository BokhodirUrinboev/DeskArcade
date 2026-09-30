using System;
using System.Collections.Generic;

namespace DeskArcade;

/// <summary>The sounds to focus by (At work → Focus sounds).</summary>
public enum FocusSoundKind { Brown, Pink, Rain, Cafe }

/// <summary>
/// The focus sound, mixed under the games' clips: brown or pink noise, rain or a café, generated as it plays, with its
/// own volume (neither the games' volume nor their Sound switch touch it), faded rather than cut.
/// </summary>
public sealed partial class Sound
{
    readonly FocusMix _focus = new((uint)Environment.TickCount);

    /// <summary>
    /// What focus sound should play: a kind (null for none), its volume from 0 to 1, and whether it is faded down for
    /// now (a meeting, a presentation). Safe to call every second with the same values.
    /// </summary>
    public void SetFocusSound(FocusSoundKind? kind, double volume, bool ducked)
    {
        if (!_running) return;
        lock (_lock) _focus.Set(kind, volume, ducked);
        _wake.Set();
    }

    /// <summary>A focus sound can be heard right now (for the time counted towards the achievement).</summary>
    public bool FocusSoundAudible
    {
        get
        {
            lock (_lock) return _focus.Audible;
        }
    }

    /// <summary>An audio device is open and playing: false without one (a missing library, a vanished device).</summary>
    public bool HasDevice => _running;
}

/// <summary>
/// A focus sound, synthesized as it plays: it never loops, so there is no join to hear, and each call carries on from
/// where the last one stopped. Output is mono at <see cref="Rate"/>, about -15 to -18 dBFS RMS, and never reaches ±1.
/// </summary>
public abstract class FocusGenerator
{
    public const int Rate = 44100;

    /// <summary>Makes a generator; the seed makes a run repeatable (for tests).</summary>
    public static FocusGenerator Create(FocusSoundKind kind, uint seed) => kind switch
    {
        FocusSoundKind.Pink => new PinkNoise(seed),
        FocusSoundKind.Rain => new RainSound(seed),
        FocusSoundKind.Cafe => new CafeSound(seed),
        _ => new BrownNoise(seed),
    };

    /// <summary>Writes the next samples (overwriting what is in <paramref name="into"/>).</summary>
    public abstract void Fill(Span<float> into);

    /// <summary>
    /// Leaves quiet samples alone and bends loud ones smoothly towards ±1 without ever reaching it: the rare peak of a
    /// noise does not clip, and below 0.7 nothing changes.
    /// </summary>
    public static float SoftLimit(float x)
    {
        float a = MathF.Abs(x);
        if (a <= 0.7f) return x;
        return MathF.CopySign(0.7f + 0.28f * MathF.Tanh((a - 0.7f) / 0.28f), x);
    }

    /// <summary>A fast xorshift generator: uniform noise without the cost of <see cref="Random"/> in the audio thread.</summary>
    protected struct Rng
    {
        uint _s;

        public Rng(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed;

        public uint NextUInt()
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return _s;
        }

        /// <summary>Uniform in [-1, 1).</summary>
        public float Next() => (int)NextUInt() * (1f / 2147483648f);

        /// <summary>Uniform in [0, 1).</summary>
        public float Unit() => (NextUInt() >> 8) * (1f / 16777216f);

        public float Range(float lo, float hi) => lo + (hi - lo) * Unit();
    }

    /// <summary>One-pole low-pass: the coefficient for a corner frequency.</summary>
    protected static float Pole(double hz) => (float)(1 - Math.Exp(-2 * Math.PI * hz / Rate));

    /// <summary>
    /// A state-variable band-pass (topology-preserving, so its centre can glide while it plays without clicks), with
    /// its coefficients worked out again only when the centre moves.
    /// </summary>
    protected struct BandPass
    {
        float _ic1, _ic2, _g, _k, _a1, _a2, _a3, _hz;

        public void Tune(float hz, float q)
        {
            if (MathF.Abs(hz - _hz) < 0.5f && _k != 0) return;
            _hz = hz;
            _g = MathF.Tan(MathF.PI * Math.Clamp(hz, 20f, Rate * 0.45f) / Rate);
            _k = 1f / q;
            _a1 = 1f / (1f + _g * (_g + _k));
            _a2 = _g * _a1;
            _a3 = _g * _a2;
        }

        public float Next(float x)
        {
            float v3 = x - _ic2;
            float v1 = _a1 * _ic1 + _a2 * v3;
            float v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
            _ic1 = 2 * v1 - _ic1;
            _ic2 = 2 * v2 - _ic2;
            return v1 * _k; // unity gain at the centre
        }
    }
}

/// <summary>
/// Brown (red) noise: white noise summed up with a slow leak, so its power falls 6 dB an octave, a deep, even rumble
/// like a distant waterfall; the lowest few hertz, which only move a speaker's cone, are filtered out.
/// </summary>
public sealed class BrownNoise : FocusGenerator
{
    const float Leak = 0.9985f, Push = 0.03f, Gain = 0.7f;
    Rng _rng;
    float _sum, _prev, _high;
    readonly float _hp = 1f - Pole(18);

    public BrownNoise(uint seed) => _rng = new Rng(seed);

    public override void Fill(Span<float> into)
    {
        for (int i = 0; i < into.Length; i++)
        {
            _sum = _sum * Leak + _rng.Next() * Push;
            _high = _hp * (_high + _sum - _prev); // a gentle high-pass under 18 Hz
            _prev = _sum;
            into[i] = SoftLimit(_high * Gain);
        }
    }
}

/// <summary>
/// Pink noise: equal power in every octave (3 dB an octave down from white), the soft, balanced hiss of steady rain on
/// a roof. Paul Kellet's refined filter of white noise, flat within half a decibel over the audible range.
/// </summary>
public sealed class PinkNoise : FocusGenerator
{
    const float Gain = 0.08f;
    Rng _rng;
    float _b0, _b1, _b2, _b3, _b4, _b5, _b6;

    public PinkNoise(uint seed) => _rng = new Rng(seed);

    public override void Fill(Span<float> into)
    {
        for (int i = 0; i < into.Length; i++) into[i] = SoftLimit(Next() * Gain);
    }

    /// <summary>One sample before the gain (the rain uses it as its bed).</summary>
    public float Next()
    {
        float white = _rng.Next();
        _b0 = 0.99886f * _b0 + white * 0.0555179f;
        _b1 = 0.99332f * _b1 + white * 0.0750759f;
        _b2 = 0.96900f * _b2 + white * 0.1538520f;
        _b3 = 0.86650f * _b3 + white * 0.3104856f;
        _b4 = 0.55000f * _b4 + white * 0.5329522f;
        _b5 = -0.7616f * _b5 - white * 0.0168980f;
        float pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362f;
        _b6 = white * 0.115926f;
        return pink;
    }
}

/// <summary>
/// Rain on a window: a hiss of countless far drops that swells and eases every few seconds, and near drops on top,
/// most of them a tiny tick, some a small "plink" as a bubble rings and rises in pitch.
/// </summary>
public sealed class RainSound : FocusGenerator
{
    const int MaxDrops = 32;
    const float Gain = 0.9f;

    struct Drop
    {
        public float Amp, Decay, Phase, Freq, Rise, Prev;
        public bool Plink;
    }

    Rng _rng;
    readonly PinkNoise _bed;
    readonly Drop[] _drops = new Drop[MaxDrops];
    int _count;
    float _low, _highIn, _high, _ticks, _level = 0.8f, _target = 0.8f;
    int _swellLeft;
    readonly float _lp = Pole(6500), _hp = 1f - Pole(380), _tickLp = Pole(7000);

    public RainSound(uint seed)
    {
        _rng = new Rng(seed);
        _bed = new PinkNoise(seed * 2654435761u + 1);
    }

    public override void Fill(Span<float> into)
    {
        for (int i = 0; i < into.Length; i++)
        {
            // the swell: a new level every 2 to 7 seconds, reached over a second or so
            if (--_swellLeft <= 0)
            {
                _swellLeft = (int)(_rng.Range(2, 7) * Rate);
                _target = _rng.Range(0.6f, 1.05f);
            }
            _level += (_target - _level) * 0.00002f;

            // the far rain: pink noise without its rumble and its sharpest top
            _low += (_bed.Next() - _low) * _lp;
            _high = _hp * (_high + _low - _highIn);
            _highIn = _low;
            float s = _high * 0.16f * _level;

            // near drops: about 55 a second at full swell
            if (_count < MaxDrops && _rng.Unit() < 55f / Rate * _level) Spawn();
            float ticks = 0;
            for (int d = _count - 1; d >= 0; d--)
            {
                ref var drop = ref _drops[d];
                float v;
                if (drop.Plink)
                {
                    drop.Phase += drop.Freq / Rate;
                    if (drop.Phase >= 1) drop.Phase -= 1;
                    if (drop.Freq < 9000) drop.Freq *= drop.Rise;
                    v = MathF.Sin(2 * MathF.PI * drop.Phase);
                }
                else
                {
                    float w = _rng.Next();
                    ticks += (w - drop.Prev) * 0.5f * drop.Amp; // a bright click: the difference of white noise
                    drop.Prev = w;
                    v = 0;
                }
                s += v * drop.Amp;
                drop.Amp *= drop.Decay;
                if (drop.Amp < 0.0005f) _drops[d] = _drops[--_count];
            }
            _ticks += (ticks - _ticks) * _tickLp; // the clicks a little rounded, less like static
            into[i] = SoftLimit((s + _ticks * 1.4f) * Gain);
        }
    }

    void Spawn()
    {
        float u = _rng.Unit();
        bool plink = _rng.Unit() < 0.3f;
        _drops[_count++] = new Drop
        {
            Plink = plink,
            Amp = (plink ? 0.05f : 0.09f) + (plink ? 0.1f : 0.22f) * u * u * u, // mostly soft, now and then a big one
            Decay = MathF.Exp(-1f / (Rate * (plink ? _rng.Range(0.006f, 0.02f) : _rng.Range(0.0008f, 0.003f)))),
            Freq = _rng.Range(1400, 4200),
            Rise = MathF.Pow(_rng.Range(1.3f, 1.8f), 1f / (Rate * 0.02f)), // the bubble's pitch climbs as it shrinks
            Phase = 0,
        };
    }
}

/// <summary>
/// A quiet café: the hum of the room, the murmur of half a dozen people talking a few tables away (voices shaped by
/// two formants each, in syllables and phrases, muffled by distance), and now and then a spoon against a cup or a cup
/// set down on a saucer.
/// </summary>
public sealed class CafeSound : FocusGenerator
{
    const int Talkers = 6, MaxPartials = 24;
    const float Gain = 1.4f;

    sealed class Talker
    {
        public float Pitch, Phase, Env, Volume, F1, F2, T1, T2, Intonation, IntonationRate;
        public int Left, Syllables;
        public bool Voiced;
        public BandPass B1, B2;
    }

    struct Partial
    {
        public float Amp, Decay, Phase, Step;
    }

    Rng _rng;
    readonly Talker[] _talkers = new Talker[Talkers];
    readonly Partial[] _partials = new Partial[MaxPartials];
    int _partialCount, _nextClink, _hitsLeft, _nextHit, _nextCup;
    float _room, _roomLow, _murmurLow, _thud, _thudPhase, _thudDecay;
    readonly float _roomLp = Pole(260), _murmurLp = Pole(1500);

    public CafeSound(uint seed)
    {
        _rng = new Rng(seed);
        for (int i = 0; i < Talkers; i++)
        {
            var t = new Talker { Pitch = _rng.Range(100, 230), F1 = 500, F2 = 1500, T1 = 500, T2 = 1500 };
            t.B1.Tune(t.F1, 4);
            t.B2.Tune(t.F2, 6);
            t.Left = (int)(_rng.Range(0, 2.5f) * Rate); // they do not all start talking at once
            _talkers[i] = t;
        }
        _nextClink = (int)(_rng.Range(2, 6) * Rate);
        _nextCup = (int)(_rng.Range(12, 30) * Rate);
    }

    public override void Fill(Span<float> into)
    {
        for (int i = 0; i < into.Length; i++)
        {
            // the room: a low, soft rumble
            _room = _room * 0.9985f + _rng.Next() * 0.03f;
            _roomLow += (_room - _roomLow) * _roomLp;
            float s = _roomLow * 0.2f;

            float murmur = 0;
            foreach (var t in _talkers) murmur += Speak(t);
            _murmurLow += (murmur - _murmurLow) * _murmurLp;
            s += _murmurLow * 0.55f;

            s += Clinks();
            into[i] = SoftLimit(s * Gain);
        }
    }

    /// <summary>One sample of one talker: a buzz and a breath through two gliding formants, in syllables and pauses.</summary>
    float Speak(Talker t)
    {
        if (--t.Left <= 0) NextSyllable(t);
        float want = t.Voiced ? 1 : 0;
        t.Env += (want - t.Env) * (want > t.Env ? 0.0018f : 0.0009f); // about 13 ms up and 25 ms down
        if (t.Env < 0.0005f && !t.Voiced) return 0;
        t.F1 += (t.T1 - t.F1) * 0.0015f;
        t.F2 += (t.T2 - t.F2) * 0.0015f;
        if ((t.Left & 15) == 0)
        {
            t.B1.Tune(t.F1, 4);
            t.B2.Tune(t.F2, 6);
        }
        t.Intonation += t.IntonationRate;
        float pitch = t.Pitch * (1 + 0.08f * MathF.Sin(t.Intonation)) * (1 + 0.004f * _rng.Next());
        t.Phase += pitch / Rate;
        if (t.Phase >= 1) t.Phase -= 1;
        float source = (2 * t.Phase - 1) * 0.7f + _rng.Next() * 0.25f;
        return (t.B1.Next(source) + t.B2.Next(source) * 0.5f) * t.Env * t.Volume;
    }

    void NextSyllable(Talker t)
    {
        if (t.Voiced)
        {
            // a short gap after a syllable, a long one after a phrase
            t.Voiced = false;
            t.Left = (int)((--t.Syllables > 0 ? _rng.Range(0.02f, 0.09f) : _rng.Range(0.4f, 2.6f)) * Rate);
            return;
        }
        if (t.Syllables <= 0)
        {
            t.Syllables = 4 + (int)(_rng.Unit() * 10);
            t.Volume = _rng.Range(0.25f, 0.7f); // near or far, turned towards us or away
            t.IntonationRate = 2 * MathF.PI * _rng.Range(0.3f, 0.9f) / Rate;
        }
        t.Voiced = true;
        t.Left = (int)(_rng.Range(0.09f, 0.26f) * Rate);
        t.T1 = _rng.Range(300, 800);
        t.T2 = _rng.Range(900, 2300);
    }

    /// <summary>Spoons on cups every few seconds, now and then a cup set down.</summary>
    float Clinks()
    {
        if (--_nextClink <= 0)
        {
            _hitsLeft = 1 + (int)(_rng.Unit() * _rng.Unit() * 4); // mostly one, sometimes a spoon stirring
            _nextHit = 0;
            _nextClink = (int)(_rng.Range(3, 11) * Rate);
        }
        if (_hitsLeft > 0 && --_nextHit <= 0)
        {
            _hitsLeft--;
            _nextHit = (int)(_rng.Range(0.11f, 0.24f) * Rate);
            Strike(_rng.Range(2300, 3600), _rng.Range(0.03f, 0.075f));
        }
        if (--_nextCup <= 0)
        {
            _nextCup = (int)(_rng.Range(14, 40) * Rate);
            _thud = _rng.Range(0.05f, 0.1f);
            _thudDecay = MathF.Exp(-1f / (Rate * 0.035f));
            Strike(_rng.Range(1700, 2300), _thud * 0.5f); // the saucer rings a little
        }

        float s = 0;
        for (int p = _partialCount - 1; p >= 0; p--)
        {
            ref var partial = ref _partials[p];
            partial.Phase += partial.Step;
            if (partial.Phase >= 1) partial.Phase -= 1;
            s += MathF.Sin(2 * MathF.PI * partial.Phase) * partial.Amp;
            partial.Amp *= partial.Decay;
            if (partial.Amp < 0.0002f) _partials[p] = _partials[--_partialCount];
        }
        if (_thud > 0.0002f)
        {
            _thudPhase += 140f / Rate;
            if (_thudPhase >= 1) _thudPhase -= 1;
            s += MathF.Sin(2 * MathF.PI * _thudPhase) * _thud;
            _thud *= _thudDecay;
        }
        return s;
    }

    /// <summary>A small struck bar of china: four inharmonic partials, the higher ones dying faster.</summary>
    void Strike(float hz, float amp)
    {
        ReadOnlySpan<float> ratios = stackalloc float[] { 1f, 2.76f, 5.4f, 8.93f };
        ReadOnlySpan<float> levels = stackalloc float[] { 1f, 0.55f, 0.3f, 0.15f };
        float ring = _rng.Range(0.07f, 0.2f);
        for (int k = 0; k < ratios.Length && _partialCount < MaxPartials; k++)
        {
            float f = hz * ratios[k];
            if (f > Rate * 0.45f) break;
            _partials[_partialCount++] = new Partial
            {
                Amp = amp * levels[k], Step = f / Rate, Phase = 0.25f, // starting at the top of the sine: a sharp strike
                Decay = MathF.Exp(-1f / (Rate * ring / (1 + k * 0.8f))),
            };
        }
    }
}

/// <summary>
/// The focus sound as the mixer plays it: the chosen generator at its own volume, faded in and out rather than
/// switched (a new sound crossfades with the old one, a meeting fades it down), and dropped once it has faded out, so a
/// silent focus sound costs nothing.
/// </summary>
public sealed class FocusMix
{
    public const double FadeInSeconds = 2.5, FadeOutSeconds = 2.5, DuckSeconds = 3, VolumeSeconds = 0.8;
    const int MaxFading = 3;

    sealed class Layer
    {
        public required FocusGenerator Generator;
        public required FocusSoundKind Kind;
        public float Gain, Target, Step;
    }

    Layer? _current;
    readonly List<Layer> _fading = new();
    float[] _scratch = new float[512];
    uint _seed;

    public FocusMix(uint seed = 1) => _seed = seed;

    /// <summary>Something is still sounding (or fading), so the mixer has to run.</summary>
    public bool Active => _current != null || _fading.Count > 0;

    /// <summary>The chosen sound can be heard: playing, not faded down for a meeting.</summary>
    public bool Audible => _current is { Gain: > 0.001f };

    /// <summary>The sound now playing, or null.</summary>
    public FocusSoundKind? Playing => _current?.Kind;

    /// <summary>
    /// What should play: a sound (null for none), its volume (0 to 1), and whether it is faded down (a meeting, a
    /// presentation). Calling it again with the same values changes nothing.
    /// </summary>
    public void Set(FocusSoundKind? kind, double volume, bool ducked)
    {
        float level = (float)Math.Clamp(volume, 0, 1);
        bool want = kind != null && !ducked && level > 0;
        if (_current != null && (!want || _current.Kind != kind))
        {
            FadeOut(_current, _current.Kind != kind || kind == null ? FadeOutSeconds : DuckSeconds);
            _current = null;
        }
        if (!want) return;
        if (_current == null)
        {
            _current = new Layer { Generator = FocusGenerator.Create(kind!.Value, _seed++ * 747796405u + 1), Kind = kind.Value };
            Ramp(_current, level, FadeInSeconds);
        }
        else if (_current.Target != level) Ramp(_current, level, VolumeSeconds);
    }

    static void Ramp(Layer layer, float target, double seconds)
    {
        layer.Target = target;
        layer.Step = (float)(Math.Max(Math.Abs(target - layer.Gain), 0.02f) / (seconds * FocusGenerator.Rate));
    }

    void FadeOut(Layer layer, double seconds)
    {
        Ramp(layer, 0, seconds);
        _fading.Add(layer);
        if (_fading.Count > MaxFading)
        {
            // switched over and over: the quietest fading sound gives way
            int quietest = 0;
            for (int i = 1; i < _fading.Count; i++)
                if (_fading[i].Gain < _fading[quietest].Gain) quietest = i;
            _fading.RemoveAt(quietest);
        }
    }

    /// <summary>Adds the next samples into <paramref name="into"/>, moving each gain a step per sample.</summary>
    public void MixInto(Span<float> into)
    {
        if (_scratch.Length < into.Length) _scratch = new float[into.Length];
        var scratch = _scratch.AsSpan(0, into.Length);
        if (_current != null) Mix(_current, into, scratch);
        for (int i = _fading.Count - 1; i >= 0; i--)
        {
            Mix(_fading[i], into, scratch);
            if (_fading[i].Gain <= 0) _fading.RemoveAt(i);
        }
    }

    static void Mix(Layer layer, Span<float> into, Span<float> scratch)
    {
        layer.Generator.Fill(scratch);
        float gain = layer.Gain, target = layer.Target, step = layer.Step;
        for (int s = 0; s < into.Length; s++)
        {
            if (gain < target) gain = MathF.Min(gain + step, target);
            else if (gain > target) gain = MathF.Max(gain - step, target);
            into[s] += scratch[s] * gain;
        }
        layer.Gain = gain;
    }
}
