// Renders the Barrow dungeon music to a looping WAV. A .NET 10 file-based app:
//
//   dotnet run tools/GenerateBarrowMusic.cs
//
// writes WoadRaiders.Client/assets/audio/barrow_theme.wav (or pass a path).
// GameScreen picks the track up by map name (Barrow.tscn -> barrow_theme.wav).
//
// Industrial chip-metal in E phrygian, 4/4 at 100 BPM — the sound of the
// Barrow King's machine-crypt. It borrows the churning, build-heavy industrial
// idiom of late-90s Nine Inch Nails (relentless syncopated low riff, mechanical
// percussion, noise risers that swell and drop): a continuous detuned machine
// hum under a palm-muted 16th ostinato on low E with a bII (F) grind and a
// tritone (Bb) stab, mechanical kit with metallic clanks and noise hats, a
// cold sparse lead, and reverse-swell risers between sections. The riffs are
// original — the debt is to the style, not the notes.
//
// Form: intro (hum + clanks + riser) / riff / riff+lead / breakdown (gated
// noise, no bass) / climax (heavier riff + lead + double kick). The hum runs
// the whole length and the climax decays into it, so the file loops seamlessly
// (loop points are in the WAV's smpl chunk and re-asserted at load).

using System;
using System.IO;

const int Rate = 44100;
const double Sx = 0.15;              // one sixteenth: 4/4 at 100 BPM
const int BarsTotal = 32;           // 4 intro / 8 riff / 8 riff+lead / 4 breakdown / 8 climax
const double BarSec = 16 * Sx;
const double TotalSec = BarsTotal * BarSec;

var outPath = args.Length > 0 ? args[0] : Path.Combine("WoadRaiders.Client", "assets", "audio", "barrow_theme.wav");
var buf = new double[(int)(TotalSec * Rate)];
int noiseSeed = 1;

// ---------------------------------------------------------------- waveforms

double Env(double t, double dur, double decayTo)
{
    double attack = Math.Min(1, t / 0.004);
    double release = Math.Clamp((dur - t) / 0.02, 0, 1);
    double decay = 1.0 - (1.0 - decayTo) * Math.Min(1, t / Math.Max(dur, 0.001));
    return attack * release * decay;
}

void Pulse(double start, double dur, double freq, double duty, double vol, double decayTo = 0.8, bool vibrato = false)
{
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, (int)((start + dur) * Rate));
    double phase = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double t = (i - n0) / (double)Rate;
        double f = freq;
        if (vibrato && t > 0.15)
            f *= 1.0 + 0.004 * Math.Sin(Math.Tau * 5.5 * (t - 0.15));
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

void Kick(double start, double vol)
{
    // Industrial kick: a square whose pitch drops 175->42 Hz in milliseconds.
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, n0 + (int)(0.11 * Rate));
    double phase = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double t = (i - n0) / (double)Rate;
        phase += (42 + 133 * Math.Exp(-t / 0.018)) / Rate;
        buf[i] += (phase % 1.0 < 0.5 ? 1.0 : -1.0) * vol * Math.Exp(-t / 0.05) * Math.Min(1, t / 0.001);
    }
}

void Snare(double start, double vol)
{
    Noise(start, 3500, vol, 0.05);
    Pulse(start, 0.035, 180, 0.5, vol * 0.5, decayTo: 0.2);
}

void Clank(double start, double vol)
{
    // A metallic hit: two inharmonic square partials (ratio ~1.57) ringing over
    // a bright noise transient — the factory-floor clang.
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, n0 + (int)(0.28 * Rate));
    double a = 0, b = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double t = (i - n0) / (double)Rate;
        a += 327.0 / Rate;
        b += 514.0 / Rate;
        double s = (a % 1.0 < 0.5 ? 1.0 : -1.0) * 0.6 + (b % 1.0 < 0.5 ? 1.0 : -1.0) * 0.4;
        buf[i] += s * vol * Math.Exp(-t / 0.06);
    }
    Noise(start, 8000, vol * 0.5, 0.02);
}

void Hat(double start, double vol) => Noise(start, 7000, vol, 0.012);

void Riser(double start, double dur, double vol)
{
    // Reverse-swell noise: brightness and volume climb across the span, the
    // signature industrial lead-in that drops into the next section's downbeat.
    var rnd = new Random(noiseSeed++);
    int n0 = (int)(start * Rate);
    int n1 = Math.Min(buf.Length, (int)((start + dur) * Rate));
    double hold = 0;
    int countdown = 0;
    for (int i = Math.Max(0, n0); i < n1; i++)
    {
        double p = (i - n0) / (double)(n1 - n0);
        int holdSamples = Math.Max(1, (int)(Rate / (400 + p * p * 8000)));
        if (countdown-- <= 0)
        {
            hold = rnd.NextDouble() * 2 - 1;
            countdown = holdSamples;
        }
        buf[i] += hold * vol * p * p;
    }
}

double Hz(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);

// ------------------------------------------------------------- riff & lead

const int RiffRoot = 40; // E2 (~82 Hz); a sub octave doubles it for weight

// A distorted "power" bass hit: sub-octave triangle for weight, a detuned
// pulse pair for grind, the fifth stacked on accents. Short = palm-muted.
void BassHit(double at, double len, int midi, double vol, bool fifth)
{
    Triangle(at, len, Hz(midi - 12), vol * 0.9);
    Pulse(at, len, Hz(midi), 0.5, vol * 0.7, decayTo: 0.25);
    Pulse(at, len, Hz(midi) * 1.006, 0.5, vol * 0.4, decayTo: 0.25);
    if (fifth)
        Pulse(at, len, Hz(midi + 7), 0.5, vol * 0.35, decayTo: 0.25);
}

// A 16-slot riff: offset above E, or -1 for rest. Downbeats ring open; the
// off-beats are choked mutes.
void PlayRiff(int[] offs, int bar, double vol)
{
    for (int s = 0; s < 16; s++)
    {
        if (offs[s] < 0)
            continue;
        bool accent = s % 4 == 0;
        double at = bar * BarSec + s * Sx;
        double len = accent ? Sx * 1.7 : Sx * 0.65;
        BassHit(at, len, RiffRoot + offs[s], vol * (accent ? 1.0 : 0.7), fifth: accent);
    }
}

// Main churn: driving E with a bII (F) grind and an octave stab.
int[] riffA = [0, 0, -1, 0, -1, 0, 0, -1, 1, -1, 0, 0, -1, 0, 12, -1];
// Climax: heavier, the F grind doubled and a tritone (Bb) shoved in.
int[] riffB = [0, 0, 0, -1, 1, 1, -1, 0, 0, 0, 6, -1, 0, -1, 3, 1];

// Cold, sparse lead in E phrygian (F natural = the bII), thin and detuned for
// width. A haunted two-bar phrase (32 sixteenths) meant to repeat, hypnotic.
void PlayLead((int midi, int sixteenths)[] notes, double startBar, double vol)
{
    double at = startBar * BarSec;
    foreach (var (midi, sixteenths) in notes)
    {
        double dur = sixteenths * Sx - 0.02;
        if (midi > 0)
        {
            Pulse(at, dur, Hz(midi), 0.125, vol, decayTo: 0.6);
            Pulse(at, dur, Hz(midi) * 0.997, 0.125, vol * 0.5, decayTo: 0.6);
        }
        at += sixteenths * Sx;
    }
}

(int, int)[] lead =
[
    (71, 3), (72, 1), (71, 2), (0, 2),      // B4 C5 B4 .
    (67, 2), (69, 2), (67, 1), (65, 3),     // G4 A4 G4 F4
    (64, 4), (65, 2), (64, 2),              // E4 . F4 E4
    (71, 4), (69, 2), (67, 2),              // B4 . A4 G4
];

// ---------------------------------------------------------------- arrange

// Continuous machine hum: detuned E and B (fifth), an octave apart, humming
// under the whole track so the loop seam never gaps.
foreach (var (midi, detune) in new[] { (28, 1.004), (35, 0.996) }) // E1, B1
{
    Pulse(0, TotalSec, Hz(midi), 0.5, 0.035, decayTo: 1.0);
    Pulse(0, TotalSec, Hz(midi) * detune, 0.5, 0.028, decayTo: 1.0);
}

for (int bar = 0; bar < BarsTotal; bar++)
{
    double t = bar * BarSec;
    bool intro = bar < 4;
    bool riffSec = bar >= 4 && bar < 12;
    bool leadSec = bar >= 12 && bar < 20;
    bool breakdown = bar >= 20 && bar < 24;
    bool climax = bar >= 24;

    // Bass
    if (riffSec || leadSec)
        PlayRiff(riffA, bar, 0.30);
    else if (climax)
        PlayRiff(riffB, bar, 0.34);

    // Drums
    if (riffSec || leadSec || climax)
    {
        int[] kicks = climax ? [0, 2, 3, 8, 10, 11] : [0, 3, 8, 11];
        foreach (int k in kicks)
            Kick(t + k * Sx, 0.5);
        foreach (int sn in new[] { 4, 12 })
        {
            Snare(t + sn * Sx, 0.26);
            Clank(t + sn * Sx, 0.16);
        }
        if (climax)
        {
            Clank(t + 6 * Sx, 0.14);
            Clank(t + 14 * Sx, 0.14);
        }
        for (int s = 0; s < 16; s += 2)
            Hat(t + s * Sx, s % 4 == 0 ? 0.05 : 0.09);
    }
    else if (intro)
    {
        if (bar % 2 == 1)
            Clank(t, 0.12);
        for (int s = 0; s < 16; s += 4)
            Hat(t + s * Sx, 0.05);
    }
    else // breakdown: gated noise pulses, sparse kit, no bass — the tension drop
    {
        for (int s = 0; s < 16; s += 2)
            Noise(t + s * Sx, 2500, 0.14, 0.04);
        Kick(t, 0.42);
        Kick(t + 8 * Sx, 0.42);
        Snare(t + 4 * Sx, 0.24);
        Snare(t + 12 * Sx, 0.24);
        Clank(t + (bar % 2 == 0 ? 6 : 10) * Sx, 0.16);
    }
}

// Lead: the phrase four times over the second riff section, then over the
// climax a touch louder.
for (int r = 0; r < 4; r++)
    PlayLead(lead, 12 + r * 2, 0.19);
for (int r = 0; r < 4; r++)
    PlayLead(lead, 24 + r * 2, 0.22);

// Crashes opening each section.
foreach (int bar in new[] { 4, 12, 24 })
    Noise(bar * BarSec, 9000, 0.22, 0.30);

// Risers: a small one into the lead, the big drop into the breakdown, and the
// swell that hurls the track into the climax.
Riser(3 * BarSec, BarSec, 0.26);   // into the first riff
Riser(11 * BarSec, BarSec, 0.20);  // into the lead
Riser(23 * BarSec, BarSec, 0.34);  // into the climax

// ------------------------------------------------------------------- output

double peak = 0;
foreach (var s in buf)
    peak = Math.Max(peak, Math.Abs(s));
double gain = 0.90 / peak;
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
