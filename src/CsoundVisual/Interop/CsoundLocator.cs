using System.Reflection;
using System.Runtime.InteropServices;

namespace CsoundVisual.Interop;

/// <summary>
/// Finds the Csound 7 installation the application runs against and binds
/// <c>csound7_64.dll</c> from it.
/// </summary>
/// <remarks>
/// Search order:
/// <list type="number">
/// <item>The <c>CSOUND_VISUAL_HOME</c> environment variable (an installation root containing <c>bin\csound7_64.dll</c>).</item>
/// <item>A <c>Csound7</c> folder next to the executable (the portable release package).</item>
/// <item>An <c>@Source\Csound7</c> folder found by walking up from the executable directory
/// (this is how the development tree is laid out).</item>
/// <item><c>%ProgramFiles%\Csound7</c>, the default location of the official installer.</item>
/// </list>
/// </remarks>
internal static class CsoundLocator
{
    /// <summary>Installation root, or <c>null</c> if <see cref="Initialize"/> failed.</summary>
    public static string? Home { get; private set; }

    /// <summary>Folder holding <c>csound7_64.dll</c> and the command-line tools.</summary>
    public static string? BinDir => Home is null ? null : Path.Combine(Home, "bin");

    /// <summary>Folder holding opcode and real-time audio plugins (passed to <c>csoundCreate</c>).</summary>
    public static string? PluginsDir => Home is null ? null : Path.Combine(Home, "plugins64");

    /// <summary>Folder with the example <c>.csd</c> files shipped with the installation.</summary>
    public static string? ExamplesDir => Home is null ? null : Path.Combine(Home, "examples");

    /// <summary>
    /// Locates the installation, registers the native library resolver and runs
    /// <c>csoundInitialize</c>. Returns <c>false</c> when no installation was found; in that case
    /// no <see cref="CsoundNative"/> function may be called.
    /// </summary>
    public static bool Initialize()
    {
        Home = FindHome();
        if (Home is null)
            return false;

        // The DLL's own native dependencies are resolved through PATH, so put bin first.
        // OPCODE7DIR64 is what csound.exe uses to find plugins; set it too for consistency.
        Environment.SetEnvironmentVariable("PATH", BinDir + ";" + Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("OPCODE7DIR64", PluginsDir);

        // Let orchestras find the data files shipped with Csound (HRTF sets for hrtfmove2,
        // sf_GMbank.sf2…) by name: SADIR/SSDIR are Csound's analysis and sound search paths.
        string samples = Path.Combine(Home, "samples");
        if (Directory.Exists(samples))
        {
            Environment.SetEnvironmentVariable("SADIR", samples);
            Environment.SetEnvironmentVariable("SSDIR", samples);
        }

        string dll = Path.Combine(BinDir!, CsoundNative.Lib + ".dll");
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
            name == CsoundNative.Lib ? NativeLibrary.Load(dll) : IntPtr.Zero);

        // Csound must not hijack Ctrl+C or run atexit hooks inside a GUI process.
        CsoundNative.csoundInitialize(CsoundNative.CSOUNDINIT_NO_SIGNAL_HANDLER | CsoundNative.CSOUNDINIT_NO_ATEXIT);
        return true;
    }

    /// <summary>Formats <c>csoundGetVersion()</c> as <c>"Csound 7.0.0"</c>.</summary>
    /// <remarks>The native value encodes the version as <c>major·1000 + minor·10 + patch</c> (7000 = 7.0.0).</remarks>
    public static string VersionText
    {
        get
        {
            int v = CsoundNative.csoundGetVersion();
            return $"Csound {v / 1000}.{v / 10 % 100}.{v % 10}";
        }
    }

    /// <summary>
    /// Applies the search order described on the class and returns the first folder whose
    /// <c>bin</c> subfolder contains <c>csound7_64.dll</c>.
    /// </summary>
    /// <returns>The installation root, or <c>null</c> when none of the candidates qualifies.</returns>
    static string? FindHome()
    {
        static bool IsHome(string dir) => File.Exists(Path.Combine(dir, "bin", CsoundNative.Lib + ".dll"));

        string? env = Environment.GetEnvironmentVariable("CSOUND_VISUAL_HOME");
        if (!string.IsNullOrWhiteSpace(env) && IsHome(env))
            return env;

        // Portable package: Csound travels in a Csound7 folder next to the program.
        string beside = Path.Combine(AppContext.BaseDirectory, "Csound7");
        if (IsHome(beside))
            return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "@Source", "Csound7");
            if (IsHome(candidate))
                return candidate;
        }

        string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Csound7");
        return IsHome(programFiles) ? programFiles : null;
    }
}
