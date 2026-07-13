using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// A persistent, autoloaded music player (registered as the "MusicJukebox"
/// autoload in project.godot) that lives outside the scene tree's churn, so a
/// theme keeps playing without a gap or a restart as the player walks between
/// screens — where per-screen players would be freed and re-created on every
/// scene change.
///
/// <para>Screens ask for the track they want in <c>_Ready</c>. Asking for the
/// track already playing is a no-op — the music simply continues — so title →
/// character select → dungeon/raid select carries the one title theme through
/// seamlessly. A different track (a dungeon's map theme) replaces it, and
/// <see cref="Silence"/> stops it outright (the run-summary screen wants quiet).</para>
/// </summary>
public partial class MusicJukebox : Node
{
    public const string TitleTheme = "res://assets/audio/title_theme.wav";

    /// <summary>The live jukebox. Set in <see cref="_Ready"/>, which the engine
    /// runs before any regular scene's <c>_Ready</c> because autoloads enter the
    /// tree first — so screens can reach it the moment they build.</summary>
    public static MusicJukebox Instance { get; private set; } = null!;

    private AudioStreamPlayer _player = null!;
    private string? _track;

    public override void _Ready()
    {
        Instance = this;
        _player = new AudioStreamPlayer();
        AddChild(_player);
    }

    /// <summary>The shared menu theme carried across every screen before the
    /// dungeon, at one fixed volume so the level never jumps mid-track.</summary>
    public void PlayTitleTheme() => Play(TitleTheme, -6f);

    /// <summary>Loop <paramref name="resPath"/> at <paramref name="volumeDb"/>. If it
    /// is already the current track the music plays on untouched (only the volume is
    /// re-applied); a different track replaces it; a missing file leaves silence.</summary>
    public void Play(string resPath, float volumeDb)
    {
        _player.VolumeDb = volumeDb;
        if (_track == resPath && _player.Playing)
            return;

        var stream = MusicPlayer.LoadLooping(resPath);
        _track = stream == null ? null : resPath;
        _player.Stream = stream;
        if (stream != null)
            _player.Play();
        else
            _player.Stop();
    }

    /// <summary>Cut the music, for screens that want silence (the run summary).</summary>
    public void Silence()
    {
        _player.Stop();
        _player.Stream = null;
        _track = null;
    }
}
