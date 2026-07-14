using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Loads the game's WAV music tracks (rendered by the tools/Generate*Music.cs
/// file-based apps — tweak a tune there and regenerate). Playback itself is owned
/// by the autoloaded <see cref="MusicJukebox"/>, which keeps a single persistent
/// player alive across scene changes so a theme carries seamlessly between screens.
/// </summary>
public static class MusicPlayer
{
    /// <summary>Load <paramref name="resPath"/> as a forward-looping stream, or
    /// return null if the file is absent (silence, never a crash).</summary>
    public static AudioStreamWav? LoadLooping(string resPath)
    {
        var stream = Load(resPath);
        if (stream == null)
            return null;

        // The wav carries its loop in a smpl chunk, but only the editor import
        // honours it — re-assert so the raw-file path loops too. Frame count
        // must come from the duration, not Data.Length byte math: the editor
        // import compresses Data (QOA), so bytes no longer map to frames.
        stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        stream.LoopBegin = 0;
        stream.LoopEnd = (int)Math.Round(stream.GetLength() * stream.MixRate);
        return stream;
    }

    /// <summary>True if a track exists at <paramref name="resPath"/> (imported or raw).</summary>
    public static bool Exists(string resPath) =>
        ResourceLoader.Exists(resPath) || Godot.FileAccess.FileExists(resPath);

    private static AudioStreamWav? Load(string resPath) =>
        // The imported resource exists once the editor has scanned the file
        // (and is all an exported build ships); reading the raw wav covers
        // running from the CLI before that first import.
        ResourceLoader.Exists(resPath) ? ResourceLoader.Load<AudioStreamWav>(resPath)
        : Godot.FileAccess.FileExists(resPath) ? AudioStreamWav.LoadFromFile(resPath)
        : null;
}
