using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Psv.App;

public partial class AppearanceWindow : Window
{
    public AppearanceWindow()
    {
        InitializeComponent();
        FontFamilyCombo.ItemsSource = DocumentView.AvailableFontFamilies;
    }

    public bool Applied { get; private set; }

    public void LoadFrom(DocumentView view)
    {
        string familyName = view.FontFamily.Name;
        FontFamilyCombo.SelectedItem = familyName;
        FontFamilyCombo.Text = familyName;
        FontSizeUpDown.Value = (decimal)view.FontSize;

        var (text, zebraEven, zebraOdd) = view.GetEffectiveColors();
        TextColorPicker.Color = text;
        ZebraEvenColorPicker.Color = zebraEven;
        ZebraOddColorPicker.Color = zebraOdd;

        FollowSystemThemeCheckBox.IsChecked = view.FollowSystemTheme;
        UpdateColorPickersEnabled();
    }

    public void ApplyTo(DocumentView view)
    {
        string familyName = string.IsNullOrWhiteSpace(FontFamilyCombo.Text) ? "monospace" : FontFamilyCombo.Text.Trim();
        view.FontFamily = new FontFamily(familyName);
        view.FontSize = (double)(FontSizeUpDown.Value ?? 14);

        view.FollowSystemTheme = FollowSystemThemeCheckBox.IsChecked == true;
        view.TextColor = TextColorPicker.Color;
        view.ZebraEvenColor = ZebraEvenColorPicker.Color;
        view.ZebraOddColor = ZebraOddColorPicker.Color;
    }

    private void OnFollowSystemThemeClick(object? sender, RoutedEventArgs e) => UpdateColorPickersEnabled();

    private void UpdateColorPickersEnabled()
    {
        bool custom = FollowSystemThemeCheckBox.IsChecked != true;
        TextColorPicker.IsEnabled = custom;
        ZebraEvenColorPicker.IsEnabled = custom;
        ZebraOddColorPicker.IsEnabled = custom;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Applied = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
