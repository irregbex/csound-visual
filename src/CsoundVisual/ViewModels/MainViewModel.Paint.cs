// MainViewModel.Paint.cs — Paint Rooms services of the main view model.
//
// Role: opens the Paint Rooms window (command + opener delegate set by MainWindow, shortcut
// Ctrl+Shift+I), keeps the Paint Rooms session for the whole app run (closing the window loses
// nothing), and implements IPaintEngineHost so the live player can play images through the app's
// Csound engine — starting a tiny «Paint Rooms host» orchestra when Csound is stopped, exactly like
// the piano roll starts the document when its Play is pressed.

using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Engine;
using CsoundVisual.Paint;

namespace CsoundVisual.ViewModels;

public sealed partial class MainViewModel : IPaintEngineHost
{
    PaintSession? paintSession;
    DocumentViewModel? paintHostDocument;

    /// <summary>Opens (or focuses) the Paint Rooms window; provided by the view.</summary>
    public Action? ShowPaintRooms { get; set; }

    /// <summary>Paint Rooms (Ctrl+Shift+I): compose and design sound by painting images.</summary>
    [RelayCommand]
    void OpenPaintRooms() => ShowPaintRooms?.Invoke();

    /// <summary>
    /// The Paint Rooms state (image, history, synthesis settings, live player), created on first
    /// use from the saved preferences and kept for the whole session.
    /// </summary>
    public PaintSession PaintSession
    {
        get
        {
            if (paintSession is null)
            {
                settings.Paint ??= new PaintRoomSettings();
                paintSession = new PaintSession(settings.Paint) { Player = new PaintLivePlayer(this) };
            }
            return paintSession;
        }
    }

    /// <summary>
    /// True when the Paint Synth picture has changes that would be lost on exit (it is not kept
    /// between sessions): edited since the last PNG export / example / new canvas, and not blank.
    /// False when Paint Rooms was never opened (the session does not exist yet).
    /// </summary>
    public bool PaintSynthHasUnsavedPicture => paintSession is { HasUnsavedPicture: true, PictureIsBlank: false };

    /// <summary>
    /// The Image Filter canvas when it has changes that would be lost on exit (it lives for the app
    /// run only), else <c>null</c>.
    /// </summary>
    public PaintSession? ImageFilterUnsavedCanvas =>
        paintSession is not null && Views.ImageFilterRoom.CanvasFor(paintSession) is { HasUnsavedPicture: true, PictureIsBlank: false } c ? c : null;

    /// <summary>Writes the settings file now (Paint Rooms calls it when its window closes).</summary>
    public void SavePaintSettings() => settings.Save();

    /// <summary>Opens a .csd written by Paint Rooms («Show Csound code») in a new editor tab.</summary>
    public void OpenPaintCode(string csdPath) => OpenPath(csdPath);

    /// <summary>Writes an information line to the main console (used by Paint Rooms).</summary>
    public void PaintInfo(string text) => AddInfo(text);

    // ------------------------------------------------------------------------------------------
    //  IPaintEngineHost
    // ------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    CsoundEngine IPaintEngineHost.Engine => engine;

    /// <inheritdoc/>
    bool IPaintEngineHost.IsPlayingLive => Mode == RunMode.Play;

    /// <inheritdoc/>
    bool IPaintEngineHost.IsRenderingFile => Mode == RunMode.Render;

    /// <inheritdoc/>
    bool IPaintEngineHost.StartPaintHost()
    {
        if (Mode == RunMode.Play) return true;
        if (Mode != RunMode.None || !csoundAvailable) return false;
        // A document that is not shown as a tab: the console says «Playing Paint Rooms host.csd»,
        // the main Stop button and the meters work as for any document.
        var doc = new DocumentViewModel(PaintLivePlayer.HostCsd(), null, SendChannelValue) { UntitledName = "Paint Rooms host.csd" };
        var (csd, options) = CsdTools.ExtractOptions(doc.Document.Text);
        var host = ApplyAudioOptions(options);
        options.Add("-d");
        paintHostDocument = doc;
        Launch(doc, csd, options, RunMode.Play, host);
        return Mode == RunMode.Play;
    }

    /// <inheritdoc/>
    bool IPaintEngineHost.IsPaintHostRunning =>
        Mode == RunMode.Play && paintHostDocument is not null && ReferenceEquals(lastRunDocument, paintHostDocument);

    /// <inheritdoc/>
    void IPaintEngineHost.StopPaintHost()
    {
        if (((IPaintEngineHost)this).IsPaintHostRunning)
            engine.Stop();
    }
}
