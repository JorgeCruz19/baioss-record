using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Metadata;

public enum MetadataFormat { Xml, Json, Csv }

/// <summary>
/// Exporta la metadata de una sesión (fecha, operador, canal, fuente, códecs,
/// resolución, fps, timecodes, layout de audio…) a XML/JSON/CSV como sidecar.
/// </summary>
public interface IMetadataExporter
{
    Task<string> ExportAsync(RecordingSession session, MetadataFormat format, string outputPath, CancellationToken ct = default);
}
