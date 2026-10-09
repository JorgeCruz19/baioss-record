# Auditoría de estabilidad antes de la venta — 2026-09-27

Tercera auditoría profunda (las anteriores: `AUDITORIA-24x7.md` y `AUDITORIA-GRABACION-2026-07.md`), hecha sobre `main` justo
después de fusionar las entradas SRT/RTMP y el relé en crudo de DeckLink, con el objetivo de dejar el producto lo más estable
posible para la venta.

**Método.** Cuatro revisiones de solo lectura en paralelo (relé de red + receptor + alineador + colchón; relé en crudo + lector
NUT + receptor DeckLink; motor del canal + supervisor de FFmpeg; canal + aplicación + API + bus de eventos). Cada afirmación se
verificó después contra el código antes de corregirla, y cada corrección importante lleva su prueba. Donde se pudo, la prueba se
ejecutó también contra el código ANTERIOR para demostrar que el fallo era real (p. ej. la sonda de disco: falla 3 de 3 veces con el
código anterior). Después, pruebas completas y la prueba de resistencia de 4 canales.

**Resultado.** 56 hallazgos corregidos (3 críticos, 11 altos, 25 medios, 17 bajos) y 12 aplazados con motivo. Ningún hallazgo
implicaba corrupción de archivos en el camino normal; los graves estaban en la supervisión (fallos silenciosos), el ciclo de
vida de los procesos y el apagado.

| Severidad | Corregidos | Aplazados |
|---|---|---|
| Crítico | 3 | 0 |
| Alto | 11 | 0 |
| Medio | 25 | 4 |
| Bajo | 17 | 8 |

---

## Críticos

- **S1 · La grabación moría con el token de quien la pidió.** El supervisor de FFmpeg enlazaba la vida del proceso al
  `CancellationToken` del llamador. Por la API llegaba el de la petición HTTP: un cliente que abortaba la petición de «grabar»
  (timeout de fetch, pestaña cerrada) cerraba la grabación recién abierta. Por el programador llegaba su `stoppingToken`: al cerrar
  la aplicación, TODAS las grabaciones programadas recibían «q» antes de su parada ordenada, la auditoría registraba una
  interrupción falsa y la recuperación competía con el apagado. **Corrección:** el supervisor usa un CTS propio (la vida del
  proceso termina solo en `DisposeAsync`); además, Iniciar y Detener del canal se completan enteros una vez pasado el pre-vuelo.
  Prueba: `CancellingTheCallersToken_NoLongerStopsTheProcess`.
- **S2 · Barras «para siempre» tras una caída del proceso con la señal presente.** `RecoverRecordingAsync` entraba en carta de
  ajuste sin mirar la señal; en las fuentes que publican su señal (NDI, SRT/RTMP) la única salida del slate era el aviso «señal de
  vuelta», que no llega si la señal nunca se fue. Un kill del vigilante bajo carga o un error de escritura dejaba el canal grabando
  barras hasta que el emisor reconectara (y a los 5 min, alarma de pérdida de señal falsa). **Corrección:** carta de ajuste solo si
  la señal falta; y el bucle de recuperación sale del slate si la fuente ya dice que hay señal. Prueba de integración con FFmpeg:
  `RecorderDeathWithTheSignalPresent_RebuildsTheLiveSource_NotBars`.
- **S3 · Una DeckLink que deja de entregar frames no se detectaba nunca.** El vigilante del supervisor toma por «progreso»
  cualquier línea de stdout, y FFmpeg imprime su progreso cada segundo aunque el contador de frames esté parado (medido con el
  FFmpeg empaquetado: 8 s sin entrada = 7 líneas `progress=continue` con `frame=100` congelado). Una tarjeta colgada sin decir «No
  input signal detected» dejaba el canal en «SEÑAL OK», el preview congelado y el archivo sin crecer, sin alarma.
  **Corrección:** `FrameFlowWatch` en el receptor permanente vigila los frames que cruzan el relé por conexión; a los 30 s sin
  ninguno (tras haber fluido, con la tarjeta abierta y con señal) reabre la captura como tras una caída. Pruebas: tres unitarias y
  `ACaptureThatStopsDeliveringFrames_IsReopened_ByTheFrameWatch` (FFmpeg real leyendo de una «tarjeta» que se calla).

## Altos

- **S4 · Reservas huérfanas en el relé de red.** Si el emisor se iba entre construir el proceso del canal y su conexión, la
  reserva quedaba en la lista con su cola cerrada: en el flujo siguiente «desbordaba» en cada fragmento (un aviso cada 5 s que
  tapaba los descartes reales), nunca iba «al día» (cada relevo esperaba el tope de 10 s: Detener cerraba el archivo tarde) y
  retenía hasta 32 MB de pre-roll. **Corrección:** al terminar el flujo salen todos los consumidores, reservados incluidos.
  Prueba: `AReservationPendingWhenTheSourceCloses_IsDropped_AndTheNextStreamDoesNotOverflow`.
- **S5 · Contraseña SRT en claro en el registro.** La línea de comandos del receptor se registraba entera, con `-passphrase` y
  `-srt_streamid`. **Corrección:** se tapan al registrar. Prueba: `TheSrtPassphraseAndStreamId_NeverReachTheLog`. (Límite
  inherente: la línea de comandos de FFmpeg la ve cualquier usuario local del equipo.)
- **S6 · Proceso FFmpeg huérfano con el dispositivo abierto.** `FfmpegChannelEngine.DisposeAsync` no se serializaba con las
  transiciones: reasignar la entrada mientras un Detener cerraba el archivo podía dejar vivo el preview que ese Detener lanzaba,
  con la tarjeta abierta (el canal nuevo no podía abrirla hasta reiniciar). **Corrección:** el cierre toma el semáforo del motor
  (con tope), es idempotente y ninguna transición lanza procesos con el motor dispuesto; y la reasignación solo se permite con el
  canal en reposo, comprobado dentro de su candado (antes, fuera y solo «grabando/en pausa»).
- **S7 · Fuga de un sumidero de preview por cada lanzamiento fallido.** Con el emisor caído, cada reintento de recuperación
  (≈ cada 30 s) dejaba vivo un listener TCP y su tarea. **Corrección:** si el proceso no arranca, su sumidero se retira.
- **S8 · Una recuperación vieja partía la grabación siguiente.** La cadena de recuperación (backoff de hasta 30 s) no estaba atada
  a su grabación: Detener + Grabar dentro de ese margen hacía que partiera la grabación NUEVA en otra pieza (o la mandara a barras).
  **Corrección:** generación de grabación; la cadena solo actúa sobre la suya.
- **S9 · El apagado podía matar un archivo a medio cerrar.** Tope total de 20 s frente a 8 s de servicios + hasta 15 s de relevo
  del preview + hasta 30 s de cierre de FFmpeg. **Corrección:** en el apagado, Detener cierra el archivo SIN levantar un preview
  nuevo (el canal se dispone justo después) ni verificar/optimizar el archivo, y el tope pasa a 45 s.
- **S10 · Parada por «disco lleno» con el disco vacío.** Una medida fallida del volumen (NAS con un corte de un sondeo, USB
  reenumerándose) llegaba como «0 bytes libres» y detenía la grabación. **Corrección:** sin medida no se decide nada (aviso con
  freno), y el auto-stop exige dos lecturas críticas seguidas. Pruebas: `AVolumeThatCannotBeMeasured_...`, `AutoStop_Needs...`.
- **S11 · Cada panel web se sumaba a la duración de Grabar/Detener.** El WebSocket de eventos enviaba dentro de la publicación
  del bus, que va en ese camino (hasta 5 s por cliente que no lee). **Corrección:** cola acotada y escritor propio por conexión;
  el bus solo encola.
- **S12 · Un token HTTP cancelado a mitad dejaba el canal incoherente.** Con el Detener a medias quedaba la guarda de disco
  desenganchada con la grabación en marcha, o el archivo cerrado y la sesión abierta en la base de datos (un «corte» falso al
  reiniciar). **Corrección:** Iniciar (tras el pre-vuelo) y Detener se completan enteros (ver S1).
- **S13 · Los descartes de los relés no llegaban a nadie.** Cuando un proceso del canal no daba abasto (disco o CPU atascados), los
  relés le descartaban datos —la grabación tiene un salto— con solo una línea de aviso cada 5 s: el evento no estaba conectado.
  **Corrección:** los descartes llegan al canal (`ICaptureSource.InputDataDropped`), que enciende la alarma «Frames perdidos» y
  registra un error con el número de unidades. Pruebas de reenvío en la fuente DeckLink y de recuento en el relé de red.
- **S14 · Fuga de handles por cada proceso terminado.** Los lectores de stdout/stderr no cerraban su tubería al terminar (con
  lecturas síncronas, `Process.Dispose` no las cierra) y el objeto de cada proceso relanzado no se liberaba: todo esperaba al
  recolector. Lo destapó la prueba de resistencia (+293 handles en 80 ciclos). **Corrección:** cada lector cierra su tubería al
  terminar y el supervisor libera el proceso (y su stdin) en cuanto sale. Resultado: +38 handles en 80 ciclos, hilos estables.

## Medios

| ID | Fallo | Corrección |
|---|---|---|
| S15 | El vigilante podía matar a FFmpeg en mitad de la «q» de un Detener (archivo sin índice). | Tras cada espera del vigilante se re-comprueba si empezó un cierre ordenado; ese cierre decide. |
| S16 | Los bucles de espera de señal y de recuperación se duplicaban y quedaban incancelables (sondeos que se estorbaban). | Se cancela el bucle anterior antes de abrir otro; el token se captura por bucle. |
| S17 | Si fallaba el relanzamiento de la degradación de codificador o de canales de audio, el canal quedaba «grabando» sin proceso. | Vuelve al camino normal de recuperación (pieza nueva). |
| S18 | La sonda del disco abría un hilo nuevo por pregunta con el disco colgado, y dos canales en la misma carpeta se hacían fallar entre sí. | Una sola escritura en vuelo por carpeta. La prueba falla 3/3 con el código anterior. |
| S19 | El vigilante del crecimiento del archivo no se reiniciaba en los relanzamientos internos (carta de ajuste: bucle de cierre cada ~35 s). | Un vigilante nuevo por proceso. |
| S20 | El sondeo de recuperación redirigía la salida de FFmpeg y no la leía (podía bloquearse y contar como fallido). | Sin redirección y con `-loglevel error -nostats`. |
| S21 | El conjunto de alarmas activas se modificaba desde varios hilos sin candado (las alarmas podían dejar de funcionar en silencio). | Candado y suscriptores aislados. |
| S22 | Sin nombre base, medir los bytes y buscar segmentos recorría todo el histórico del canal cada 2 s. | Glob por la base real de cada proceso de la sesión. |
| S23 | Un fin de flujo limpio (el emisor cerró) seguía el backoff de las caídas: puerto de escucha cerrado hasta 30 s. | Relanzamiento a los 300 ms sin contar como reintento (con backoff si la ejecución fue fugaz). Prueba. |
| S24 | La cola por consumidor del relé de red se medía en fragmentos: toleraba 5–15 s de un consumidor parado. | Tope en bytes (128 MiB ≈ 50 s a 20 Mb/s). Prueba. |
| S25 | Los bucles de aceptación de ambos relés podían morir en silencio (y el de origen, girar en caliente). | Alta protegida y pausa tras un fallo. |
| S26 | Una excepción del alineador de audio cortaba la conexión con el emisor. | Se reenvía el fragmento sin realinear (aviso con freno). |
| S27 | El alineador retenía el audio sin tope de tiempo si el vídeo no llegaba (hasta 32 MB). | Tope de 10 s por reloj. |
| S28 | Una excepción en la entrega del preview con colchón mataba su hilo, y con él la aplicación. | Entrega protegida (en el colchón y en el motor). |
| S29 | DeckLink: el volcado de la entrada pisaba un «sin señal» anterior y el canal quedaba en «SEÑAL OK» sin señal tras reabrir. | El estado de señal se reinicia por proceso, no al abrir. Prueba. |
| S30 | Relé en crudo: tras un descarte se reanudaba en cualquier unidad (en NUT las marcas son relativas al último punto de sincronía). | Se reanuda solo en un punto de sincronía. |
| S31 | Los receptores permanentes (red y DeckLink) quedaban atados al token con plazo de quien abrió la fuente. | Su vida es la de la fuente. |
| S32 | El renombrado bloqueaba un hilo del pool hasta 10 min y podía renombrar otra sesión si otra grabación empezaba y terminaba mientras se escribía el nombre. | Asíncrono, y por sesión (`RenameRecordingAsync`). |
| S33 | La persistencia de cada segmento corría dentro de un candado que la interfaz toma al renombrar, y sus tareas no se podaban. | Fuera del candado y en el pool; se podan las terminadas. |
| S34 | Grabar, Detener, aplicar un preset y cambiar la carpeta hacían E/S de disco y SQLite en el hilo de la interfaz. | Al pool. |
| S35 | La reconciliación de segmentos huérfanos y el barrido de temporales de remux solo miraban `recordings/`. | También la carpeta de cada canal (sin subcarpetas, en segundo plano). |
| S36 | La poda de la auditoría borraba meses de filas en un solo DELETE (tomando el único escritor de SQLite). | Por lotes de 5000 con pausa. Prueba. |
| S37 | Detener esperaba sin tope a la medida del disco (un NAS colgado la bloquea en el sistema). | Tope de 2 s. |
| S38 | La limpieza de retención buscaba candidatos por fecha de fin sin índice. | Índice `IX_Sessions_EndedAt` idempotente. |
| S39 | Ver S6: la comprobación de «canal ocupado» de la reasignación iba fuera de su candado e ignoraba Iniciando/Deteniendo. | Dentro del candado y solo en reposo. |

## Bajos

S40 eventos del supervisor aislados (un suscriptor que lanzaba terminaba la supervisión; prueba) · S41 la recuperación se
programa aunque falle el aviso de interrupción · S42 cierres idempotentes (sumidero, relés, receptores, motor) · S43 parada del
escaneo de segmentos reentrante · S44 cada relevo juzga «al día» con la reserva de SU proceso · S45 `NetworkInput.Validate` ya no
se salta comprobaciones en escucha sin host (prueba) · S46 el sondeo de una fuente de red no consume reservas · S47 lector NUT:
valores imposibles como flujo inválido y la unidad se devuelve al pool · S48 el relé en crudo ya no registra cada reapertura
normal como «flujo NUT inválido» · S49 las reservas del relé en crudo caducan también sin flujo · S50 el nivel de disco se
reinicia en cada grabación · S51 el canal suelta todas sus suscripciones al disponerse · S52 la confirmación de cierre tiene
dueño y no se abre dos veces · S53 la API contesta 404 (no 500) a un canal inexistente o reasignándose (prueba) · S54 el
WebSocket ya no puede lanzar al disponer su semáforo (ver S11) · S55 la bajada de canales de audio de DeckLink toma el candado ·
S56 si la señal vuelve a caer mientras se sale de la carta de ajuste, se reevalúa en el acto.

## Aplazados (con motivo)

| Severidad | Hallazgo | Motivo |
|---|---|---|
| Medio | Durante una reasignación de entrada (2–20 s) el programador audita «franja omitida» en cada segundo. | Solo durante la reasignación, que ya no se permite con el canal ocupado; ruido en la auditoría, sin pérdida. |
| Medio | Techo de memoria del relé en crudo: 128 MB por consumidor (hasta ~3 GB con 8 canales 4K atascados a la vez) y el pool de búferes redondea a potencias de 2. | Solo con todos los discos atascados a la vez; conviene un presupuesto por relé cuando haya equipos 4K. |
| Medio | `StorageManager` no mide volúmenes UNC: la limpieza por espacio queda desactivada para destinos en NAS. | La retención de medios es opt-in y hoy está desactivada. |
| Medio | Apagado sin indicación de progreso («Finalizando N canales…» y botón de forzar). | El tope de 45 s y el Detener de apagado (S9) cubren el riesgo; la mejora es de interfaz. |
| Bajo | El alineador de audio no vuelve a medir tras un salto de reloj del emisor a mitad de flujo. | El servo lo corrige despacio; sin caso real observado. |
| Bajo | Reservas del relé de red emparejadas por orden de conexión (dos lanzamientos seguidos pueden intercambiarlas). | Efecto visual menor (unos frames del preview). |
| Bajo | Copias por fragmento en el relé de red y el alineador. | Coste medido asumible (relé ≈ 0,19 núcleos por canal). |
| Bajo | Rasgado ocasional del preview con la interfaz muy cargada (cuarentena de 2 búferes). | Cosmético y heredado; sin queja en producción. |
| Bajo | Temporizador de 15,6 ms del colchón de preview a 50/60 fps. | Medido bien a 25/30 fps; revisar con fuentes de 50/60 fps. |
| Bajo | Un `BeginInvoke` por línea de medidores (80/s con 8 canales). | Sin problema medido en la interfaz. |
| Bajo | Líneas de stderr que llegan tras anunciar la salida del proceso (tope de 2 s). | Muy improbable; efecto: un aviso tardío. |
| Bajo | Cierre de Windows con el diálogo del nombre abierto. | No alcanzable desde la interfaz (el diálogo es modal). |

## Evidencia

**Pruebas.** Unitarias: 743 en verde (17 nuevas: `StabilityAuditTests` y ampliaciones de `NetworkStreamRelayTests` y
`DecklinkCaptureSourceRelayTests`). Integración con FFmpeg real: 93 en verde y 5 omitidas por diseño (sin hardware NDI, medición
de rendimiento y resistencia bajo variable de entorno); 2 nuevas en `StabilityAuditIntegrationTests`. La prueba de API que
esperaba un 500 para un canal inexistente pasa a exigir 404 (S53).

**Contraprueba.** `ConcurrentVolumeProbesOnTheSameFolder_AllSeeTheDiskResponding` se ejecutó contra el `VolumeProbe` anterior:
falla 3 de 3 veces (16 sondas simultáneas en la misma carpeta: alguna ve «el disco no responde»). Con el nuevo pasa siempre.

**Resistencia** (`RelaySoakTests`, `BAIOSS_SOAK=1`): 4 canales DeckLink sintéticos por el relé en crudo, ciclos de Grabar/Detener
en paralelo y una caída provocada del proceso de captura por canal. Handles medidos tras recolectar la basura.

| Corrida | Archivos | Errores | FFmpeg (en marcha → final) | Hilos | Handles | Detener p50 / p95 / máx |
|---|---|---|---|---|---|---|
| Antes de corregir S14, 20 ciclos | 80 | 0 | 8 → 8 | 40 → 36 | 530 → 823 (+293) | 0,82 / 1,89 / 2,36 s |
| Después, 20 ciclos | 80 | 0 | 8 → 8 | 39 → 39 | 525 → 563 (+38) | 0,82 / 2,00 / 2,02 s |
| Después, 40 ciclos | 160 | 0 | 8 → 8 | 39 → 39 | 524 → 563 (+39) | 1,76 / 2,15 / 2,98 s |

Los handles ya no crecen con los ciclos (+38 en 80 paradas, +39 en 160). Cada receptor se relanzó exactamente una vez, la
caída provocada: el vigilante de frames (S3) no dio ningún falso positivo. La mediana de Detener salta entre 0,8 y 1,8 s según
cuántas paradas caen en el modo lento del cierre de x264 (0,3–0,5 s o 1,3–1,5 s con los 4 canales parando a la vez); ese reparto
bimodal ya estaba en las corridas anteriores a los cambios (medianas de 1,73 y 0,84 s, p95 de 1,9 s).

**Build de venta.** Las 6 DLL se copiaron a `publish/` y se volvió a ofuscar (`scripts\obfuscate.ps1`, autoverificado). Prueba de
humo de esa build ofuscada en una copia aislada, con base de datos nueva: arranca y la API responde; Grabar → Detener con nombre
por la API renombra el archivo (`auditoria-humo.mp4`); un canal inexistente devuelve 404; con los dos canales grabando, cerrar
la ventana y confirmar deja la aplicación cerrada en 1,4 s (cada archivo cerrado en 0,7 s con el Detener de apagado, sesiones
cerradas en la base de datos, archivos válidos de 19,9 s según ffprobe); al volver a abrir no hay sesiones huérfanas; y el
registro no tiene ni un error (ni `MissingMethod`/`TypeLoad` por la ofuscación).

## Cambios de comportamiento visibles

- Cerrar la aplicación con grabaciones en curso puede tardar hasta 45 s (antes el tope era 20 s y se forzaba la salida).
- Cuando un relé descarta datos porque la grabación no da abasto, se enciende la alarma «Frames perdidos» y queda un error en
  el registro con el número de unidades descartadas.
- Una DeckLink que deja de entregar frames 30 s se reabre sola (el canal pasa por «reabriendo»).
- En carta de ajuste, la DeckLink en modo relé ya no se sondea: sale de las barras cuando su receptor ve señal.
- Un emisor SRT/RTMP que cierra tras emitir encuentra el puerto de escucha abierto de nuevo en 0,3 s.
- La API responde 404 a un canal inexistente o que se está reasignando.
- Cerrar la ventana abre una sola confirmación aunque se pulse la X varias veces.
