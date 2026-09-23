using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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

    // Reverse lookup so a theme switch can re-render the icons already placed on menu and
    // toolbar items in the new icon color, without every call site having to remember them.
    private static readonly ConditionalWeakTable<Image, IconSpec> Specs = new();

    private sealed record IconSpec(string Name, int Size, Color Color);

    public static Image Get(string name, int size, Color color)
    {
        string key = $"{name}|{size}|{color.ToArgb():X8}";
        return Cache.GetOrAdd(key, _ =>
        {
            var bitmap = Render(name, size, color);
            Specs.AddOrUpdate(bitmap, new IconSpec(name, size, color));
            return bitmap;
        });
    }

    /// <summary>Re-renders every item image that was drawn in <paramref name="from"/> using
    /// <paramref name="to"/>; accent-colored icons (the task-list marks) are left alone.</summary>
    public static void Recolor(ToolStripItemCollection items, Color from, Color to)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Image is not null && Specs.TryGetValue(item.Image, out var spec) && spec.Color.ToArgb() == from.ToArgb())
                item.Image = Get(spec.Name, spec.Size, to);
            if (item is ToolStripDropDownItem dropDown)
                Recolor(dropDown.DropDownItems, from, to);
        }
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
