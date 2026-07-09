using Avalonia.Media;

namespace Psv.App;

/// <summary>Theme-driven color set shared by <see cref="DocumentView"/> and <see cref="HexView"/>.</summary>
internal readonly record struct ViewPalette(Color Text, Color ZebraEven, Color ZebraOdd);

internal static class ViewPalettes
{
    public static readonly ViewPalette Light = new(
        Text: Colors.Black,
        ZebraEven: Colors.White,
        ZebraOdd: Color.FromRgb(0xF0, 0xF0, 0xF0));

    public static readonly ViewPalette Dark = new(
        Text: Color.FromRgb(0xE0, 0xE0, 0xE0),
        ZebraEven: Color.FromRgb(0x1E, 0x1E, 0x1E),
        ZebraOdd: Color.FromRgb(0x25, 0x25, 0x26));

    private static readonly Color LightGutterBackground = Color.FromRgb(0xE8, 0xE8, 0xE8);
    private static readonly Color DarkGutterBackground = Color.FromRgb(0x2D, 0x2D, 0x2D);
    private static readonly Color GutterTextGray = Color.FromRgb(0x80, 0x80, 0x80);

    // Rec. 601 luma coefficients, used to pick a light or dark gutter to match the zebra base color.
    private const double LumaRed = 0.299;
    private const double LumaGreen = 0.587;
    private const double LumaBlue = 0.114;
    private const double GutterLuminanceThreshold = 0.5;

    public static (Color Background, Color Text) GetGutterColors(Color zebraEvenColor)
    {
        double luminance = ((LumaRed * zebraEvenColor.R) + (LumaGreen * zebraEvenColor.G) + (LumaBlue * zebraEvenColor.B)) / 255.0;
        Color background = luminance > GutterLuminanceThreshold ? LightGutterBackground : DarkGutterBackground;
        return (background, GutterTextGray);
    }
}
