using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Psv.App;

public partial class GoToLineWindow : Window
{
    public GoToLineWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LineNumberUpDown.Focus();
    }

    public long? ChosenLineNumber { get; private set; }

    public void SetLineRange(long currentLineOneBased, long maxLineOneBased)
    {
        long max = Math.Max(1, maxLineOneBased);
        RangeText.Text = $"Line number (1 - {max:N0})";
        LineNumberUpDown.Maximum = max;
        LineNumberUpDown.Minimum = 1;
        LineNumberUpDown.Value = Math.Clamp(currentLineOneBased, 1, max);
    }

    private void OnGoClick(object? sender, RoutedEventArgs e) => Confirm();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnLineNumberKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        if (LineNumberUpDown.Value is { } value)
        {
            ChosenLineNumber = (long)value;
        }

        Close();
    }
}
