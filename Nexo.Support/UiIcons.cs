using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Nexo.Support;

/// <summary>Local, resolution-independent icons for the desktop interface.</summary>
public static class UiIcons
{
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = new Dictionary<string, Geometry>(StringComparer.Ordinal)
    {
        ["brand"] = Geometry.Parse("M12,1 L14.7,9.3 23,12 14.7,14.7 12,23 9.3,14.7 1,12 9.3,9.3 Z"),
        ["chat"] = Geometry.Parse("M20,11.5 C20,16.2 16.4,20 11.5,20 C9.9,20 8.3,19.6 7,18.8 L3,20 4.2,16 C3.4,14.7 3,13.2 3,11.5 C3,6.8 6.7,3 11.5,3 C16.3,3 20,6.8 20,11.5 Z M7.5,11.5 L15.5,11.5"),
        ["health"] = Geometry.Parse("M20.5,8.7 C20.5,13.5 15.9,17.5 12,20.7 C8.1,17.5 3.5,13.5 3.5,8.7 C3.5,5.9 5.5,4 8,4 C9.8,4 11.2,4.9 12,6.3 C12.8,4.9 14.2,4 16,4 C18.5,4 20.5,5.9 20.5,8.7 Z M5,12 L9,12 10.5,9.5 13,14.5 14.5,12 19,12"),
        ["history"] = Geometry.Parse("M20.5,12 A8.5,8.5 0 1 1 18,6 M20.5,4 L20.5,9 15.5,9 M12,7 L12,12 15.5,14"),
        ["tickets"] = Geometry.Parse("M6,3 L15,3 19,7 19,21 6,21 Z M15,3 L15,7 19,7 M9,11 L16,11 M9,15 L16,15 M9,19 L13,19"),
        ["users"] = Geometry.Parse("M12,12 A4,4 0 1 0 12,4 A4,4 0 1 0 12,12 Z M4.5,20 C4.5,16.8 7.2,14.5 12,14.5 C16.8,14.5 19.5,16.8 19.5,20 M19,7.5 C20.8,8.1 21.5,9.4 21.5,11 M20,15.5 C21.4,16.2 22,17.5 22,19"),
        ["settings"] = Geometry.Parse("M4,6 L20,6 M4,12 L20,12 M4,18 L20,18 M9,3.5 L9,8.5 M16,9.5 L16,14.5 M8,15.5 L8,20.5"),
        ["audit"] = Geometry.Parse("M8,4 L16,4 M9,3 L9,5 15,5 15,3 M7,5 L5,5 5,21 19,21 19,5 17,5 M8.5,10 L15.5,10 M8.5,14 L15.5,14 M8.5,18 L13,18"),
        ["camera"] = Geometry.Parse("M4,7 L20,7 20,18 4,18 Z M12,15.5 A3,3 0 1 0 12,9.5 A3,3 0 1 0 12,15.5 Z M7,4.5 L10,4.5"),
        ["network"] = Geometry.Parse("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M3,12 L21,12 M12,3 C8.5,6 8.5,18 12,21 M12,3 C15.5,6 15.5,18 12,21"),
        ["audio"] = Geometry.Parse("M4,13 L4,10 7,10 11,6 11,17 7,13 Z M15,9 C16.5,10 16.5,13 15,14 M18,6 C21,9 21,15 18,18"),
        ["performance"] = Geometry.Parse("M4.6,17.5 A9,9 0 1 1 19.4,17.5 M12,12 L17,8 M9,18 L15,18 M12,12 A1,1 0 1 0 12,14 A1,1 0 1 0 12,12 Z"),
        ["bluetooth"] = Geometry.Parse("M12,2 L18,7 7,17 M12,22 L18,17 7,7 M12,2 L12,22"),
        ["printer"] = Geometry.Parse("M6,8 L6,3 18,3 18,8 M6,18 L4,18 4,9 20,9 20,18 18,18 M6,15 L18,15 18,21 6,21 Z M16,12 L17,12"),
        ["send"] = Geometry.Parse("M12,19 L12,5 M6,11 L12,5 18,11"),
        ["plus"] = Geometry.Parse("M12,5 L12,19 M5,12 L19,12"),
        ["check"] = Geometry.Parse("M4,12 L9.5,17.5 20,7"),
        ["arrow"] = Geometry.Parse("M5,12 L19,12 M13,6 L19,12 13,18"),
        ["shield"] = Geometry.Parse("M12,2 L20,5 20,11 C20,16 16.5,19.4 12,22 C7.5,19.4 4,16 4,11 L4,5 Z M9,12 L11,14 15,10")
    };

    public static Viewbox Create(string name, double size) => Create(name, size, (Brush)Application.Current.FindResource("Accent"));

    public static Viewbox Create(string name, double size, Brush stroke)
    {
        if (!Shapes.TryGetValue(name, out var shape)) throw new ArgumentOutOfRangeException(nameof(name), name, "Ícone desconhecido.");
        var path = new Path
        {
            Data = shape,
            Stroke = stroke,
            StrokeThickness = name == "brand" ? 0 : 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = name == "brand" ? stroke : Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Child = new Grid { Width = 24, Height = 24, Children = { path } }
        };
    }
}
