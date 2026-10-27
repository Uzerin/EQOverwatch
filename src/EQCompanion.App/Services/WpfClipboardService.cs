using System.Runtime.InteropServices;
using System.Windows;
using EQCompanion.Core.Models;
using EQCompanion.Core.Theming;

namespace EQCompanion.App.Services;

/// <summary>The real clipboard. Windows briefly locks the clipboard while another program reads it, so writes are retried.</summary>
public sealed class WpfClipboardService : IClipboardService
{
    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(60);

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Retry(() => Clipboard.SetText(text));
    }

    public void SetEncounterImage(EncounterSnapshot encounter, ThemePalette palette, string footer, int topCount)
    {
        var image = EncounterImageRenderer.Render(encounter, palette, footer, topCount);
        Retry(() => Clipboard.SetImage(image));
    }

    private static void Retry(Action write)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (COMException) when (attempt < Attempts)
            {
                Thread.Sleep(RetryDelay); // CLIPBRD_E_CANT_OPEN: someone else has it open for a moment
            }
        }
    }
}
