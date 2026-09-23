using System.Collections.Concurrent;
using Svg;

namespace MarkForge.Editor;

/// <summary>
/// Rasterizes the embedded Bootstrap Icons SVGs (MIT licensed, bundled offline under
/// Assets/Icons) into Bitmaps for toolbar and menu items. Icons are recolored at render
/// time via their <c>currentColor</c> fill so a single SVG set works for any accent color,
/// and results are cached by (name, size, color) since the same icon is reused in both
/// the toolbar and the menu at different sizes.
/// </summary>
internal static class IconProvider
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();

    public static Image Get(string name, int size, Color color)
    {
        string key = $"{name}|{size}|{color.ToArgb():X8}";
        return Cache.GetOrAdd(key, _ => Render(name, size, color));
    }

    private static Bitmap Render(string name, int size, Color color)
    {
        string svgText = LoadEmbeddedSvg(name);
        string hex = ColorTranslator.ToHtml(color);
        svgText = svgText.Replace("currentColor", hex, StringComparison.OrdinalIgnoreCase);

        var document = SvgDocument.FromSvg<SvgDocument>(svgText);
        return document.Draw(size, size);
    }

    private static string LoadEmbeddedSvg(string name)
    {
        var assembly = typeof(IconProvider).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Assets.Icons.{name}.svg";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Icon not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
