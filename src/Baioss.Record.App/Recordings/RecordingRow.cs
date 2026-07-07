using CommunityToolkit.Mvvm.ComponentModel;
using Baioss.Record.Domain;

namespace Baioss.Record.App.Recordings;

/// <summary>
/// Una fila de la lista de grabaciones (ventana «Grabaciones»). Los textos se calculan una vez al cargar;
/// la <see cref="Protection"/> es observable para que la fila refleje al instante el marcado del operador.
/// </summary>
public sealed partial class RecordingRow : ObservableObject
{
    public Guid Id { get; }
    public string ChannelKey { get; }
    /// <summary>Nombre del archivo de la grabación (el 1º si está segmentada), con extensión; «—» si no se conoce.</summary>
    public string FileName { get; }
    public string DateText { get; }
    public string TimeText { get; }
    public string DurationText { get; }
    public string SizeText { get; }
    public string Operator { get; }
    /// <summary>Carpeta que contiene los archivos de la grabación (para «Abrir carpeta»); null si no se conoce.</summary>
    public string? FolderPath { get; }

    public RecordingRow(Guid id, string channelKey, string fileName, string dateText, string timeText,
        string durationText, string sizeText, string @operator, string? folderPath, RecordingProtection protection)
    {
        Id = id;
        ChannelKey = channelKey;
        FileName = fileName;
        DateText = dateText;
        TimeText = timeText;
        DurationText = durationText;
        SizeText = sizeText;
        Operator = @operator;
        FolderPath = folderPath;
        _protection = protection;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtectionText))]
    [NotifyPropertyChangedFor(nameof(IsProtected))]
    [NotifyPropertyChangedFor(nameof(IsImportant))]
    [NotifyPropertyChangedFor(nameof(IsNormal))]
    private RecordingProtection _protection;

    /// <summary>Etiqueta legible de la protección (para el «chip» de la fila).</summary>
    public string ProtectionText => Protection switch
    {
        RecordingProtection.Protected => "🔒 Protegida",
        RecordingProtection.Important => "★ Importante",
        _ => "Normal",
    };

    public bool IsProtected => Protection == RecordingProtection.Protected;
    public bool IsImportant => Protection == RecordingProtection.Important;
    public bool IsNormal => Protection == RecordingProtection.None;
}
