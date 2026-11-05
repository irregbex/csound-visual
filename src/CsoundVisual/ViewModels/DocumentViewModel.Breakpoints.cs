using CsoundVisual.Editor;

namespace CsoundVisual.ViewModels;

// Breakpoint part of DocumentViewModel: each tab owns its own set of breakpoints. The gutter UI
// lives in Views/MainWindow.Breakpoints.cs and the engine side in MainViewModel.Debug.cs.
public sealed partial class DocumentViewModel
{
    /// <summary>Backing store of <see cref="Breakpoints"/>, created on first use.</summary>
    BreakpointStore? breakpoints;

    /// <summary>
    /// Breakpoints of this document (F9 / click in the gutter), anchored to their lines while the
    /// text is edited. Kept only for the session.
    /// </summary>
    /// <remarks>
    /// Created lazily because most documents never get a breakpoint. The store is bound to
    /// <see cref="Document"/> so its line anchors move when lines are inserted or deleted above
    /// them; it is not saved to disk or into the settings.
    /// </remarks>
    public BreakpointStore Breakpoints => breakpoints ??= new BreakpointStore(Document);
}
