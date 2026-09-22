using Baioss.Record.Application.Network;

namespace Baioss.Record.App;

/// <summary>
/// Acceso a la API en ESTA ejecución: el archivo de ajustes, lo que pide (<see cref="Wanted"/>), lo que de verdad se
/// aplicó al arrancar (<see cref="Applied"/>; difiere si la dirección pedida no se pudo enlazar) y, en ese caso, por
/// qué (<see cref="Warning"/>). La ventana de Configuración lo enseña y guarda cambios para el SIGUIENTE arranque.
/// <see cref="LoadProblem"/>: el archivo existía pero no se pudo leer (se usaron los valores de partida).
/// </summary>
public sealed record ApiAccessState(string Path, ApiAccessSettings Wanted, ApiAccessSettings Applied, string? Warning, string? LoadProblem = null);
