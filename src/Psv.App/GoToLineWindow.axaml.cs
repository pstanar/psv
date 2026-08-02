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
        UpdateMaxLine(maxLineOneBased);
        LineNumberUpDown.Minimum = 1;
        LineNumberUpDown.Value = Math.Clamp(currentLineOneBased, 1, LineNumberUpDown.Maximum);
    }

    /// <summary>
    /// Widens the upper bound alone, leaving the box's current value untouched - for the caller to
    /// call while this dialog is still open on a live-tailed, actively-growing file (its modal
    /// ShowDialog call runs a nested dispatcher loop, so the file can keep growing underneath it).
    /// Unlike <see cref="SetLineRange"/>, this must never overwrite what the user has already typed.
    /// </summary>
    public void UpdateMaxLine(long maxLineOneBased)
    {
        long max = Math.Max(1, maxLineOneBased);
        RangeText.Text = $"Line number (1 - {max:N0})";
        LineNumberUpDown.Maximum = max;
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
