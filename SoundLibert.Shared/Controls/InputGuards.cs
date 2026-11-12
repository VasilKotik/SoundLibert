using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SoundLibert.UI.Controls;

/// <summary>
/// Small input helpers. The main one: a row/list item has a Tapped handler, but
/// it may also contain buttons (like / more / remove). Tapping such a button
/// still bubbles Tapped to the row, which would trigger the row action as well
/// (e.g. clicking "unlike" also started playback). Handlers use
/// <see cref="FromButton"/> to bail out in that case.
/// </summary>
internal static class InputGuards
{
    public static bool FromButton(RoutedEventArgs e)
        => e.Source is Visual v && (v is Button || v.FindAncestorOfType<Button>() != null);

    /// <summary>
    /// True when a row tap originated on an inner button, so the row action must
    /// be skipped. Desktop uses <see cref="RoutedEventArgs.Source"/>; touch input
    /// is routed differently, so the original pointer source is used there.
    /// </summary>
    public static bool FromButtonInteracted(RoutedEventArgs e)
    {
        if (e.Source is Visual v && (v is Button || v.FindAncestorOfType<Button>() != null))
            return true;

        if (e is PointerEventArgs { Pointer.Captured: Visual captured } &&
            (captured is Button || captured.FindAncestorOfType<Button>() != null))
        {
            return true;
        }

        return false;
    }
}
