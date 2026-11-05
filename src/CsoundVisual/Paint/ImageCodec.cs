// ImageCodec.cs — Paint Rooms foundation.
//
// Role: pictures ↔ SpectralImage. Decodes picture files with the Windows Imaging Component (WinRT
// BitmapDecoder: most formats, EXIF rotation, sRGB) and falls back to SkiaSharp (the graphics
// library Avalonia already ships, so no new dependency); scales them to the canvas size with good
// filtering (SkiaSharp) and maps colours to the left/right levels (ColorMapping). Exports a
// SpectralImage as a PNG in the app's «data» colour convention:
//     red = left level, green = right level, blue = min(left, right)
// so a mono image exports as plain grey (brightness = level) and a stereo one re-imports exactly
// with the RedGreen mapping.
//
// Orientation: the TOP row of a picture is the HIGHEST pitch row of the canvas (row Height−1).
// Pure computation apart from file I/O; thread-safe.
//
// BIT DEPTHS AND THE TWO IMPORT PATHS
//   * 8-bit pictures with the three classic mappings (Brightness, RedGreen, BrightnessHuePan) use the
//     original path: SkiaSharp scales a premultiplied RGBA8 bitmap over black (FromBitmap). Levels
//     come in steps of 1/255.
//   * 16-bit pictures (PNG/TIFF with 16 bits per channel: 65 536 steps), and ANY picture imported
//     with the RGB / RGBA mappings, use the FLOAT path (FromPixels): the decoder hands over the
//     straight (non-premultiplied) channels — ushort for 16-bit, byte for 8-bit — and our own
//     separable resampler (area average when shrinking, linear interpolation when enlarging, an
//     exact copy at 1:1) works in 32-bit floats, so no precision is lost on the way. 16-bit files
//     are decoded WITHOUT colour management, so the stored numbers arrive unchanged (data pictures
//     such as our own exports round-trip exactly); 8-bit ones are colour-managed to sRGB as before.
//   * Alpha: the classic mappings and RGBA composite the picture over black (colour × alpha), which
//     is what «transparent = silence» means; for RGBA the noise channel (blue) is averaged weighted
//     by alpha (Σ B·A / Σ A), so a half-transparent edge does not dilute a pixel's noisiness. The
//     RGB mapping ignores alpha completely (every pixel is opaque).
//
// EXPORT: 8-bit PNG through SkiaSharp, 16-bit PNG through a small built-in PNG writer (WritePng16:
// IHDR + one zlib IDAT + IEND, colour type 6 = RGBA, 16 bits big-endian per channel). In both, red
// = left, green = right, blue = the noise layer when the picture has one (re-import with «RGB: …»)
// or min(left, right) when it has none (the classic grey look; re-import with «Red → left…»), alpha
// = fully opaque.

using SkiaSharp;

namespace CsoundVisual.Paint;

/// <summary>
/// How a picture of any size and shape is fitted to the canvas when it is imported.
/// </summary>
/// <remarks>
/// The canvas always has <c>rows</c> = the pitch axis's row count (pitch resolution), so the
/// picture's HEIGHT is always scaled to the rows. What changes is how its WIDTH becomes time.
/// Stored in the Paint settings as an int: do not reorder.
/// </remarks>
public enum ImportFit
{
    /// <summary>
    /// Keep the picture's proportions: the number of columns follows its aspect ratio
    /// (columns = rows × width / height, clamped to the allowed range) and the duration stays the
    /// one chosen. A wide panorama gets many columns, a portrait photo few. Default.
    /// </summary>
    KeepProportions = 0,

    /// <summary>Stretch the picture to the current canvas size (it may look squashed or stretched).</summary>
    Stretch = 1,

    /// <summary>
    /// Keep the proportions INSIDE the current canvas size, centred, with black (silent) bars
    /// around it: nothing is distorted and the canvas size does not change.
    /// </summary>
    FitInside = 2,

    /// <summary>
    /// FULL horizontal detail: one column per picture pixel (up to <see cref="SpectralImage.MaxWidth"/>),
    /// whatever the rows and the duration. The duration then only decides how fast those columns
    /// are played. The caller passes the picture's width as the column count (see the import dialog);
    /// the picture is stretched to it, which for one column per pixel is no stretch at all.
    /// Appended last so saved settings keep their meaning.
    /// </summary>
    FullDetail = 3,
}

/// <summary>A decoded picture plus what the import dialog shows about it.</summary>
/// <param name="Bitmap">The pixels (RGBA, premultiplied, 8 bits). The caller owns and disposes it.</param>
/// <param name="OriginalWidth">Width of the file's picture in pixels (before any decode-time reduction).</param>
/// <param name="OriginalHeight">Height of the file's picture in pixels.</param>
/// <param name="Format">Short format name for the user ("JPEG", "PNG", "HEIF"…).</param>
public sealed record DecodedPicture(SKBitmap Bitmap, int OriginalWidth, int OriginalHeight, string Format) : IDisposable
{
    /// <summary>Bits per channel of the decoded data: 16 for 16-bit PNG/TIFF (65 536 levels), otherwise 8.</summary>
    public int BitsPerChannel { get; init; } = 8;

    /// <summary>True when at least one pixel is not fully opaque (the alpha channel carries information).</summary>
    public bool HasAlpha { get; init; }

    /// <summary>
    /// Straight (non-premultiplied) RGBA with 16 bits per channel, row-major and top-down
    /// (index = (y·Width + x)·4 + channel), same size as <see cref="Bitmap"/>; set for 16-bit pictures.
    /// </summary>
    public ushort[]? Straight16 { get; init; }

    /// <summary>Straight RGBA with 8 bits per channel, same layout; set for 8-bit pictures (used by the float path).</summary>
    public byte[]? Straight8 { get; init; }

    /// <summary>Short description of the depth for the import dialog: «16-bit» / «8-bit», plus «+ alpha».</summary>
    public string DepthText => (BitsPerChannel >= 16 ? "16-bit" : "8-bit") + (HasAlpha ? " + alpha" : "");

    /// <inheritdoc/>
    public void Dispose() => Bitmap.Dispose();
}

/// <summary>Picture import/export for Paint Rooms (see the file header).</summary>
public static class ImageCodec
{
    /// <summary>
    /// File extensions offered by the import picker and accepted by drag-and-drop (lower case,
    /// with the dot). PNG/JPEG/BMP/GIF/TIFF/ICO/JPEG XR are decoded by Windows itself; WebP,
    /// HEIF/HEIC and AVIF need the free codec extensions from the Microsoft Store (usually present
    /// on Windows 11); WebP also decodes through SkiaSharp as a fallback.
    /// </summary>
    public static readonly string[] Extensions =
        [".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".ico", ".jxr", ".wdp"];

    /// <summary>
    /// Pictures larger than this on either side are reduced WHILE decoding (keeping proportions):
    /// a canvas never needs more (its maximum width is <see cref="SpectralImage.MaxWidth"/>), and a
    /// 50-megapixel photo would otherwise cost hundreds of MB just to be scaled down again.
    /// </summary>
    const int MaxDecodeSide = 4096;

    /// <summary>
    /// Decodes a picture of any size. First with the Windows Imaging Component (WinRT
    /// <c>BitmapDecoder</c>): it knows the most formats, applies the EXIF rotation of phone photos
    /// (so a portrait photo is not imported lying on its side) and converts to sRGB. If Windows
    /// cannot decode it, SkiaSharp tries.
    /// </summary>
    /// <param name="path">Picture file.</param>
    /// <returns>The picture; dispose it when done.</returns>
    /// <exception cref="InvalidDataException">Neither decoder could read the file (message explains why).</exception>
    /// <remarks>Blocking (WinRT async calls are waited on): call it from a background thread.</remarks>
    public static DecodedPicture Decode(string path)
    {
        string? windowsError = null;
        try
        {
            // The WinRT imaging API exists on every Windows 10/11 this app supports (its target is
            // Windows 11 24H2); the explicit check satisfies the platform-compatibility analyser.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
                return DecodeWithWindows(path);
            windowsError = "Windows imaging is not available";
        }
        catch (Exception ex)
        {
            windowsError = ex.Message;
        }
        // Fallback: SkiaSharp (no EXIF rotation, but reads e.g. WebP without the Store codec).
        using var codec = SKCodec.Create(path);
        var bitmap = codec is null ? null : SKBitmap.Decode(codec);
        if (codec is null || bitmap is null)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string hint = ext is ".heic" or ".heif" or ".avif"
                ? " Install «HEIF Image Extensions» / «AV1 Video Extension» from the Microsoft Store."
                : "";
            throw new InvalidDataException("The picture could not be decoded (" + (windowsError ?? "unknown format") + ")." + hint);
        }
        // Straight 8-bit copy for the float path (converted by Skia from whatever it decoded).
        using var straight = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.CopyTo(straight, SKColorType.Rgba8888);
        byte[] s8 = straight.GetPixelSpan().ToArray();
        return new DecodedPicture(bitmap, bitmap.Width, bitmap.Height, codec.EncodedFormat.ToString().ToUpperInvariant())
        {
            Straight8 = s8,
            HasAlpha = AnyTransparent8(s8),
        };
    }

    /// <summary>True when some pixel of straight RGBA8 data has alpha &lt; 255.</summary>
    /// <param name="px">Straight RGBA bytes.</param>
    static bool AnyTransparent8(byte[] px)
    {
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 255) return true;
        return false;
    }

    /// <summary>True when some pixel of straight RGBA16 data has alpha &lt; 65535.</summary>
    /// <param name="px">Straight RGBA ushorts.</param>
    static bool AnyTransparent16(ushort[] px)
    {
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 65535) return true;
        return false;
    }

    /// <summary>
    /// Windows Imaging Component path of <see cref="Decode"/>: decodes STRAIGHT (non-premultiplied)
    /// RGBA — 16 bits per channel when the file stores 16 (Rgba16 / Gray16 sources), else 8 —
    /// respecting EXIF orientation, reduced to at most <see cref="MaxDecodeSide"/> pixels per side
    /// with Fant (area-averaging) interpolation; then builds the 8-bit premultiplied preview bitmap.
    /// </summary>
    /// <remarks>
    /// Colour management: 8-bit pictures are converted to sRGB (photos look right); 16-bit pictures
    /// are NOT colour-managed, so their numbers arrive exactly as stored (data pictures, our own
    /// 16-bit exports). Premultiplication of the preview: c·a/255 rounded to nearest.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240.0")]
    static DecodedPicture DecodeWithWindows(string path)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask().GetAwaiter().GetResult();
        using var stream = file.OpenReadAsync().AsTask().GetAwaiter().GetResult();
        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        // OrientedPixelWidth/Height already account for the EXIF rotation (a 90° photo swaps them).
        int w = (int)decoder.OrientedPixelWidth, h = (int)decoder.OrientedPixelHeight;
        var transform = new Windows.Graphics.Imaging.BitmapTransform();
        double reduce = Math.Min(1.0, (double)MaxDecodeSide / Math.Max(w, h));
        if (reduce < 1.0)
        {
            // The transform's scaled size is expressed BEFORE orientation is applied, i.e. in the
            // file's stored (un-rotated) pixel grid, hence PixelWidth/PixelHeight here.
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * reduce));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * reduce));
            transform.InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant;
        }
        // 16 bits per channel in the file? (WIC reports the native format of the frame.)
        bool deep = decoder.BitmapPixelFormat is Windows.Graphics.Imaging.BitmapPixelFormat.Rgba16 or Windows.Graphics.Imaging.BitmapPixelFormat.Gray16;
        using var soft = decoder.GetSoftwareBitmapAsync(
            deep ? Windows.Graphics.Imaging.BitmapPixelFormat.Rgba16 : Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
            deep ? Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage : Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb).AsTask().GetAwaiter().GetResult();
        int bw = soft.PixelWidth, bh = soft.PixelHeight;
        var bitmap = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul));
        var preview = new byte[4 * bw * bh];
        ushort[]? s16 = null;
        byte[]? s8 = null;
        if (deep)
        {
            // Rgba16: 8 bytes per pixel, little-endian ushorts R, G, B, A (straight).
            var raw = new byte[8 * bw * bh];
            soft.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(raw));
            s16 = new ushort[4 * bw * bh];
            Buffer.BlockCopy(raw, 0, s16, 0, raw.Length);
            for (int i = 0; i < preview.Length; i += 4)
            {
                // 16 → 8 bits by rounding (v·255/65535), then premultiply for the preview bitmap.
                int a = (s16[i + 3] * 255 + 32767) / 65535;
                for (int c = 0; c < 3; c++) preview[i + c] = (byte)(((s16[i + c] * 255 + 32767) / 65535 * a + 127) / 255);
                preview[i + 3] = (byte)a;
            }
        }
        else
        {
            s8 = new byte[4 * bw * bh];
            soft.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(s8));
            for (int i = 0; i < preview.Length; i += 4)
            {
                int a = s8[i + 3];
                for (int c = 0; c < 3; c++) preview[i + c] = (byte)((s8[i + c] * a + 127) / 255);
                preview[i + 3] = (byte)a;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(preview, 0, bitmap.GetPixels(), preview.Length);
        string format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
        {
            "JPG" or "JPEG" or "JFIF" => "JPEG",
            "TIF" => "TIFF",
            "HEIC" => "HEIF",
            "WDP" => "JPEG XR",
            var other => other,
        };
        return new DecodedPicture(bitmap, w, h, format)
        {
            BitsPerChannel = deep ? 16 : 8,
            Straight16 = s16,
            Straight8 = s8,
            HasAlpha = s16 is not null ? AnyTransparent16(s16) : AnyTransparent8(s8!),
        };
    }

    /// <summary>
    /// <c>CsoundVisual.exe --picture-test file1 [file2 …]</c>: decodes each picture and prints its
    /// size, format, decode time and the canvas each <see cref="ImportFit"/> produces on a
    /// 512-row logarithmic axis (columns, rows, share of lit cells). No window, no sound.
    /// </summary>
    /// <param name="files">Picture files to try.</param>
    /// <returns>0 when every file decoded and every fit produced a canvas, 1 otherwise.</returns>
    public static int SelfTest(string[] files)
    {
        var axis = PitchAxis.Logarithmic(40, 12000, 512);
        int failures = 0;
        foreach (var f in files)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var pic = Decode(f);
                Console.WriteLine($"{Path.GetFileName(f)}: {pic.OriginalWidth}×{pic.OriginalHeight} {pic.Format} {pic.DepthText}, decoded {pic.Bitmap.Width}×{pic.Bitmap.Height} in {sw.ElapsedMilliseconds} ms");
                foreach (var fit in Enum.GetValues<ImportFit>())
                {
                    var img = FromPicture(pic, 1024, axis, 12, ColorMapping.Brightness, fit);
                    int lit = 0;
                    for (int i = 0; i < img.Left.Length; i++) if (img.Left[i] > 0.05f) lit++;
                    Console.WriteLine($"   {fit,-16} → {img.Width} columns × {img.Height} rows, {100.0 * lit / img.Left.Length:0.0} % lit");
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"{Path.GetFileName(f)}: FAIL {ex.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Columns that keep a picture's proportions on a canvas of <paramref name="rows"/> rows
    /// (one canvas cell is treated as square): rows × width / height, clamped to
    /// [<see cref="SpectralImage.MinWidth"/>, <see cref="SpectralImage.MaxWidth"/>] and to at least 16.
    /// </summary>
    /// <param name="pictureWidth">Picture width in pixels.</param>
    /// <param name="pictureHeight">Picture height in pixels (0 is treated as 1).</param>
    /// <param name="rows">Canvas rows (the axis row count).</param>
    /// <returns>Canvas columns.</returns>
    public static int ColumnsFor(int pictureWidth, int pictureHeight, int rows) =>
        Math.Clamp((int)Math.Round((double)rows * pictureWidth / Math.Max(1, pictureHeight)), Math.Max(16, SpectralImage.MinWidth), SpectralImage.MaxWidth);

    /// <summary>
    /// Builds the canvas image for an imported picture according to <paramref name="fit"/>:
    /// <see cref="ImportFit.KeepProportions"/> uses <see cref="ColumnsFor"/> columns,
    /// <see cref="ImportFit.Stretch"/> and <see cref="ImportFit.FitInside"/> use
    /// <paramref name="canvasWidth"/>.
    /// </summary>
    /// <param name="picture">The decoded picture.</param>
    /// <param name="canvasWidth">Current canvas columns (used by Stretch and FitInside).</param>
    /// <param name="axis">Pitch axis (its row count is the picture's height on the canvas).</param>
    /// <param name="duration">Duration of the result in seconds.</param>
    /// <param name="mapping">Colour → level mapping.</param>
    /// <param name="fit">How the picture's shape is fitted.</param>
    /// <returns>A new image (the picture is not disposed).</returns>
    /// <remarks>
    /// 8-bit pictures with a classic mapping take the original SkiaSharp path (<see cref="FromBitmap"/>);
    /// 16-bit pictures and the RGB / RGBA mappings take the float path (<see cref="FromPixels"/>).
    /// </remarks>
    public static SpectralImage FromPicture(DecodedPicture picture, int canvasWidth, PitchAxis axis, double duration, ColorMapping mapping, ImportFit fit)
    {
        int width = fit == ImportFit.KeepProportions ? ColumnsFor(picture.Bitmap.Width, picture.Bitmap.Height, axis.RowCount) : canvasWidth;
        bool floatPath = picture.BitsPerChannel > 8 || mapping is ColorMapping.RgbNoise or ColorMapping.RgbaNoiseMask;
        if (floatPath && (picture.Straight16 is not null || picture.Straight8 is not null))
            return FromPixels(picture, width, axis, duration, mapping, fit == ImportFit.FitInside);
        return FromBitmap(picture.Bitmap, width, axis, duration, mapping, fit == ImportFit.FitInside);
    }

    /// <summary>
    /// The float import path (see the file header): straight 8- or 16-bit RGBA → 32-bit float
    /// resampling to the canvas grid → levels (and the noise layer for the RGB mappings).
    /// </summary>
    /// <param name="picture">Decoded picture with <see cref="DecodedPicture.Straight16"/> or <see cref="DecodedPicture.Straight8"/>.</param>
    /// <param name="width">Columns of the result.</param>
    /// <param name="axis">Pitch axis (its row count is the picture's height on the canvas).</param>
    /// <param name="duration">Duration in seconds.</param>
    /// <param name="mapping">Colour mapping.</param>
    /// <param name="keepProportions">True: scale uniformly to fit inside (centred, silent bars, rounded to whole cells).</param>
    /// <returns>A new image; it has a noise layer only for <see cref="ColorMapping.RgbNoise"/> / <see cref="ColorMapping.RgbaNoiseMask"/>.</returns>
    /// <remarks>
    /// Channels carried through the resampler (4 float planes):
    /// <list type="bullet">
    /// <item>RgbNoise: straight R, G, B (alpha ignored) — left = R, right = G, noise = B.</item>
    /// <item>everything else: premultiplied R·A, G·A, B·A and A. Classic mappings read the
    /// premultiplied colour (= the picture over black); RgbaNoiseMask: left = R·A, right = G·A,
    /// noise = Σ(B·A)/Σ(A) (alpha-weighted mean of the straight blue).</item>
    /// </list>
    /// Separable: first the vertical pass (picture rows → canvas rows, into w × rows planes), then the
    /// horizontal pass (picture columns → canvas columns). At 1:1 both passes are exact copies, so a
    /// 16-bit picture of exactly the canvas size imports with full 16-bit precision.
    /// </remarks>
    public static SpectralImage FromPixels(DecodedPicture picture, int width, PitchAxis axis, double duration, ColorMapping mapping, bool keepProportions = false)
    {
        var img = new SpectralImage(width, axis, duration);
        int W = img.Width, H = img.Height;
        int w = picture.Bitmap.Width, h = picture.Bitmap.Height;
        var p16 = picture.Straight16;
        var p8 = picture.Straight8;
        bool straightRgb = mapping == ColorMapping.RgbNoise;
        bool noiseLayer = mapping is ColorMapping.RgbNoise or ColorMapping.RgbaNoiseMask;
        // Destination rectangle on the canvas grid (whole cells): everything, or the centred fit.
        int dw = W, dh = H, dx0 = 0, dy0 = 0;
        if (keepProportions)
        {
            double scale = Math.Min((double)W / w, (double)H / h);
            dw = Math.Clamp((int)Math.Round(w * scale), 1, W);
            dh = Math.Clamp((int)Math.Round(h * scale), 1, H);
            dx0 = (W - dw) / 2;
            dy0 = (H - dh) / 2;
        }
        // Reads source pixel (x, y) as the 4 carried channels (see remarks), each 0…1.
        void Read(int x, int y, Span<float> ch)
        {
            int o = (y * w + x) * 4;
            float r, g, b, a;
            if (p16 is not null) { r = p16[o] / 65535f; g = p16[o + 1] / 65535f; b = p16[o + 2] / 65535f; a = p16[o + 3] / 65535f; }
            else { r = p8![o] / 255f; g = p8[o + 1] / 255f; b = p8[o + 2] / 255f; a = p8[o + 3] / 255f; }
            if (straightRgb) { ch[0] = r; ch[1] = g; ch[2] = b; ch[3] = 1; }
            else { ch[0] = r * a; ch[1] = g * a; ch[2] = b * a; ch[3] = a; }
        }
        // 1) Vertical pass: picture rows (top-down) → dh destination rows, for every picture column.
        var wy = ResampleWeights(h, dh);
        var planes = new float[4][];
        for (int c = 0; c < 4; c++) planes[c] = new float[w * dh];
        Span<float> px = stackalloc float[4];
        for (int ty = 0; ty < dh; ty++)
            foreach (var (sy, wt) in wy[ty])
                for (int x = 0; x < w; x++)
                {
                    Read(x, sy, px);
                    int o = ty * w + x;
                    for (int c = 0; c < 4; c++) planes[c][o] += wt * px[c];
                }
        // 2) Horizontal pass + colour mapping into the canvas (picture top row = highest pitch row).
        var wx = ResampleWeights(w, dw);
        var nz = noiseLayer ? img.EnsureNoise() : null;
        for (int ty = 0; ty < dh; ty++)
        {
            int row = H - 1 - (dy0 + ty);
            for (int tx = 0; tx < dw; tx++)
            {
                float c0 = 0, c1 = 0, c2 = 0, c3 = 0;
                foreach (var (sx, wt) in wx[tx])
                {
                    int o = ty * w + sx;
                    c0 += wt * planes[0][o]; c1 += wt * planes[1][o]; c2 += wt * planes[2][o]; c3 += wt * planes[3][o];
                }
                int i = (dx0 + tx) * H + row;
                switch (mapping)
                {
                    case ColorMapping.RgbNoise:
                        img.Left[i] = Math.Clamp(c0, 0f, 1f); img.Right[i] = Math.Clamp(c1, 0f, 1f); nz![i] = Math.Clamp(c2, 0f, 1f);
                        break;
                    case ColorMapping.RgbaNoiseMask:
                        img.Left[i] = Math.Clamp(c0, 0f, 1f); img.Right[i] = Math.Clamp(c1, 0f, 1f);
                        // Alpha-weighted mean of the straight blue; fully transparent → tonal (it is silent anyway).
                        nz![i] = c3 > 1e-7f ? Math.Clamp(c2 / c3, 0f, 1f) : 0f;
                        break;
                    default:
                        var (l, r) = MapColor(Math.Clamp(c0, 0f, 1f), Math.Clamp(c1, 0f, 1f), Math.Clamp(c2, 0f, 1f), mapping);
                        img.Left[i] = l; img.Right[i] = r;
                        break;
                }
            }
        }
        return img;
    }

    /// <summary>
    /// 1-D resampling weights from <paramref name="srcLen"/> samples to <paramref name="dstLen"/>:
    /// for each destination index, the (source index, weight) pairs that sum to 1.
    /// </summary>
    /// <param name="srcLen">Source length (≥ 1).</param>
    /// <param name="dstLen">Destination length (≥ 1).</param>
    /// <returns>One list per destination index.</returns>
    /// <remarks>
    /// Shrinking (srcLen &gt; dstLen): an area (box) average over the source span [i·s, (i+1)·s) with
    /// fractional end weights, s = srcLen/dstLen — every source sample counts exactly once in total.
    /// Same length or enlarging: linear interpolation at the destination centre mapped into the
    /// source, (i + 0.5)·s − 0.5, clamped at the ends — at 1:1 this is exactly (i, 1): a lossless copy.
    /// </remarks>
    static List<(int Index, float Weight)>[] ResampleWeights(int srcLen, int dstLen)
    {
        var result = new List<(int, float)>[dstLen];
        double s = (double)srcLen / dstLen;
        for (int i = 0; i < dstLen; i++)
        {
            var list = new List<(int, float)>(4);
            if (s > 1.0000001)
            {
                double a = i * s, b = (i + 1) * s;
                for (int k = (int)Math.Floor(a); k < Math.Min(srcLen, (int)Math.Ceiling(b)); k++)
                {
                    double overlap = Math.Min(b, k + 1) - Math.Max(a, k);
                    if (overlap > 0) list.Add((k, (float)(overlap / s)));
                }
            }
            else
            {
                double c = Math.Clamp((i + 0.5) * s - 0.5, 0, srcLen - 1);
                int i0 = (int)Math.Floor(c), i1 = Math.Min(i0 + 1, srcLen - 1);
                float t = (float)(c - i0);
                list.Add((i0, 1 - t));
                if (t > 0 && i1 != i0) list.Add((i1, t));
            }
            result[i] = list;
        }
        return result;
    }

    /// <summary>
    /// Decodes <paramref name="path"/> and returns a new image of <paramref name="width"/> columns on
    /// <paramref name="axis"/>, with the colours mapped by <paramref name="mapping"/>.
    /// </summary>
    /// <param name="path">Picture file.</param>
    /// <param name="width">Columns of the result (the picture is stretched to it).</param>
    /// <param name="axis">Pitch axis of the result (the picture is stretched to its row count).</param>
    /// <param name="duration">Duration of the result (seconds).</param>
    /// <param name="mapping">Colour → level mapping.</param>
    /// <returns>A new image.</returns>
    /// <exception cref="InvalidDataException">The file could not be decoded.</exception>
    public static SpectralImage Import(string path, int width, PitchAxis axis, double duration, ColorMapping mapping)
    {
        using var decoded = Decode(path);
        // Stretch = the whole picture onto width × rows; FromPicture picks the 8-bit or the float path.
        return FromPicture(decoded, width, axis, duration, mapping, ImportFit.Stretch);
    }

    /// <summary>Same as <see cref="Import"/> for an already decoded bitmap (tests, clipboard, presets).</summary>
    /// <param name="source">The picture.</param>
    /// <param name="width">Columns of the result.</param>
    /// <param name="axis">Pitch axis of the result.</param>
    /// <param name="duration">Duration of the result (seconds).</param>
    /// <param name="mapping">Colour → level mapping.</param>
    /// <param name="keepProportions">False: stretch the picture over the whole canvas. True: scale it
    /// uniformly to fit inside, centred, leaving black (silent) bars.</param>
    /// <returns>A new image.</returns>
    public static SpectralImage FromBitmap(SKBitmap source, int width, PitchAxis axis, double duration, ColorMapping mapping, bool keepProportions = false)
    {
        var img = new SpectralImage(width, axis, duration);
        int h = img.Height;
        // Draw the picture scaled to exactly width × height pixels into a known 32-bit RGBA,
        // PREMULTIPLIED bitmap over black (a canvas needs premultiplied pixels; premultiplied
        // values are exactly «colour × alpha», i.e. transparent parts count as black, which is
        // what we want). Same size: nearest sampling = an exact copy. Otherwise bilinear with
        // mipmaps, which averages properly when a big photo is reduced to the canvas.
        var info = new SKImageInfo(img.Width, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var scaled = new SKBitmap(info);
        using (var canvas = new SKCanvas(scaled))
        using (var skImage = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.Black);
            var sampling = source.Width == img.Width && source.Height == h
                ? new SKSamplingOptions(SKFilterMode.Nearest)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            var dest = new SKRect(0, 0, img.Width, h);
            if (keepProportions)
            {
                // Uniform scale that fits both directions, centred; the rest stays black = silence.
                float scale = Math.Min((float)img.Width / source.Width, (float)h / source.Height);
                float dw = source.Width * scale, dh = source.Height * scale;
                dest = SKRect.Create((img.Width - dw) / 2, (h - dh) / 2, dw, dh);
            }
            canvas.DrawImage(skImage, dest, sampling);
        }
        // Bitmap rows are top-down and row-major (4 bytes R, G, B, A per pixel); the image is
        // column-major and bottom-up, so each pixel is transposed into place.
        var px = scaled.GetPixelSpan();
        for (int py = 0; py < h; py++)
        {
            int row = h - 1 - py; // top of the picture = highest pitch
            for (int x = 0; x < img.Width; x++)
            {
                int o = (py * img.Width + x) * 4;
                float r = px[o] / 255f, g = px[o + 1] / 255f, b = px[o + 2] / 255f;
                var (l, rr) = MapColor(r, g, b, mapping);
                int i = x * h + row;
                img.Left[i] = l;
                img.Right[i] = rr;
            }
        }
        return img;
    }

    /// <summary>
    /// Colour (each 0…1) → (left, right) levels.
    /// </summary>
    /// <remarks>
    /// BrightnessHuePan: level = max(r, g, b) (the HSV value); the hue angle h gives a pan
    /// p = sin(h − 60°)/sin 60°, clamped to ±1, which is −1 for red and magenta (left), +1 for green
    /// and cyan (right) and 0 for yellow and blue (centre); p is scaled by the saturation so greys
    /// stay centred. Balance law: the side away from the pan keeps full level up to the centre and
    /// fades linearly to 0 at the opposite extreme.
    /// </remarks>
    /// <param name="r">Red, 0…1 (gamma-encoded sRGB).</param>
    /// <param name="g">Green, 0…1.</param>
    /// <param name="b">Blue, 0…1.</param>
    /// <param name="mapping">The import rule chosen by the user.</param>
    /// <returns>Left and right levels, each 0…1.</returns>
    public static (float Left, float Right) MapColor(float r, float g, float b, ColorMapping mapping)
    {
        switch (mapping)
        {
            case ColorMapping.RedGreen:
            case ColorMapping.RgbNoise:        // (the noise channel is handled by FromPixels)
            case ColorMapping.RgbaNoiseMask:
                return (r, g);
            case ColorMapping.BrightnessHuePan:
            {
                float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
                if (max <= 0) return (0, 0);
                float sat = (max - min) / max;
                float pan = 0;
                if (sat > 1e-4f)
                {
                    // Standard RGB → hue angle in degrees.
                    float d = max - min, hue;
                    if (max == r) hue = 60 * (((g - b) / d) % 6);
                    else if (max == g) hue = 60 * ((b - r) / d + 2);
                    else hue = 60 * ((r - g) / d + 4);
                    pan = Math.Clamp(MathF.Sin((hue - 60) * MathF.PI / 180) / MathF.Sin(MathF.PI / 3), -1f, 1f) * sat;
                }
                return (max * MathF.Min(1, 1 - pan), max * MathF.Min(1, 1 + pan));
            }
            default:
                // Rec. 709 luma of the (gamma-encoded) values: what the eye reads as brightness.
                float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                return (y, y);
        }
    }

    /// <summary>
    /// Writes <paramref name="image"/> as a PNG of Width × Height pixels (red = left, green =
    /// right, blue = noise layer or min(left, right); top row = highest pitch), 8 or 16 bits per channel.
    /// </summary>
    /// <param name="image">The image to export.</param>
    /// <param name="path">Target .png file (overwritten).</param>
    /// <param name="sixteenBit">True: 16 bits per channel (levels quantised to 1/65535, a lossless
    /// round trip for practical purposes); false: 8 bits (1/255 steps).</param>
    public static void ExportPng(SpectralImage image, string path, bool sixteenBit)
    {
        if (sixteenBit) ExportPng16(image, path);
        else ExportPng(image, path);
    }

    /// <summary>
    /// Writes <paramref name="image"/> as an 8-bit PNG of Width × Height pixels (red = left, green =
    /// right, blue = noise layer or min(left, right); top row = highest pitch).
    /// </summary>
    /// <param name="image">The image to export (8 bits per level: levels are quantised to 1/255).</param>
    /// <param name="path">Target .png file (overwritten).</param>
    public static void ExportPng(SpectralImage image, string path)
    {
        using var bmp = ToBitmap(image);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    /// <summary>The image as an RGBA bitmap in the export convention (caller disposes it).</summary>
    /// <param name="image">The image.</param>
    /// <returns>A Width × Height opaque, unpremultiplied RGBA8888 bitmap.</returns>
    public static SKBitmap ToBitmap(SpectralImage image)
    {
        int w = image.Width, h = image.Height;
        var nz = image.Noise;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var px = bmp.GetPixelSpan();
        for (int py = 0; py < h; py++)
        {
            int row = h - 1 - py;
            for (int x = 0; x < w; x++)
            {
                int i = x * h + row, o = (py * w + x) * 4;
                byte l = (byte)Math.Round(Math.Clamp(image.Left[i], 0f, 1f) * 255);
                byte r = (byte)Math.Round(Math.Clamp(image.Right[i], 0f, 1f) * 255);
                px[o] = l;
                px[o + 1] = r;
                // Blue: the noise layer when there is one (re-import with «RGB: … B = noise»),
                // otherwise min(L, R) so a mono picture exports as plain grey.
                px[o + 2] = nz is not null ? (byte)Math.Round(Math.Clamp(nz[i], 0f, 1f) * 255) : Math.Min(l, r);
                px[o + 3] = 255;
            }
        }
        return bmp;
    }

    /// <summary>
    /// Writes <paramref name="image"/> as a 16-bit-per-channel RGBA PNG (same colour convention as
    /// <see cref="ToBitmap"/>, alpha = 65535). Levels are rounded to the nearest of 65 536 steps,
    /// so a round trip through <see cref="ColorMapping.RgbNoise"/> (or <see cref="ColorMapping.RedGreen"/>
    /// for pictures without noise) changes no level by more than 1/131070 ≈ 7.6·10⁻⁶.
    /// </summary>
    /// <param name="image">The image.</param>
    /// <param name="path">Target .png file (overwritten).</param>
    public static void ExportPng16(SpectralImage image, string path)
    {
        int w = image.Width, h = image.Height;
        var nz = image.Noise;
        var rgba = new ushort[w * h * 4];
        static ushort Q(float v) => (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535);
        for (int py = 0; py < h; py++)
        {
            int row = h - 1 - py;   // picture top = highest pitch
            for (int x = 0; x < w; x++)
            {
                int i = x * h + row, o = (py * w + x) * 4;
                ushort l = Q(image.Left[i]), r = Q(image.Right[i]);
                rgba[o] = l;
                rgba[o + 1] = r;
                rgba[o + 2] = nz is not null ? Q(nz[i]) : Math.Min(l, r);
                rgba[o + 3] = 65535;
            }
        }
        WritePng16(path, w, h, rgba);
    }

    /// <summary>
    /// Minimal PNG writer for 16-bit RGBA (colour type 6, bit depth 16, no interlace): signature,
    /// IHDR, one zlib-compressed IDAT (every scanline with filter type 0 «None»), IEND.
    /// </summary>
    /// <param name="path">Target file (overwritten).</param>
    /// <param name="width">Pixels per row.</param>
    /// <param name="height">Rows.</param>
    /// <param name="rgba">Straight RGBA samples, row-major top-down, 4 per pixel.</param>
    /// <remarks>
    /// PNG stores 16-bit samples BIG-endian. Each chunk = length (4 bytes, big-endian), type (4 ASCII
    /// bytes), data, CRC-32 of type + data (the standard reflected polynomial 0xEDB88320). Used for
    /// the 16-bit export and by the tests to generate 16-bit RGBA test pictures.
    /// </remarks>
    public static void WritePng16(string path, int width, int height, ushort[] rgba)
    {
        // Raw scanlines: filter byte 0, then width × 4 samples × 2 bytes.
        int stride = 1 + width * 8;
        var raw = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * stride;
            raw[o] = 0;
            for (int k = 0; k < width * 4; k++)
            {
                ushort v = rgba[y * width * 4 + k];
                raw[o + 1 + 2 * k] = (byte)(v >> 8);
                raw[o + 2 + 2 * k] = (byte)v;
            }
        }
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw);
            idat = ms.ToArray();
        }
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 16;   // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        ihdr[10] = 0;   // compression: deflate
        ihdr[11] = 0;   // filter method 0
        ihdr[12] = 0;   // no interlace
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", idat);
        WriteChunk(fs, "IEND", []);
    }

    /// <summary>Writes one PNG chunk (length, type, data, CRC-32).</summary>
    /// <param name="s">Output stream.</param>
    /// <param name="type">Four-letter chunk type.</param>
    /// <param name="data">Chunk data.</param>
    static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        Span<byte> c = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    /// <summary>Lazily built CRC-32 lookup table (reflected polynomial 0xEDB88320).</summary>
    static uint[]? crcTable;

    /// <summary>Continues a CRC-32 over <paramref name="data"/> (start with 0xFFFFFFFF, finish with ^ 0xFFFFFFFF).</summary>
    /// <param name="crc">Running value.</param>
    /// <param name="data">Bytes to add.</param>
    /// <returns>The updated running value.</returns>
    static uint Crc32(uint crc, byte[] data)
    {
        var table = crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
