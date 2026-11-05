using CsoundVisual.Cabbage;
using CsoundVisual.Engine;

namespace CsoundVisual.ViewModels;

/// <summary>
/// Cabbage support of a document: the parsed <c>&lt;Cabbage&gt;</c> section, or the widgets
/// declared with <c>;@knob</c>-style comments in an ordinary document.
/// </summary>
/// <remarks>
/// The form is consumed by <see cref="CabbageCard"/> (side panel) and the detachable
/// <c>CabbageWindow</c>, both through <see cref="CabbageTracker"/>; its widget channels are also merged into <see cref="Channels"/> by
/// <see cref="RefreshChannels"/>, so Cabbage widgets drive the same
/// <see cref="ChannelControlViewModel"/> instances as the ordinary sliders (MIDI learn keeps
/// working on them).
/// </remarks>
public sealed partial class DocumentViewModel
{
    /// <summary>
    /// Fingerprint of the text the current <see cref="cabbageForm"/> was parsed from: "C" + tag
    /// line + section text for a <c>&lt;Cabbage&gt;</c> section, "A" + annotation key for
    /// <c>;@knob</c> annotations, or <c>null</c> when the document has neither. Comparing it is
    /// much cheaper than re-parsing, and edits elsewhere in the file leave it unchanged.
    /// </summary>
    string? cabbageKey;

    /// <summary>Last parsed widget form (see <see cref="CabbageForm"/>).</summary>
    CabbageForm? cabbageForm;

    /// <summary>
    /// The widgets of the document — from its <c>&lt;Cabbage&gt;</c> section or, when it has
    /// none, from <c>;@knob</c> annotations — or <c>null</c>. Re-parsed by
    /// <see cref="RefreshChannels"/> only when the relevant text changed.
    /// </summary>
    public CabbageForm? CabbageForm => cabbageForm;

    /// <summary>True when the document is a Cabbage instrument (has a <c>&lt;Cabbage&gt;</c> section).</summary>
    public bool IsCabbageDocument => cabbageForm is { FromAnnotations: false };

    /// <summary>True when the document has widgets to show (Cabbage section or annotations).</summary>
    public bool HasWidgetPanel => cabbageForm is not null;

    /// <summary>
    /// Re-parses the widgets when their source text changed (raising change notifications) and
    /// returns the current form.
    /// </summary>
    /// <param name="text">Full text of the document.</param>
    /// <returns>The current form, or <c>null</c> when the document declares no widgets.</returns>
    /// <remarks>
    /// A <c>&lt;Cabbage&gt;</c> section wins over annotations. The tag line is part of the key
    /// because the parser reports warnings with absolute line numbers: moving the section must
    /// re-parse it even if its text is identical.
    /// </remarks>
    CabbageForm? UpdateCabbage(string text)
    {
        var section = CabbageParser.FindSection(text);
        string? key = section is { } s ? "C" + s.TagLine + "\n" + s.Section
                    : CabbageSupport.AnnotationKey(text) is { } a ? "A" + a : null;
        if (key == cabbageKey) return cabbageForm; // unchanged: keep the form (and its widget identities)
        cabbageKey = key;
        cabbageForm = section is { } sec ? CabbageParser.ParseSection(sec.Section, sec.TagLine)
                    : key is null ? null : CabbageSupport.FromAnnotations(text, CsdTools.DetectChannels(text));
        // Hand-raised notifications: these are computed properties over plain fields, so the
        // source generator does not know about them. CabbageTracker (CabbageCard.cs) rebuilds the
        // widget panels when it sees CabbageForm change.
        OnPropertyChanged(nameof(CabbageForm));
        OnPropertyChanged(nameof(IsCabbageDocument));
        OnPropertyChanged(nameof(HasWidgetPanel));
        return cabbageForm;
    }
}
