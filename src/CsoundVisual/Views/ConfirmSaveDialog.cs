using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsoundVisual.Localization;
using CsoundVisual.ViewModels;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>
/// Modal "unsaved changes" prompt with Save / Don't save / Cancel. Built in code because it is
/// tiny; it reuses the application theme resources for its look.
/// </summary>
/// <remarks>
/// <para>
/// The dialog's result is a <see cref="SaveChoice"/>: <c>MainWindow</c> implements the
/// view-model delegate <c>MainViewModel.ConfirmSave</c> as
/// <c>new ConfirmSaveDialog(doc.FileName).ShowDialog&lt;SaveChoice&gt;(owner)</c>, and the VM
/// saves, discards or aborts (closing a tab or the app) depending on the answer.
/// </para>
/// <para>
/// Keyboard: Esc = <see cref="SaveChoice.Cancel"/>, Enter = <see cref="SaveChoice.Save"/> (the
/// primary, accent-coloured button). Closing with the window's ✕ makes
/// <c>ShowDialog&lt;SaveChoice&gt;</c> return <c>default(SaveChoice)</c>; that is why
/// <see cref="SaveChoice.Cancel"/> is the enum's first member (value 0), so ✕ means «cancel».
/// </para>
/// </remarks>
public sealed class ConfirmSaveDialog : Window
{
    /// <summary>Creates the dialog for the document called <paramref name="fileName"/>.</summary>
    /// <param name="fileName">Display name of the document (shown in the question).</param>
    public ConfirmSaveDialog(string fileName)
    {
        Title = Loc.S("Cambios sin guardar", "Unsaved changes");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.Brush("Bg");

        // Local factory: a themed button that closes the dialog with the given result.
        // "primary" adds the accent style used for the default action.
        Button Make(string text, SaveChoice choice, bool primary)
        {
            var b = new Button { Content = text, Classes = { "tool" }, Padding = new Thickness(16, 8) };
            if (primary) b.Classes.Add("primary");
            b.Click += (_, _) => Close(choice);
            return b;
        }

        Content = new StackPanel
        {
            Margin = new Thickness(22, 20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = Loc.S($"¿Guardar los cambios de {fileName}?", $"Save changes to {fileName}?"), FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Loc.S("Si no los guardas, se perderán.", "If you don't save them, they will be lost."), Classes = { "dim" } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        Make(Loc.S("Cancelar", "Cancel"), SaveChoice.Cancel, false),
                        Make(Loc.S("No guardar", "Don't save"), SaveChoice.Discard, false),
                        Make(Loc.S("Guardar", "Save"), SaveChoice.Save, true),
                    },
                },
            },
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(SaveChoice.Cancel);
            else if (e.Key == Key.Enter) Close(SaveChoice.Save);
        };
    }
}
