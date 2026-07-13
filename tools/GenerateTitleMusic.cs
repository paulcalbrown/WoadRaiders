// Renders the title-screen music to a looping WAV. A .NET 10 file-based app:
//
//   dotnet run tools/GenerateTitleMusic.cs
//
// writes WoadRaiders.Client/assets/audio/title_theme.wav (or pass a path).
//
// The piece is Celtic/gothic chip-metal in A phrygian, 6/8: a growling
// detuned drone fifth and a low tolling bell under a pulse-wave air with
// cuts, rolls and a closing half-step trill, a second voice in diatonic
// thirds, and a palm-muted chug riff walking i-bVII-bII/bVI — power chord on
// the downbeat, muted eighths behind it, chromatic approach notes where the
// root moves and a tritone stab where it doesn't. A swept-square kick, noise
// snare and hats drive it, double-kick under the jig, a crash opening each
// section. Form: intro / air / air+thirds / jig / outro — the outro drone
// flows back into the intro so the file loops seamlessly (loop points are in
// the WAV's smpl chunk and re-asserted by TitleScreen at load).

using System;
using System.IO;

const int Rate = 44100;
const double EighthSec = 0.25;      // 6/8 at 80 BPM per dotted quarter: a doom trudge
const int BarsTotal = 32;           // 4 intro + 8 air + 8 air' + 8 jig + 4 outro
const double BarSec = 6 * EighthSec;
const double TotalSec = BarsTotal * BarSec;

var outPath = args.Length > 0 ? args[0] : Path.Combine("WoadRaiders.Client", "assets", "audio", "title_theme.wav");
var buf = new double[(int)(TotalSec * Rate)];
int noiseSeed = 1;

// ---------------------------------------------------------------- waveforms

double Env(double t, double dur, double decayTo)
{
    double attack = Math.Min(1, t / 0.006);
    double release = Math.Clamp((dur - t) / 0.02, 0, 1);
    double decay = 1.0 - (1.0 - decayTo) * Math.Min(1, t / Math.Max(dur, 0.001));
    return attack * release * decay;
}

void Pulse(double start, double dur, double freq, double duty, double vol, double decayTo = 0.8, bool vibrato = true)
{
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, (int)((start + dur) * Rate));
    double phase = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double t = (i - n0) / (double)Rate;
        double f = freq;
        if (vibrato && t > 0.15)
            f *= 1.0 + 0.004 * Math.Sin(Math.Tau * 5.5 * (t - 0.15)); // NES-style delayed vibrato
        phase += f / Rate;
        buf[i] += (phase % 1.0 < duty ? 1.0 : -1.0) * vol * Env(t, dur, decayTo);
    }
}

void Triangle(double start, double dur, double freq, double vol)
{
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, (int)((start + dur) * Rate));
    double phase = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double t = (i - n0) / (double)Rate;
        phase += freq / Rate;
        buf[i] += (4.0 * Math.Abs(phase % 1.0 - 0.5) - 1.0) * vol * Env(t, dur, 0.9);
    }
}

void Noise(double start, double holdHz, double vol, double tau)
{
    var rnd = new Random(noiseSeed++);
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, n0 + (int)(tau * 6 * Rate));
    int holdSamples = Math.Max(1, (int)(Rate / holdHz));
    double hold = 0;
    int countdown = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        if (countdown-- <= 0)
        {
            hold = rnd.NextDouble() * 2 - 1;
            countdown = holdSamples;
        }
        buf[i] += hold * vol * Math.Exp(-((i - n0) / (double)Rate) / tau);
    }
}

void Bell(double start, double freq, double vol)
{
    // A chip bell: fundamental + fifth + octave partials, long exponential ring.
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, n0 + (int)(2.4 * Rate));
    double p1 = 0, p2 = 0, p3 = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double t = (i - n0) / (double)Rate;
        p1 += freq / Rate;
        p2 += freq * 1.5 / Rate;
        p3 += freq * 2.0 / Rate;
        double s = (p1 % 1.0 < 0.25 ? 1.0 : -1.0)
                 + (p2 % 1.0 < 0.5 ? 0.5 : -0.5)
                 + (p3 % 1.0 < 0.5 ? 0.35 : -0.35);
        buf[i] += s * vol * Math.Exp(-t / 0.8) * Math.Min(1, t / 0.004);
    }
}

void Kick(double start, double vol)
{
    // The metal kick: a square whose pitch drops 175->45 Hz in a few
    // milliseconds — the drop is the punch.
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, n0 + (int)(0.10 * Rate));
    double phase = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double t = (i - n0) / (double)Rate;
        phase += (45 + 130 * Math.Exp(-t / 0.02)) / Rate;
        buf[i] += (phase % 1.0 < 0.5 ? 1.0 : -1.0) * vol * Math.Exp(-t / 0.045) * Math.Min(1, t / 0.002);
    }
}

void Snare(double start, double vol)
{
    Noise(start, 3500, vol, 0.055);
    Pulse(start, 0.035, 185, 0.5, vol * 0.5, decayTo: 0.2, vibrato: false); // the drum's body
}

double Hz(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);

// A phrygian, ascending: A Bb C D E F G. Ornaments and the harmony voice
// walk this ladder so every cut, roll and third stays in the mode.
int[] scale = [9, 10, 0, 2, 4, 5, 7];
int StepUp(int midi)
{
    for (int m = midi + 1; ; m++)
        if (Array.IndexOf(scale, m % 12) >= 0)
            return m;
}
int StepDown(int midi)
{
    for (int m = midi - 1; ; m--)
        if (Array.IndexOf(scale, m % 12) >= 0)
            return m;
}
int ThirdBelow(int midi) => StepDown(StepDown(midi));

// ------------------------------------------------------------------- melody

// Ornaments: ' ' none, 'c' cut (quick upper grace), 'r' roll (five-note turn),
// 't' trill (a half-step shiver against the flat second, fading as it goes).
// harmonyVol adds a second voice a diatonic third below, plain and unornamented.
void PlayAir((int midi, double eighths, char orn)[] notes, double startEighth, double vol, double duty, double echoVol, double harmonyVol = 0)
{
    double at = startEighth * EighthSec;
    foreach (var (midi, eighths, orn) in notes)
    {
        double dur = eighths * EighthSec - 0.015; // breath between notes
        if (midi > 0)
        {
            double t = at;
            double d = dur;
            if (orn == 't')
            {
                int steps = Math.Max(2, (int)(d / 0.10));
                for (int i = 0; i < steps; i++)
                    Pulse(t + i * 0.10, 0.112, Hz(i % 2 == 0 ? midi : StepUp(midi)), duty,
                        vol * (1.0 - 0.3 * i / steps), vibrato: false);
            }
            else
            {
                if (orn == 'c')
                {
                    Pulse(t, 0.03, Hz(StepUp(midi)), duty, vol, vibrato: false);
                    t += 0.03;
                    d -= 0.03;
                }
                else if (orn == 'r')
                {
                    Pulse(t, 0.08, Hz(midi), duty, vol, vibrato: false);
                    Pulse(t + 0.08, 0.06, Hz(StepUp(midi)), duty, vol, vibrato: false);
                    Pulse(t + 0.14, 0.06, Hz(midi), duty, vol, vibrato: false);
                    Pulse(t + 0.20, 0.06, Hz(StepDown(midi)), duty, vol, vibrato: false);
                    t += 0.26;
                    d -= 0.26;
                }
                Pulse(t, d, Hz(midi), duty, vol);
            }
            if (harmonyVol > 0)
                Pulse(at, dur, Hz(ThirdBelow(midi)), 0.125, harmonyVol, vibrato: false);
            if (echoVol > 0) // dotted-eighth cathedral echo
                Pulse(at + 1.5 * EighthSec, dur * 0.8, Hz(midi), 0.125, echoVol, vibrato: false);
        }
        at += eighths * EighthSec;
    }
}

// The air (A section), 8 bars of 6/8: the rising fifth answered by a falling
// phrygian line — every second degree is Bb, leaning down onto the tonic —
// twice, each ending in a roll. The F5 in bar five is the mode's dark colour.
(int, double, char)[] air =
[
    (69, 3, 'c'), (76, 3, 'c'),                         // A4 . . E5 . .
    (74, 2, ' '), (72, 1, ' '), (70, 3, 'c'),           // D5 . C5 Bb4 . .
    (72, 2, ' '), (70, 1, ' '), (69, 2, ' '), (67, 1, ' '),
    (69, 6, 'r'),                                       // A4 held, rolled
    (69, 3, ' '), (76, 2, 'c'), (77, 1, ' '),           // A4 . . E5 . F5
    (81, 2, 'c'), (79, 1, ' '), (76, 3, ' '),           // A5 . G5 E5 . .
    (74, 2, ' '), (72, 1, ' '), (70, 2, ' '), (67, 1, ' '),
    (69, 6, 'r'),
];

// The jig (B section): running eighths, cresting on the flat second at the top.
(int, double, char)[] jig =
[
    (81, 1, ' '), (79, 1, ' '), (76, 1, ' '), (79, 1, ' '), (76, 1, ' '), (74, 1, ' '),
    (76, 2, ' '), (72, 1, ' '), (74, 2, ' '), (70, 1, ' '),
    (72, 1, ' '), (70, 1, ' '), (69, 1, ' '), (70, 1, ' '), (72, 1, ' '), (74, 1, ' '),
    (76, 3, 'c'), (75, 3, ' '),                            // E5 sinking onto Eb5: the tritone
    (81, 1, ' '), (79, 1, ' '), (77, 1, ' '), (79, 1, ' '), (81, 1, ' '), (82, 1, ' '),
    (81, 2, ' '), (79, 1, ' '), (76, 2, ' '), (74, 1, ' '),
    (72, 1, ' '), (74, 1, ' '), (76, 1, ' '), (74, 1, ' '), (72, 1, ' '), (70, 1, ' '),
    (69, 6, 't'),                                          // home on a shivering trill
];

// The outro sigh: the full phrygian fall, E D C Bb, home to the tonic.
(int, double, char)[] outro =
[
    (76, 3, 'c'), (74, 3, ' '),
    (72, 2, ' '), (70, 1, ' '), (69, 3, ' '),
    (0, 12, ' '),
];

// ----------------------------------------------------------------- backing

// Bagpipe drone: tonic and fifth, detuned wide enough that the pairs beat
// slowly against each other — a shimmer sharpened into a growl.
foreach (var (midi, detune) in new[] { (57, 1.0025), (64, 0.9975) }) // A3, E4
{
    Pulse(0, TotalSec, Hz(midi), 0.5, 0.040, decayTo: 1.0, vibrato: false);
    Pulse(0, TotalSec, Hz(midi) * detune, 0.5, 0.030, decayTo: 1.0, vibrato: false);
}

// The riff. Roots walk i bVII bII i / i bVII bVI i — the bII and bVI are the
// phrygian gloom — indexed so each 8-bar melody phrase starts on the tonic
// (the intro picks up the tail of the cycle). Each bar: a power chord on the
// downbeat, palm-muted chugs behind it, and an approach note on the last
// eighth — chromatic when the root is about to move, a tritone stab when it
// stays. Triangle carries the weight, a gritty low pulse doubles for edge.
int[] bassRoots = [45, 43, 46, 45, 45, 43, 41, 45]; // A2 G2 Bb2 A2 A2 G2 F2 A2
for (int bar = 2; bar < BarsTotal - 2; bar++)
{
    int idx = ((bar - 4) % 8 + 8) % 8;
    int root = bassRoots[idx];
    int next = bassRoots[(idx + 1) % 8];
    int approach = next == root ? root + 6 : next > root ? next - 1 : next + 1;
    for (int e = 0; e < 6; e++)
    {
        bool accent = e == 0;
        int note = e == 5 ? approach : root;
        double len = accent ? 0.24 : 0.10; // one open hit, then palm mutes
        double at = bar * BarSec + e * EighthSec;
        Triangle(at, len, Hz(note), accent ? 0.32 : 0.22);
        Pulse(at, len, Hz(note), 0.25, accent ? 0.12 : 0.08, decayTo: 0.3, vibrato: false);
        if (accent)
        {
            Triangle(at, len, Hz(note + 7), 0.16); // the power chord's fifth
            Pulse(at, len, Hz(note + 7), 0.25, 0.06, decayTo: 0.3, vibrato: false);
        }
    }
}

// The kit: kick on the downbeat, snare on the backbeat, hats marking the
// lilt; the second air adds a pickup kick, the jig runs double-kick under
// everything. A crash opens each section; snare rolls swell into and out
// of the jig.
for (int bar = 4; bar < 30; bar++)
{
    bool doubleKick = bar >= 20 && bar < 28;
    double t = bar * BarSec;
    Kick(t, 0.50);
    Snare(t + 3 * EighthSec, 0.30);
    Noise(t + 2 * EighthSec, 6000, 0.08, 0.03);
    Noise(t + 5 * EighthSec, 6000, 0.10, 0.03);
    if (doubleKick)
    {
        Kick(t + 1 * EighthSec, 0.26);
        Kick(t + 2 * EighthSec, 0.26);
        Kick(t + 4 * EighthSec, 0.26);
        Kick(t + 5 * EighthSec, 0.26);
    }
    else if (bar >= 12)
        Kick(t + 4 * EighthSec, 0.24);
}

foreach (int rollBar in new[] { 19, 27 })
{
    double t0 = rollBar * BarSec + 4.5 * EighthSec;
    for (int i = 0; i < 6; i++)
        Snare(t0 + i * 0.06, 0.07 + 0.035 * i);
}

foreach (int bar in new[] { 4, 12, 20, 28 })
    Noise(bar * BarSec, 9000, 0.22, 0.30); // crash

// Arpeggiated drone shimmer under the second air and the jig.
int[] arp = [57, 64, 69, 64, 57, 64]; // A3 E4 A4 E4 A3 E4
for (int bar = 12; bar < 28; bar++)
    for (int e = 0; e < 6; e++)
        Pulse(bar * BarSec + e * EighthSec, EighthSec * 0.9, Hz(arp[e]), 0.125, 0.06, decayTo: 0.5, vibrato: false);

// The bell tolls over the intro and the outro (the loop seam) — an octave
// down from where a church would hang it.
foreach (int bar in new[] { 0, 2, 28, 30 })
    Bell(bar * BarSec, Hz(45), 0.18); // A2

// ----------------------------------------------------------------- the tune

PlayAir(air, 4 * 6, 0.26, 0.25, 0.0);              // first air, dry and alone
PlayAir(air, 12 * 6, 0.26, 0.25, 0.09, 0.13);      // second air: echo + thirds
PlayAir(jig, 20 * 6, 0.24, 0.25, 0.08, 0.12);      // the jig, in harmony
PlayAir(outro, 28 * 6, 0.24, 0.25, 0.09, 0.12);    // the sigh home

// ------------------------------------------------------------------- output

double peak = 0;
foreach (var s in buf)
    peak = Math.Max(peak, Math.Abs(s));
double gain = 0.88 / peak;
var samples = new short[buf.Length];
for (int i = 0; i < buf.Length; i++)
    samples[i] = (short)Math.Clamp(buf[i] * gain * 32767.0, short.MinValue, short.MaxValue);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
using (var w = new BinaryWriter(File.Create(outPath)))
{
    int dataBytes = samples.Length * 2;
    const int smplChunk = 8 + 36 + 24;
    w.Write("RIFF"u8);
    w.Write(4 + 8 + 16 + smplChunk + 8 + dataBytes);
    w.Write("WAVE"u8);

    w.Write("fmt "u8);
    w.Write(16);
    w.Write((short)1);            // PCM
    w.Write((short)1);            // mono
    w.Write(Rate);
    w.Write(Rate * 2);            // byte rate
    w.Write((short)2);            // block align
    w.Write((short)16);           // bits

    // smpl chunk: full-length forward loop, honoured by Godot's WAV import.
    w.Write("smpl"u8);
    w.Write(36 + 24);
    w.Write(0); w.Write(0);                       // manufacturer, product
    w.Write(1_000_000_000 / Rate);                // sample period, ns
    w.Write(60); w.Write(0); w.Write(0); w.Write(0); // unity note, fraction, SMPTE
    w.Write(1); w.Write(0);                       // one loop, no extra data
    w.Write(0); w.Write(0);                       // loop id, type = forward
    w.Write(0); w.Write(samples.Length - 1);      // start, end
    w.Write(0); w.Write(0);                       // fraction, infinite

    w.Write("data"u8);
    w.Write(dataBytes);
    foreach (var s in samples)
        w.Write(s);
}

Console.WriteLine($"Wrote {outPath}: {TotalSec:0.00}s, peak {peak:0.00} pre-gain, {new FileInfo(outPath).Length / 1024} KiB");
