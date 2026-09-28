namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Lee la salida (stdout/stderr) de un proceso hijo línea a línea en un HILO PROPIO, no en el pool de hilos.
///
/// Por qué no <c>Process.BeginOutputReadLine</c>: en Windows las tuberías que <c>Process</c> crea para redirigir la
/// salida son SÍNCRONAS (tuberías anónimas), y .NET implementa sus lecturas «asíncronas» ejecutando una lectura
/// bloqueante en un hilo del pool. Cada flujo redirigido de cada proceso vivo ocupa así un hilo del pool de forma
/// permanente mientras espera bytes. Con varios FFmpeg a la vez (canales + receptores de red, dos flujos cada uno) el
/// pool se quedaba sin hilos y TODO lo asíncrono de la aplicación —frames del preview, relé de red, temporizadores,
/// finalización de tareas— se paraba en seco: con 4 procesos en una máquina de 8 núcleos y la CPU alta, 13 s de
/// preview congelado en cada Grabar/Detener (medido con los eventos del runtime: el pool fijaba su objetivo en 8 hilos,
/// los 8 bloqueados en tuberías, y tardaba ese tiempo en decidirse a crear otro). Un hilo dedicado por flujo cuesta
/// solo su pila y no compite con nada.
/// </summary>
public static class ProcessOutput
{
    /// <summary>
    /// Arranca un hilo de fondo que entrega a <paramref name="onLine"/> cada línea de <paramref name="reader"/> (en orden,
    /// una a una, en ese mismo hilo: el manejador no debe bloquearse mucho) y termina al cerrarse la tubería (el
    /// proceso salió). Una excepción del manejador no tumba el hilo ni el proceso: se pasa a <paramref name="onError"/>.
    /// </summary>
    public static Thread ReadLines(StreamReader reader, Action<string> onLine, string name, Action<Exception>? onError = null)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    try { onLine(line); }
                    catch (Exception ex) { onError?.Invoke(ex); }
                }
            }
            catch (Exception ex) { onError?.Invoke(ex); } // tubería cerrada bajo los pies (el proceso se liberó): fin
        })
        { IsBackground = true, Name = name };
        thread.Start();
        return thread;
    }

    /// <summary>Espera (acotado) a que los hilos lectores vacíen la tubería tras la salida del proceso, para que las últimas
    /// líneas (p. ej. el motivo de un fallo) se entreguen antes de anunciar esa salida.</summary>
    public static void Join(TimeSpan timeout, params Thread[] readers)
    {
        var deadline = DateTime.UtcNow + timeout;
        foreach (var t in readers)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !t.Join(remaining)) return;
        }
    }
}
