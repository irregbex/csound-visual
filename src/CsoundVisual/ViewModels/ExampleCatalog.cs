using System.Text.Json;
using System.Text.Json.Serialization;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>One example of the bundled collection (an entry of <c>Examples/index.json</c>).</summary>
/// <remarks>
/// Deserialised with System.Text.Json; the <c>[property: JsonPropertyName]</c> attributes map the
/// lower-case JSON keys written by <c>tools/import_examples.py</c> onto the positional record
/// parameters. Everything below the constructor is computed for the example browser
/// (<c>Views/ExamplesWindow.cs</c>): localised title/description, list label, display category
/// and the cached search text. Showcase-style entries carry their own Spanish texts
/// (<c>titleEs</c>, <c>descriptionEs</c>); manual entries exist only in English.
/// </remarks>
/// <param name="File">Path relative to the Examples folder ("Manual/oscil.csd").</param>
/// <param name="Title">Opcode the example belongs to (or the file name).</param>
/// <param name="Name">File name without extension ("oscil_2").</param>
/// <param name="Category">Manual category ("Signal Generators:Basic Oscillators").</param>
/// <param name="Description">One-line description of the opcode, from the manual (English).</param>
/// <param name="Source">"manual" (csound/manual repository), "csound7" (the installation), or "showcase" / "audiofx" / "cabbage" for the app's curated galleries.</param>
public sealed record ExampleEntry(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("source")] string Source)
{
    /// <summary>Spanish title (curated gallery pieces only).</summary>
    [JsonPropertyName("titleEs")] public string? TitleEs { get; init; }

    /// <summary>Spanish description (curated gallery pieces only).</summary>
    [JsonPropertyName("descriptionEs")] public string? DescriptionEs { get; init; }

    /// <summary>Title in the interface language.</summary>
    public string DisplayTitle => !Loc.IsEnglish && !string.IsNullOrEmpty(TitleEs) ? TitleEs : Title;

    /// <summary>Description in the interface language (manual descriptions exist only in English).</summary>
    public string DisplayDescription => !Loc.IsEnglish && !string.IsNullOrEmpty(DescriptionEs) ? DescriptionEs : Description;

    /// <summary>True for the curated galleries (Showcase, Audio processing and Cabbage instruments).</summary>
    /// <remarks>Curated pieces are labelled by their (localised) title alone, see <see cref="Label"/>.</remarks>
    public bool IsShowcase => Category is ExampleCatalog.ShowcaseCategory or ExampleCatalog.AudioFxCategory or ExampleCatalog.CabbageCategory;

    /// <summary>Live effect meant for the «Csound Visual FX» VST3 plugin (processes the track's input).</summary>
    [JsonPropertyName("live")] public bool Live { get; init; }

    /// <summary>Absolute path of the .csd next to the executable.</summary>
    /// <remarks><see cref="File"/> always uses '/' in the index; it is converted to the platform separator here.</remarks>
    public string FullPath => Path.Combine(ExampleCatalog.Folder, File.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Category for display: "Signal Generators › Basic Oscillators", with a localised suffix for
    /// live effects meant for the VST3 FX plug-in.
    /// </summary>
    public string CategoryDisplay => ExampleCatalog.DisplayCategory(Category) + (Live ? Loc.S("  · efecto en vivo para Cubase", "  · live effect for Cubase") : "");

    /// <summary>List label: the opcode, plus the file name when it differs ("oscil · oscil_2").</summary>
    public string Label => IsShowcase || string.Equals(Title, Name, StringComparison.OrdinalIgnoreCase) ? DisplayTitle : $"{Title}  ·  {Name}";

    /// <summary>Lower-case text searched by the filter box.</summary>
    /// <remarks>
    /// Includes both languages, so a search works whatever the interface language. Built on first
    /// use and cached: the filter runs over ~1500 entries on every keystroke.
    /// </remarks>
    internal string SearchText => SearchTextCache.GetValue(this,
        e => $"{e.Title} {e.TitleEs} {e.Name} {e.Category} {e.Description} {e.DescriptionEs}".ToLowerInvariant());

    /// <summary>Cache behind <see cref="SearchText"/>, keyed by entry identity.</summary>
    /// <remarks>
    /// Bug fix (record equality): the cache used to be an instance field, and the compiler-generated
    /// record <c>Equals</c>/<c>GetHashCode</c> include every instance field — so two identical
    /// entries compared unequal once only one of them had been searched, and an entry's hash code
    /// changed after its first search (breaking it as a dictionary/set key). A
    /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey, TValue}"/> keeps the
    /// cache outside the record (it compares keys by reference and lets entries be collected).
    /// </remarks>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ExampleEntry, string> SearchTextCache = new();
}

/// <summary>
/// The example collection bundled with the app: ~1500 examples of the official Csound manual
/// (one or more per opcode, with the sound files they use) plus the examples of the Csound 7
/// installation. Imported by <c>tools/import_examples.py</c> into <c>src/CsoundVisual/Examples</c>
/// and copied next to the executable at build time.
/// </summary>
/// <remarks>
/// <para>
/// Besides the manual, <c>index.json</c> lists the app's own curated galleries (Showcase, Audio
/// processing, Cabbage instruments). The import script writes the entries already in display
/// order (curated galleries first, then manual categories), so this class never sorts: it only
/// loads the file once and filters it.
/// </para>
/// <para>
/// Static and lazily loaded: the index is read the first time <see cref="All"/> is touched (the
/// side-panel summary or the example browser), on the UI thread, and kept for the session. A
/// missing or malformed index yields an empty catalog instead of an exception, and
/// <see cref="Summary"/> tells the user the folder was not found.
/// </para>
/// </remarks>
public static class ExampleCatalog
{
    /// <summary>Shape of <c>index.json</c>: <c>{ "examples": [ … ] }</c>.</summary>
    sealed record IndexFile([property: JsonPropertyName("examples")] List<ExampleEntry> Examples);

    /// <summary>Cache behind <see cref="All"/>; <c>null</c> until the index has been loaded.</summary>
    static IReadOnlyList<ExampleEntry>? all;

    /// <summary>Category name of the curated gallery (listed first).</summary>
    public const string ShowcaseCategory = "Showcase";

    /// <summary>Category name of the audio-processing gallery (listed second).</summary>
    public const string AudioFxCategory = "AudioFX";

    /// <summary>Category name of the Cabbage instruments gallery (files with a &lt;Cabbage&gt; section, listed third).</summary>
    public const string CabbageCategory = "Cabbage";

    /// <summary>Category name for display (the curated galleries get a star and a translated name).</summary>
    /// <param name="category">Raw category from the index; manual categories use ':' between levels.</param>
    /// <returns>The localised gallery name, or the manual category with " › " between levels.</returns>
    public static string DisplayCategory(string category) => category switch
    {
        ShowcaseCategory => Loc.S("★ Lo mejor de Csound", "★ Csound showcase"),
        AudioFxCategory => Loc.S("★ Procesamiento de audio", "★ Audio processing"),
        CabbageCategory => Loc.S("★ Instrumentos Cabbage", "★ Cabbage instruments"),
        _ => category.Replace(":", " › "),
    };

    /// <summary>Folder holding the examples and index.json (next to the executable).</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Examples");

    /// <summary>Every example, sorted by category then opcode (empty if the folder is missing).</summary>
    /// <remarks>The order is the one in <c>index.json</c> (the import script sorts it).</remarks>
    public static IReadOnlyList<ExampleEntry> All => all ??= Load();

    /// <summary>Distinct categories in display order.</summary>
    /// <remarks>
    /// <see cref="Enumerable.Distinct{TSource}(IEnumerable{TSource})"/> keeps first-occurrence
    /// order, so this follows the index order. Recomputed on every access (callers read it once).
    /// </remarks>
    public static IReadOnlyList<string> Categories => All.Select(e => e.Category).Distinct().ToList();

    /// <summary>True when <paramref name="path"/> is inside the bundled examples folder (edits must be saved elsewhere).</summary>
    /// <param name="path">A document path, or <c>null</c> for an untitled document.</param>
    /// <remarks>
    /// Used so «Save» on an example turns into «Save as» (the bundled files are never overwritten)
    /// and so the example browser can replace a clean example tab instead of piling up tabs.
    /// The trailing separator prevents a sibling folder such as <c>Examples2</c> from matching.
    /// </remarks>
    public static bool Contains(string? path) =>
        path is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Examples whose text matches every word of <paramref name="query"/>, optionally in one category.</summary>
    /// <param name="query">Space-separated words (case-insensitive substring match, AND); empty or <c>null</c> matches all.</param>
    /// <param name="category">Raw category to restrict to, or <c>null</c> for all categories.</param>
    /// <returns>A lazily evaluated sequence in catalog order.</returns>
    public static IEnumerable<ExampleEntry> Filter(string? query, string? category)
    {
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.Where(e => (category is null || e.Category == category) && words.All(w => e.SearchText.Contains(w)));
    }

    /// <summary>Reads and deserialises <c>Examples/index.json</c>.</summary>
    /// <returns>The entries, or an empty list when the file is missing or cannot be parsed.</returns>
    static IReadOnlyList<ExampleEntry> Load()
    {
        try
        {
            string index = Path.Combine(Folder, "index.json");
            if (!System.IO.File.Exists(index)) return [];
            return JsonSerializer.Deserialize<IndexFile>(System.IO.File.ReadAllText(index))?.Examples ?? [];
        }
        catch (Exception)
        {
            // A broken index must not prevent the app from starting; the browser just shows nothing.
            return [];
        }
    }

    /// <summary>Short text for the side panel: how many examples there are.</summary>
    public static string Summary => All.Count == 0
        ? Loc.S("No se encontró la carpeta Examples junto al programa.", "The Examples folder next to the program was not found.")
        : Loc.S($"{All.Count} ejemplos del manual de Csound, por categoría y con búsqueda.", $"{All.Count} examples from the Csound manual, by category and searchable.");
}
