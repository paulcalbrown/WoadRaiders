using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Loads a WAV and plays it as a looping music track. The tracks are rendered
/// by the tools/Generate*Music.cs file-based apps (dotnet run) — tweak a tune
/// there and regenerate.
/// </summary>
public static class MusicPlayer
{
    /// <summary>Start <paramref name="resPath"/> looping under <paramref name="parent"/>,
    /// or return null if the file is absent (silence, never a crash). The player
    /// is a child of the caller, so freeing the caller — a scene change — stops
    /// the music.</summary>
    public static AudioStreamPlayer? Loop(Node parent, string resPath, float volumeDb)
    {
        var stream = Load(resPath);
        if (stream == null)
            return null;

        // The wav carries its loop in a smpl chunk, but only the editor import
        // honours it — re-assert so the raw-file path loops too.
        stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        stream.LoopBegin = 0;
        stream.LoopEnd = stream.Data.Length / 2; // 16-bit mono: two bytes per frame

        var player = new AudioStreamPlayer { Stream = stream, VolumeDb = volumeDb };
        parent.AddChild(player);
        player.Play();
        return player;
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
