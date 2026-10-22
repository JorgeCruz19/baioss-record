using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Storage;
using Baioss.Record.App.Localization;

namespace Baioss.Record.App.Storage;

/// <summary>
/// Una fila de la ventana «Borrar grabaciones». Los textos se calculan una vez al cargar (en el idioma vigente: la
/// ventana recarga al cambiarlo); la marca de selección es observable. Guarda la sesión tal como la leyó la lista, con
/// sus segmentos, para que la confirmación cuente sus archivos sin volver a la BD.
/// </summary>
public sealed partial class RecordingCleanupRow : ObservableObject
{
    public RecordingCleanupRow(RecordingSession session, string channelKey, bool inProgress)
    {
        Session = session;
        ChannelKey = channelKey;
        IsInProgress = inProgress;

        var local = session.StartedAt.ToLocalTime();
        DateText = local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        TimeText = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var first = session.Segments.OrderBy(s => s.Index).Select(s => s.FilePath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        FileName = first is null ? "—" : Path.GetFileName(first);
        FolderPath = first is null ? null : Path.GetDirectoryName(first);
        FilesText = session.Segments.Count.ToString(CultureInfo.InvariantCulture);
        DurationText = StorageFormat.Duration(session.Duration);
        SizeText = StorageFormat.Bytes(session.TotalBytes);
        StateText = inProgress ? Loc.T("Cln_State_InProgress") : session.Protection switch
        {
            RecordingProtection.Protected => Loc.T("Rec_Prot_Protected"),
            RecordingProtection.Important => Loc.T("Rec_Prot_Important"),
            _ => "",
        };
        DeleteTip = inProgress ? Loc.T("Cln_Tip_InProgress")
                  : IsProtected ? Loc.T("Cln_Tip_Protected")
                  : Loc.T("Cln_Tip_Delete");
    }

    public RecordingSession Session { get; }
    public Guid Id => Session.Id;
    public string ChannelKey { get; }
    /// <summary>Primer archivo de la grabación (con extensión), o «—» si no tiene ninguno registrado.</summary>
    public string FileName { get; }
    /// <summary>Carpeta del primer archivo, para «Abrir carpeta»; null si no se conoce.</summary>
    public string? FolderPath { get; }
    public string DateText { get; }
    public string TimeText { get; }
    public string DurationText { get; }
    /// <summary>Cuántos archivos tiene: más de uno en una grabación partida en segmentos o retomada tras una caída.</summary>
    public string FilesText { get; }
    public string SizeText { get; }
    /// <summary>«● En curso», «🔒 Protegida», «★ Importante», o vacío.</summary>
    public string StateText { get; }

    /// <summary>La está escribiendo un canal ahora: no se puede borrar.</summary>
    public bool IsInProgress { get; }
    /// <summary>Protegida o Importante: no se puede borrar sin pasarla antes a Normal.</summary>
    public bool IsProtected => RecordingDeletionRules.IsProtected(Session);
    // Para el color del chip de estado (los DataTrigger de WPF no comparan enums de otro ensamblado sin conversor).
    public bool IsMarkedProtected => Session.Protection == RecordingProtection.Protected;
    public bool IsMarkedImportant => Session.Protection == RecordingProtection.Important;
    public bool CanDelete => !IsInProgress && !IsProtected;
    /// <summary>Qué hace el botón de borrar de la fila o, si no se puede, por qué.</summary>
    public string DeleteTip { get; }

    [ObservableProperty] private bool _isSelected;
}
