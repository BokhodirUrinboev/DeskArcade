using System;

namespace DeskArcade;

/// <summary>
/// The desktop pets' voices. Each one is made the way an animal makes a sound: a buzzing source (the vocal
/// folds) whose pitch never sits perfectly still, shaped by three resonances of the throat and mouth (the
/// formants). Moving the formants while the sound plays is what turns a tone into a "mi-a-ow" or a "wow".
/// Every animal has several calls, so the pet can greet, complain, beg for attention or chatter at the cursor.
/// Whistles and hisses are the exceptions: a whistle is one pure sliding tone and a hiss is breath with no voice in it.
/// </summary>
public sealed partial class Sound
{
    void SynthesizeAnimals()
    {
        // cat
        _clips["meow"] = Meow(0.72, 480, 700, 430);
        _clips["meow2"] = Meow(0.5, 560, 790, 520);
        _clips["mew"] = Meow(0.26, 700, 860, 660);
        _clips["mrrp"] = Vocal(0.34, t => Arc(t / 0.34, 360, 480, 570),
            t => Swell(t, 0.34, 0.02) * (t < 0.2 ? 0.55 + 0.45 * Math.Sin(2 * Math.PI * 30 * t) : 1), // the rolled "rr"
            t => (Arc(t / 0.34, 500, 600, 760), Arc(t / 0.34, 1300, 1450, 1750), 2800),
            breath: 0.1, rasp: 0.1, bright: 0.4);
        _clips["chirp"] = Seq(0.045, Ek(870), Ek(910), Ek(850), Ek(920), Ek(840)); // chattering at a bird it cannot reach
        _clips["hiss"] = Normalize(Vocal(0.75, _ => 100, t => Math.Min(1, t / 0.015) * Math.Pow(Math.Max(0, 1 - t / 0.75), 0.8),
            _ => (2600, 4300, 6300), breath: 1, voice: 0, width: 3.5), 0.5);
        _clips["purr"] = Purr(1.6);

        // dog
        _clips["bark"] = Seq(0.1, Bark(0.15, 390, 270), Bark(0.13, 410, 290));
        _clips["bark1"] = Bark(0.17, 430, 290);
        _clips["woof"] = Bark(0.26, 210, 150, size: 1.35, rasp: 0.35);
        _clips["whine"] = Amp(Vocal(1.0, t => Arc(t, 800, 1150, 720), t => Swell(t, 1.0, 0.1), _ => (1000, 2500, 3600),
            breath: 0.06, jitter: 0.02, vibrato: 6.5, depth: 0.03, bright: 0.35), 0.7);
        _clips["pant"] = Normalize(Seq(0.035,
            Pant(true), Pant(false), Pant(true), Pant(false), Pant(true), Pant(false), Pant(true), Pant(false), Pant(true), Pant(false)), 0.45);
        _clips["growl"] = Vocal(0.9, t => 95 + 12 * Math.Sin(2 * Math.PI * 3 * t), t => Swell(t, 0.9, 0.12), _ => (420, 950, 2300),
            breath: 0.25, rasp: 0.55, jitter: 0.05, bright: 0.75);

        // duck: a mallard's call gets quieter and shorter with every quack
        _clips["quack"] = Quack(0.21, 440);
        _clips["quacks"] = Seq(0.075, Quack(0.22, 480), Amp(Quack(0.19, 450), 0.85), Amp(Quack(0.17, 420), 0.7),
            Amp(Quack(0.15, 395), 0.55), Amp(Quack(0.13, 370), 0.42));
        _clips["chatter"] = Amp(Seq(0.07, Quack(0.07, 380), Amp(Quack(0.08, 360), 0.85), Amp(Quack(0.06, 390), 0.9)), 0.55);

        // bunny: mostly quiet; squeaks when startled, little grunting honks when happy, thumps a back foot in alarm
        _clips["squeak"] = Vocal(0.13, t => Arc(t / 0.13, 1500, 2300, 1900), t => Swell(t, 0.13, 0.01), _ => (2300, 3700, 5200),
            breath: 0.12, jitter: 0.02, bright: 0.2);
        _clips["squeak2"] = Vocal(0.22, t => 1300 + 900 * t / 0.22, t => Swell(t, 0.22, 0.015), _ => (2100, 3500, 5000),
            breath: 0.12, jitter: 0.02, bright: 0.2);
        _clips["grunt"] = Amp(Seq(0.075, Grunt(), Grunt(), Grunt()), 0.6);
        _clips["thump"] = Normalize(Render(0.2, t =>
        {
            double f = 55 + 70 * Math.Exp(-t * 40);
            return Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 22) + Noise() * Math.Exp(-t * 150) * 0.3;
        }), 0.7);

        // penguin: an African penguin brays like a donkey, gasping in between
        _clips["honk"] = Bray(0.45, 330);
        _clips["bray"] = Seq(0.05, Amp(Bray(0.13, 430), 0.45), Bray(0.3, 340), Amp(Bray(0.12, 440), 0.45), Bray(0.32, 345),
            Amp(Bray(0.12, 450), 0.45), Bray(0.95, 330));
        _clips["peep"] = Vocal(0.15, t => 700 + 250 * t / 0.15, t => Swell(t, 0.15, 0.01), _ => (950, 1900, 3100),
            breath: 0.1, jitter: 0.02, bright: 0.5);

        // fox: a "wow-wow-wow" contact bark, a stuttering gekker, and the vixen's scream
        _clips["yip"] = Seq(0.17, FoxBark(0.13, 760), Amp(FoxBark(0.13, 790), 0.9), Amp(FoxBark(0.12, 740), 0.8));
        _clips["yip1"] = FoxBark(0.15, 820);
        var gekker = new float[10][];
        for (int i = 0; i < gekker.Length; i++) gekker[i] = Amp(Gek(420 + (i * 37 % 90)), 0.7 + 0.3 * ((i * 5 % 3) / 2.0));
        _clips["gekker"] = Seq(0.035, gekker);
        _clips["scream"] = Vocal(0.85, t => Arc(t / 0.85, 780, 1250, 700), t => Swell(t, 0.85, 0.05),
            t => (Arc(t / 0.85, 800, 1150, 700), Arc(t / 0.85, 2000, 1850, 1300), 3300),
            breath: 0.22, rasp: 0.45, jitter: 0.03, vibrato: 7, depth: 0.03, bright: 0.75);

        // hamster: thin high squeaks and a fast, teeth-chattering chitter
        _clips["hsqueak"] = Hsq(0.09, 3000);
        _clips["hsqueak2"] = Seq(0.03, Hsq(0.06, 3100), Amp(Hsq(0.05, 3500), 0.85));
        var chitter = new float[8][];
        for (int i = 0; i < chitter.Length; i++) chitter[i] = Amp(Chit(2400 + (i * 53 % 400)), 0.75 + 0.25 * ((i * 7 % 3) / 2.0));
        _clips["chitter"] = Seq(0.02, chitter);

        // turtle: a slow, breathy hiss with no voice in it, and a tiny grunt from deep in the shell
        _clips["thiss"] = Normalize(Vocal(0.9, _ => 90, t => Math.Min(1, t / 0.25) * Math.Pow(Math.Max(0, 1 - t / 0.9), 1.2),
            _ => (1800, 3200, 5200), breath: 1, voice: 0, width: 3), 0.4);
        _clips["tgrunt"] = Amp(Vocal(0.16, t => 150 - 30 * t / 0.16, t => Swell(t, 0.16, 0.02), _ => (450, 1000, 2200),
            breath: 0.4, rasp: 0.4, jitter: 0.04, bright: 0.5), 0.6);

        // parrot: a harsh squawk, a sliding whistle and a nasal two-note "hel-lo"
        _clips["squawk"] = Vocal(0.28, t => Arc(t / 0.28, 900, 1400, 800), t => Math.Min(1, t / 0.01) * Math.Pow(Math.Max(0, 1 - t / 0.28), 0.5),
            _ => (1300, 2600, 4200), breath: 0.25, rasp: 0.7, jitter: 0.05, bright: 0.9, width: 1.3);
        _clips["whistle"] = Whistle(0.45, 1500, 2600, 1900);
        _clips["hello"] = Seq(0.04,
            Vocal(0.14, t => 520 + 80 * t / 0.14, t => Swell(t, 0.14, 0.01), _ => (600, 1900, 2600), breath: 0.1, rasp: 0.3, bright: 0.7, width: 0.9),
            Vocal(0.2, t => Arc(t / 0.2, 480, 620, 380), t => Swell(t, 0.2, 0.01), _ => (450, 900, 2500), breath: 0.1, rasp: 0.3, bright: 0.7, width: 0.9));

        // frog: a two-pulse "rib-bit" and a deep croak that rolls at about 24 pulses a second
        _clips["ribbit"] = Seq(0.03,
            Vocal(0.09, t => 380 - 80 * t / 0.09, t => Swell(t, 0.09, 0.008), _ => (500, 1200, 2400), breath: 0.15, rasp: 0.5, jitter: 0.03, bright: 0.8),
            Vocal(0.13, t => 300 + 120 * t / 0.13, t => Swell(t, 0.13, 0.008), _ => (550, 1300, 2500), breath: 0.15, rasp: 0.5, jitter: 0.03, bright: 0.8));
        _clips["croak"] = Vocal(0.55, t => 110 + 15 * Math.Sin(2 * Math.PI * 8 * t),
            t => Swell(t, 0.55, 0.05) * (0.6 + 0.4 * Math.Abs(Math.Sin(2 * Math.PI * 24 * t))), _ => (380, 900, 2100),
            breath: 0.2, rasp: 0.6, jitter: 0.05, bright: 0.8);

        // owl: round, soft hoots ("hoo", "hoo-hoooo") and a rolling trill
        _clips["hoot"] = Hoot(0.4, 440);
        _clips["hoots"] = Seq(0.09, Amp(Hoot(0.22, 450), 0.8), Hoot(0.5, 430));
        _clips["trill"] = Vocal(0.5, t => 700 + 90 * Math.Sin(2 * Math.PI * 22 * t), t => Swell(t, 0.5, 0.04), _ => (900, 1800, 3000),
            breath: 0.1, bright: 0.3, width: 0.8);

        // dragon: a chest-deep rumble, a puff of breath (also its fire) and a short roar
        _clips["rumble"] = Normalize(Vocal(1.0, t => 55 + 10 * Math.Sin(2 * Math.PI * 2 * t), t => Swell(t, 1.0, 0.15), _ => (250, 700, 1800),
            breath: 0.2, rasp: 0.6, jitter: 0.06, bright: 0.8), 0.5);
        _clips["huff"] = Normalize(Filtered(0.3, 0.12, 0.35, t => Math.Min(1, t / 0.02) * Math.Exp(-t * 9)), 0.5);
        _clips["roar"] = Amp(Vocal(0.7, t => Arc(t / 0.7, 140, 220, 120), t => Math.Min(1, t / 0.04) * Math.Pow(Math.Max(0, 1 - t / 0.7), 0.8),
            t => (Arc(t / 0.7, 500, 800, 450), Arc(t / 0.7, 1200, 1500, 1000), 2600),
            breath: 0.3, rasp: 0.5, jitter: 0.05, vibrato: 9, depth: 0.03, bright: 0.85), 0.7);

        // shared by every animal, played at a pitch that suits its size
        _clips["yawn"] = Amp(Vocal(1.0, t => Arc(t, 540, 430, 230), t => Math.Min(1, t / 0.2) * Math.Pow(Math.Max(0, 1 - t), 0.7),
            t => (Arc(t, 450, 1000, 420), Arc(t, 1400, 1450, 800), 2600), breath: 0.45, voice: 0.6, jitter: 0.03, bright: 0.4), 0.7);
        _clips["snore"] = Normalize(Vocal(1.5, _ => 85, t => Math.Pow(Math.Sin(Math.PI * t / 1.5), 1.5), _ => (480, 1050, 2300),
            breath: 0.9, voice: 0.35, rasp: 0.5, jitter: 0.05, bright: 0.6), 0.4);
        _clips["sniff"] = Seq(0.06, Amp(Sniff(), 0.5), Amp(Sniff(), 0.4), Amp(Sniff(), 0.5), Amp(Sniff(), 0.35));
        _clips["lick"] = Seq(0.08, Lick(), Lick(), Lick());
        _clips["flap"] = Seq(0.04, Flap(), Flap(), Flap(), Flap());
    }

    float[] Meow(double len, double lo, double hi, double end) => Vocal(len,
        t => Arc(t / len, lo, hi, end),
        t => Swell(t, len, 0.05),
        t => (Arc(t / len, 650, 1150, 700), Arc(t / len, 2300, 1750, 1100), Arc(t / len, 3600, 3100, 2900)), // "mi - a - ow"
        breath: 0.07, rasp: 0.04, jitter: 0.015, vibrato: 5.5, depth: 0.012, bright: 0.45);

    float[] Ek(double f) => Vocal(0.045, t => f * (1 - 4 * t), t => Swell(t, 0.045, 0.004), _ => (1100, 2100, 3300),
        breath: 0.15, rasp: 0.15, bright: 0.6);

    float[] Bark(double len, double from, double to, double size = 1, double rasp = 0.25) => Vocal(len,
        t => from + (to - from) * Math.Sqrt(t / len),
        t => Math.Min(1, t / 0.006) * Math.Pow(Math.Max(0, 1 - t / len), 1.3),
        t => (Arc(t / len, 550, 950, 650) / size, Arc(t / len, 1250, 1650, 1300) / size, 2700 / size),
        breath: 0.28, rasp: rasp, jitter: 0.03, bright: 0.7);

    /// <summary>One breath of a dog's pant: out (louder, open) or in (softer).</summary>
    float[] Pant(bool exhale) => Amp(Vocal(exhale ? 0.085 : 0.07, _ => 150, t => Swell(t, exhale ? 0.085 : 0.07, 0.015),
        _ => exhale ? (850, 1500, 2600) : (650, 1250, 2400), breath: 1, voice: 0.15, width: 1.8), exhale ? 1 : 0.55);

    float[] Quack(double len, double f) => Vocal(len,
        t => f * (1.06 - 0.3 * t / len),
        t => Math.Min(1, t / 0.012) * Math.Pow(Math.Max(0, 1 - t / len), 0.7),
        t => (Arc(t / len, 750, 1050, 850), Arc(t / len, 1500, 1750, 1450), 2800),
        breath: 0.1, rasp: 0.6, jitter: 0.04, bright: 0.85, width: 0.8);

    float[] Grunt() => Vocal(0.055, _ => 210, t => Swell(t, 0.055, 0.006), _ => (520, 1150, 2400), breath: 0.35, rasp: 0.35, bright: 0.6);

    float[] Bray(double len, double f) => Vocal(len,
        t => f * (1 + 0.14 * Math.Sin(Math.PI * t / len)),
        t => Swell(t, len, 0.03),
        t => (Arc(t / len, 600, 850, 650), Arc(t / len, 1150, 1400, 1100), 2500),
        breath: 0.15, rasp: 0.35, jitter: 0.03, vibrato: 8, depth: 0.02, bright: 0.8);

    float[] FoxBark(double len, double f) => Vocal(len,
        t => Arc(t / len, f * 0.9, f * 1.12, f * 0.72),
        t => Math.Min(1, t / 0.01) * Math.Pow(Math.Max(0, 1 - t / len), 0.9),
        t => (Arc(t / len, 700, 1050, 600), Arc(t / len, 1600, 1750, 1050), 3000), // "wow"
        breath: 0.2, rasp: 0.3, jitter: 0.03, bright: 0.7);

    float[] Gek(double f) => Vocal(0.045, t => f * (1 - 3 * t), t => Swell(t, 0.045, 0.004), _ => (800, 1500, 2700),
        breath: 0.3, rasp: 0.6, jitter: 0.04, bright: 0.7);

    float[] Hsq(double len, double f) => Vocal(len, t => Arc(t / len, f * 0.85, f * 1.1, f * 0.9), t => Swell(t, len, 0.004), _ => (3000, 4800, 6500),
        breath: 0.1, jitter: 0.03, bright: 0.3);

    float[] Chit(double f) => Vocal(0.03, t => f * (1 - 2 * t), t => Swell(t, 0.03, 0.003), _ => (2600, 4200, 6000),
        breath: 0.2, rasp: 0.2, bright: 0.4);

    float[] Hoot(double len, double f) => Vocal(len, t => Arc(t / len, f * 0.95, f * 1.06, f * 0.86), t => Swell(t, len, 0.06),
        _ => (480, 1000, 2300), breath: 0.15, voice: 0.8, bright: 0.15, width: 0.7);

    /// <summary>A whistle: one pure tone whose pitch slides from a through b to c, with a hint of breath.</summary>
    float[] Whistle(double len, double a, double b, double c)
    {
        var buf = Buf(len);
        double phase = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            phase += Arc(t / len, a, b, c) / Rate;
            double env = Math.Min(1, t / 0.02) * Math.Pow(Math.Max(0, 1 - t / len), 0.5);
            buf[i] = (float)((Math.Sin(2 * Math.PI * phase) + Math.Sin(4 * Math.PI * phase) * 0.12 + Noise() * 0.03) * env);
        }
        return Normalize(buf, 0.5);
    }

    float[] Sniff() => Vocal(0.06, _ => 200, t => Swell(t, 0.06, 0.01), _ => (2400, 4200, 6500), breath: 1, voice: 0, width: 2.5);

    float[] Lick() => Normalize(Render(0.03, t => Noise() * Math.Exp(-t * 160) + Math.Sin(2 * Math.PI * 1500 * t) * Math.Exp(-t * 110) * 0.6), 0.35);

    float[] Flap() => Normalize(Filtered(0.07, 0.35, 0.45, t => Math.Sin(Math.PI * t / 0.07)), 0.5);

    /// <summary>A purr: about 25 muscle twitches a second, rumbling on the breath out and, softer and slower, on the breath in.</summary>
    float[] Purr(double seconds)
    {
        var b = Buf(seconds);
        double low = 0, low2 = 0, cut = seconds * 0.52, phase = 0;
        for (int i = 0; i < b.Length; i++)
        {
            double t = (double)i / Rate;
            bool outBreath = t < cut;
            double k = outBreath ? t / cut : (t - cut) / (seconds - cut);
            double rate = outBreath ? 26 : 22;
            phase = (phase + rate / Rate) % 1;
            double pulse = Math.Exp(-phase * 7);
            double src = Noise() * 0.8 + Math.Sin(2 * Math.PI * 2 * rate * t) * 0.3;
            low += (src * pulse - low) * 0.06;
            low2 += (low - low2) * 0.12;
            b[i] = (float)(low2 * Math.Sqrt(Math.Sin(Math.PI * k)) * (outBreath ? 1 : 0.7));
        }
        return Normalize(b, 0.5);
    }

    /// <summary>
    /// A source-filter voice. <paramref name="f0"/> is the pitch in Hz, <paramref name="env"/> the loudness and
    /// <paramref name="formants"/> the three resonances (Hz), all as functions of time in seconds. <paramref name="rasp"/>
    /// weakens every other vocal-fold cycle (a quack's or a bray's roughness), <paramref name="breath"/> mixes in
    /// air noise that goes through the same resonances, <paramref name="voice"/> 0 leaves only the breath (a hiss or a sniff),
    /// <paramref name="bright"/> sets how buzzy the source is, and <paramref name="width"/> widens the resonances.
    /// </summary>
    float[] Vocal(double seconds, Func<double, double> f0, Func<double, double> env, Func<double, (double, double, double)> formants,
        double breath = 0.06, double rasp = 0, double jitter = 0.01, double vibrato = 0, double depth = 0,
        double voice = 1, double bright = 0.5, double width = 1)
    {
        var b = Buf(seconds);
        var res = new Resonator[3];
        double phase = 0, wobble = 0, tilt = 0, amp = 1;
        int cycle = 0;
        for (int i = 0; i < b.Length; i++)
        {
            double t = (double)i / Rate;
            if ((i & 31) == 0)
            {
                var (f1, f2, f3) = formants(t);
                res[0].Tune(f1, width);
                res[1].Tune(f2, width);
                res[2].Tune(f3, width);
            }
            wobble += (Noise() - wobble) * 0.002; // no animal holds a pitch perfectly still
            double f = f0(t) * (1 + depth * Math.Sin(2 * Math.PI * vibrato * t) + jitter * wobble * 30);
            double dt = Math.Clamp(f / Rate, 1e-4, 0.45);
            phase += dt;
            if (phase >= 1)
            {
                phase -= 1;
                cycle++;
                amp = (1 + Noise() * 0.12) * ((cycle & 1) == 1 ? 1 - rasp : 1);
            }
            double saw = 2 * phase - 1 - PolyBlep(phase, dt);
            tilt += (saw - tilt) * bright;
            double src = tilt * voice * amp + Noise() * breath;
            double y = res[0].Run(src) + res[1].Run(src) * 0.7 + res[2].Run(src) * 0.35;
            b[i] = (float)(y * env(t));
        }
        return Normalize(b);
    }

    /// <summary>A two-pole band-pass (0 dB at its peak) standing in for one resonance of the vocal tract.</summary>
    struct Resonator
    {
        double _b0, _a1, _a2, _x1, _x2, _y1, _y2;

        public void Tune(double f, double width)
        {
            f = Math.Clamp(f, 60, Rate * 0.45);
            double bw = Math.Max(70, f * 0.11) * width;
            double w = 2 * Math.PI * f / Rate, alpha = Math.Sin(w) * bw / (2 * f), a0 = 1 + alpha;
            _b0 = alpha / a0;
            _a1 = -2 * Math.Cos(w) / a0;
            _a2 = (1 - alpha) / a0;
        }

        public double Run(double x)
        {
            double y = _b0 * (x - _x2) - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;
            return y;
        }
    }

    /// <summary>Smooths the sawtooth's jump so high voices do not alias into a whistle.</summary>
    static double PolyBlep(double t, double dt)
    {
        if (t < dt)
        {
            t /= dt;
            return t + t - t * t - 1;
        }
        if (t > 1 - dt)
        {
            t = (t - 1) / dt;
            return t * t + t + t + 1;
        }
        return 0;
    }

    /// <summary>Goes from <paramref name="a"/> through <paramref name="b"/> (halfway) to <paramref name="c"/> as k runs 0 to 1.</summary>
    static double Arc(double k, double a, double b, double c)
    {
        k = Math.Clamp(k, 0, 1);
        static double S(double x) => x * x * (3 - 2 * x);
        return k < 0.5 ? a + (b - a) * S(k * 2) : b + (c - b) * S(k * 2 - 1);
    }

    /// <summary>An envelope that fades in over <paramref name="attack"/> seconds and out toward the end.</summary>
    static double Swell(double t, double length, double attack) => Math.Min(1, t / attack) * Math.Pow(Math.Max(0, 1 - t / length), 0.6);

    static float[] Normalize(float[] b, double peak = 0.6)
    {
        float max = 0;
        foreach (var x in b) max = Math.Max(max, Math.Abs(x));
        return max < 1e-6 ? b : Amp(b, peak / max);
    }

    static float[] Amp(float[] b, double k)
    {
        for (int i = 0; i < b.Length; i++) b[i] = (float)(b[i] * k);
        return b;
    }

    /// <summary>Sounds one after another with <paramref name="gap"/> seconds of silence between them.</summary>
    float[] Seq(double gap, params float[][] parts)
    {
        int g = (int)(gap * Rate), n = 0;
        foreach (var p in parts) n += p.Length;
        var b = new float[n + g * (parts.Length - 1)];
        int at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(b, at);
            at += p.Length + g;
        }
        return b;
    }
}
