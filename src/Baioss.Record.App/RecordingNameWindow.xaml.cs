using System;
using System.Windows;

namespace Baioss.Record.App;

/// <summary>
/// Diálogo modal que, al DETENER una grabación MANUAL, pide cómo guardarla. El nombre se usa como base
/// del archivo; si ya existe uno igual en la carpeta del canal, el motor añade « 1», « 2»… para no chocar.
/// </summary>
public partial class RecordingNameWindow : Window
{
    /// <summary>Nombre aceptado por el operador (válido solo cuando <see cref="Window.DialogResult"/> es true).</summary>
    public string RecordingName { get; private set; } = "";

    public RecordingNameWindow(string channelKey, string suggested)
    {
        InitializeComponent();
        Title = $"Guardar grabación — Canal {channelKey}";
        SubtitleText.Text = $"Canal {channelKey} · ponle nombre para guardarla";
        NameBox.Text = suggested;
        // Pre-selecciona el texto sugerido para que el operador pueda sobrescribirlo escribiendo directamente.
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { NameBox.Focus(); return; } // nombre obligatorio
        RecordingName = name;
        DialogResult = true;
    }
}
