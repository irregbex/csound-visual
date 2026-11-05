// IPaintRoom.cs — Paint Rooms foundation: the plug-in point for rooms.
//
// Role: a «room» is one tab of the Paint Rooms window (Paint Synth, Spectrum Room, Image
// Filter, Image Sequencer). Adding a room = writing ONE class that implements IPaintRoom and adding it to the
// list in Views/PaintRoomsWindow.cs (method CreateRooms). The window owns the tab header, the
// keyboard routing and the lifetime; the room owns its content.
//
// Everything a room needs comes through PaintRoomContext: the shared PaintSession (image,
// history, synthesis settings, live player, clipboards), the main view model (console, editor
// tabs, file pickers, the Csound engine) and the window (for dialogs and file pickers with
// custom filters).

using Avalonia.Controls;
using Avalonia.Input;

namespace CsoundVisual.Paint;

/// <summary>Services and shared state given to every room when it is created.</summary>
/// <param name="Session">Shared Paint Rooms state (image, history, synthesis, player, clipboards).</param>
/// <param name="App">The app's main view model (console, editor tabs, save picker, engine).</param>
/// <param name="Window">The Paint Rooms window (owner for dialogs, storage provider for pickers).</param>
public sealed record PaintRoomContext(PaintSession Session, ViewModels.MainViewModel App, Window Window);

/// <summary>One room (tab) of the Paint Rooms window.</summary>
public interface IPaintRoom : IDisposable
{
    /// <summary>Tab title in the current UI language (use <c>Loc.S</c>).</summary>
    string Title { get; }

    /// <summary>Tab tooltip: one or two sentences saying what the room is for.</summary>
    string Tip { get; }

    /// <summary>
    /// The session whose picture this room edits — and therefore whose undo/redo history the
    /// window's Undo / Redo buttons act on (the shared session for Paint Synth, a private one for
    /// the Spectrum Room and the Image Filter, the edited pattern's own session for the Image
    /// Sequencer — it may change while the window is open; the window re-reads it on every use).
    /// </summary>
    PaintSession EditSession { get; }

    /// <summary>
    /// Undoes the room's last change (the window's ↶ button). Default: the <see cref="EditSession"/>'s
    /// history. A room with several histories (the Image Sequencer: patterns + arrangement and
    /// settings) overrides this to undo whatever was changed last.
    /// </summary>
    void Undo() => EditSession.Undo();

    /// <summary>Redoes the last undone change (the window's ↷ button). See <see cref="Undo"/>.</summary>
    void Redo() => EditSession.Redo();

    /// <summary>True when <see cref="Undo"/> has something to undo.</summary>
    bool CanUndo => EditSession.History.CanUndo;

    /// <summary>True when <see cref="Redo"/> has something to redo.</summary>
    bool CanRedo => EditSession.History.CanRedo;

    /// <summary>History label of the step <see cref="Undo"/> would undo (for the button's tooltip), or <c>null</c>.</summary>
    string? UndoLabel => EditSession.History.UndoLabel;

    /// <summary>History label of the step <see cref="Redo"/> would redo, or <c>null</c>.</summary>
    string? RedoLabel => EditSession.History.RedoLabel;

    /// <summary>
    /// True when the room holds work the user has not saved: an edited picture (Paint Synth, Image
    /// Filter), an edited analysed sound (Spectrum Room), patterns / arrangement (Image Sequencer).
    /// Closing the Paint Rooms window asks «Save changes?» for each such room; «Don't save» calls
    /// <see cref="DiscardChanges"/>. Default: false.
    /// </summary>
    bool HasUnsavedChanges => false;

    /// <summary>What the «Save changes to …?» prompt calls this room's work (UI language).</summary>
    string UnsavedName => Title;

    /// <summary>
    /// Saves the room's work: the answer «Save» on close, the window's 💾 button and Ctrl+S. For the
    /// next session (Image Sequencer) or to a file the user picks (PNG / WAV).
    /// </summary>
    /// <returns>False when saving did not happen (file picker cancelled, write failed): the close is abandoned so nothing is lost.</returns>
    Task<bool> SaveChangesAsync() => Task.FromResult(true);

    /// <summary>Drops the room's unsaved work (answer «Don't save»): the next session starts from the last saved state.</summary>
    void DiscardChanges() { }

    /// <summary>The room's whole user interface (created once, kept while the window is open).</summary>
    Control Content { get; }

    /// <summary>Called when the room's tab becomes visible (refresh views from the session).</summary>
    void OnActivated();

    /// <summary>Called when another tab is selected (stop room-specific previews if needed).</summary>
    void OnDeactivated();

    /// <summary>
    /// Window-level key press while this room is active (tunnelling, before focused controls
    /// such as text boxes see it). Return true when handled. Rooms should ignore keys when a
    /// text box has focus (check <paramref name="focusIsTextInput"/>).
    /// </summary>
    /// <param name="e">The key event (do not set <c>Handled</c>; return true instead).</param>
    /// <param name="focusIsTextInput">True when a text input currently has keyboard focus.</param>
    /// <returns>True when the room consumed the key (the window then marks it handled).</returns>
    bool HandleKey(KeyEventArgs e, bool focusIsTextInput);
}
