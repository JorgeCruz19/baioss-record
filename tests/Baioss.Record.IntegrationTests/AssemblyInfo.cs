using Xunit;

// Los tests de integración lanzan procesos FFmpeg reales (grabación/preview) y abren SQLite.
// Ejecutarlos en serie evita la contención de CPU/IO que, en paralelo, hacía que el cierre del
// contenedor no llegara a tiempo y el test resultara intermitente.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
