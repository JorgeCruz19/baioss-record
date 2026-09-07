# Incidente 2026-09-06 — segmento perdido en una grabación programada de 8 horas

**Qué se vio:** la tarea «TEST SUNDAY» (07:00–15:00, segmentos de 30 min) produjo 17 archivos en vez de 16.
El segmento **9** pesa 2,85 GB en vez de ~3,9 GB y el Explorador no le muestra duración; a partir del 10, los
archivos empiezan en :24/:54 en vez de :00/:30.

## Reconstrucción (a partir de la carpeta)

La columna «Fecha» del Explorador en una carpeta de vídeos es la **fecha de creación del medio** que FFmpeg
escribe en la cabecera de cada archivo = el instante en que **empieza** cada segmento.

| Segmento | Empieza | Tamaño | Duración legible | Lectura |
|---|---|---|---|---|
| 1 … 8 | 07:00 … 10:30 | ~3,9 GB | 30:00 | normales |
| **9** | 11:00 | **2,85 GB (≈ 73 %)** | **—** | escribió ≈ 22 min y se cortó a las **≈ 11:22** |
| **10** | **11:24** | 3,87 GB | 30:00 | la grabación **se reanudó en una pieza nueva** |
| 11 … 16 | 11:54 … 14:24 | ~3,9 GB | 30:00 | normales, desplazados 24 min |
| 17 | 14:54 | 0,8 GB | 06:00 | hasta el fin de la tarea a las 15:00 |

Es decir: a las ~11:22 **el proceso de grabación murió**, la aplicación lo detectó y **siguió grabando en un
archivo nuevo** unos dos minutos después, numerado a continuación (10), y así hasta el final. Material perdido:
el hueco de ≈ 2 min entre 11:22 y 11:24, **más los 22 minutos del segmento 9 si no se recupera** (ver abajo).

Este comportamiento se reprodujo en local matando el proceso FFmpeg a mitad de un segmento: la aplicación hace
exactamente lo descrito (registro: «el proceso de grabación murió (código −1); recuperando en una PIEZA NUEVA»,
y un segundo después arranca con `-segment_start_number` siguiente).

## Por qué el segmento 9 no se puede leer

El producto graba **MP4 estándar con el índice (`moov`) al final** de cada archivo (`Recording:FragmentedMp4 =
false` en `appsettings.json`). Se eligió así por dos ventajas reales —búsqueda perfecta en reproducción local y
cierre rápido sin reescritura— y con un razonamiento: *«esta máquina tiene SAI, el corte de luz no es un
riesgo»*.

El razonamiento cubría el corte de luz, pero **no la muerte del propio proceso FFmpeg**, que tiene el mismo
efecto sobre el archivo que se está escribiendo: el índice se escribe al cerrar, y si el proceso muere antes,
el archivo se queda sin él. Sin índice, ningún reproductor lo abre («moov atom not found»), aunque los 2,85 GB
de vídeo estén físicamente ahí. Comprobado en local con la misma receta de FFmpeg:

| Modo | Segmento cerrado bien | Segmento cuyo proceso murió a mitad |
|---|---|---|
| MP4 estándar (el actual) | duración visible, reproducible | **ilegible** (sin `moov`) |
| fMP4 fragmentado | duración NO visible en el Explorador, reproducible | reproducible hasta el último fragmento (pierde ≤ 1 s) |

## Causa raíz (del registro de esa máquina, `baioss-20260906.log`)

El disco de destino **`D:` dejó de aceptar escrituras durante ~110 segundos**, y el vigilante de la aplicación
reaccionó matando a FFmpeg, lo que convirtió un bache recuperable en un archivo sin índice. La secuencia:

| Hora | Registro | Qué significa |
|---|---|---|
| 07:00–11:19 | Salud: A 30/30 fps, disco ≈ 2,2 MB/s | 4 h 20 min perfectas, un solo canal |
| **11:20:04** | Arranca una grabación **manual en el canal B** (DeckLink Duo 2, mismo códec x264 18 Mbps) | Segundo flujo hacia `D:` |
| 11:21:23 | Salud: A 2,5 MB/s, B 2,3 MB/s | Ambos bien; en total ≈ 5 MB/s, nada para un disco |
| **11:22:40** | Salud: **disco 0,0 MB/s en los dos canales**; B baja a 24 fps. La propia línea llega **17 s tarde** | `D:` ha dejado de completar escrituras; hasta la app se bloquea al mirar los archivos |
| **11:22:45** | `Watchdog: el archivo no crece desde hace 00:00:30 (FFmpeg reporta progreso pero no escribe); intentando cierre ordenado (q)` | A lleva 30 s sin que su archivo crezca (desde ≈ 11:22:15) |
| 11:22:58 → 11:23:43 | Salud: disco 0,0–0,6 MB/s; B 20 → 16 fps | El disco sigue colgado; B, que no fue matado, aguanta perdiendo cuadros |
| **11:23:14** | `Watchdog: FFmpeg no respondió al cierre ordenado; forzando` | La «q» no puede completarse: cerrar el archivo exige escribir en un disco que no responde |
| **11:23:55** | `FFmpeg salió con código -1` → `recuperando en una PIEZA NUEVA` | El kill tardó **41 s** en surtir efecto (proceso atascado en E/S del sistema) |
| 11:23:56 | Pipeline nuevo con `-segment_start_number 10` | Nace el archivo 10 (por eso empieza a las 11:24) |
| 11:24:01 | `_9.mp4 NO pasó la verificación (sin pistas/duración válidas)` | La app detecta el archivo sin índice y enciende la alarma |
| **11:24:13** | Salud: **disco 11,1 MB/s** (B 8,9), ambos 30/30 fps | `D:` vuelve y B vacía su atasco de golpe |
| 11:25 → 15:00 | Salud normal | Sin más incidencias |

**El canal B es la prueba de qué habría pasado sin el kill**: sufrió el mismo disco colgado, no fue matado
—su archivo creció un poco cada 30 s y el vigilante no saltó—, perdió algunos cuadros durante el bache y
**su archivo salió íntegro**. A fue matado únicamente porque su archivo no creció *nada* en 30 s, y eso costó
el segmento entero.

**Lo que NO fue**: ni la señal (ninguna pérdida de señal en todo el día), ni el codificador (x264 mantuvo 30 fps
en A), ni el espacio (sin avisos de disco lleno), ni un fallo de FFmpeg (salió con −1 = matado por nosotros).

**Por qué se colgó `D:`** no lo dice este registro: es cosa del disco o de lo que lo estuviera usando, no de la
aplicación. Lo comprobado en esa máquina después del incidente:

- **Registro «Sistema» de Windows entre las 11:21 y las 11:25: vacío.** Ningún reinicio de controladora, ningún
  tiempo de espera agotado, ningún error de NTFS. Es decir, el sistema no vio un fallo: cada operación terminó
  (muy tarde, pero terminó) dentro de su límite. El disco estaba **extremadamente lento**, no muerto.
- **Discos físicos: dos Seagate IronWolf 2 TB (`ST2000NT001`, HDD 5400 rpm, SATA) y un SSD SATA de 512 GB.**
  Con esto caen dos hipótesis iniciales: no es USB ni ahorro de energía, y la gama IronWolf es CMR (Seagate la
  publica como tal), así que tampoco encaja la pausa de reorganización de un disco SMR.

Y dos datos más, de una segunda lectura del registro, que cambian el diagnóstico:

- **No fue un bache aislado: hubo cuatro en el día.** Además del de las 11:22 (~110 s), la línea de salud
  muestra el ritmo de escritura del canal A cayendo a **0,9 MB/s a las 08:06** (~35 s parado), **1,4 a las
  09:48** (~20 s) y **0,2 a las 12:34** (~55 s), con el canal solo y a 30/30 fps en todos los casos. Solo el
  tercero superó los 30 s de crecimiento *nulo* que disparaban al vigilante.
- **A las 11:22 se pararon DOS discos a la vez.** El canal B grababa en `E:\capturer2\` —otro volumen, el
  otro IronWolf— y su ritmo también cayó a 0,0 en el mismo instante. Dos discos físicos distintos no se
  cuelgan a la vez por sí solos: **la causa está por encima de los discos** (algo que satura o congela la
  pila de almacenamiento de esa máquina durante decenas de segundos, de forma recurrente).

Descartado con los datos de esa máquina: el disco `D:` en sí (sano y, además, `E:` cayó con él), el
mantenimiento programado de Windows (Optimize Drives corrió por última vez el 1/9; el análisis de Defender, el
2/9 y el 6/9 a las 19:54; nada a las 11:2x), la propia aplicación (ninguna retención, archivado ni limpieza
en el registro de ese día) y cualquier error que el sistema haya visto (registro «Sistema» vacío).

Lo que queda, por orden: **(1) un programa de esa máquina que copia, sincroniza o respalda las carpetas de
grabación** entre `D:` y `E:` o hacia fuera (Historial de archivos, un agente de copia de seguridad, OneDrive u
otra sincronización, herramientas del fabricante del disco) — mover archivos de 4 GB entre dos HDD de 5400 rpm
deja a cero las escrituras de ambos durante justo ese tiempo—; **(2) una latencia de la controladora SATA o
su driver** que el registro «Sistema» no recoge pero el de Storport sí; **(3) salud de los discos**, poco
probable siendo dos. Cómo distinguirlas, en ese equipo:

```powershell
# Latencias de E/S que el registro «Sistema» no recoge (Storport avisa cuando una operación tarda de más)
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Storage-Storport/Operational'; StartTime='2026-09-06 07:00'; EndTime='2026-09-06 15:00'} -ErrorAction SilentlyContinue | Format-Table TimeCreated, Id, Message -Wrap

# ¿Hay algo copiando/sincronizando las carpetas de grabación? (Historial de archivos, respaldos, sincronización)
Get-Service fhsvc, wbengine, OneSyncSvc* -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-FileHistory-Engine/BackupLog'; StartTime='2026-09-06 07:00'; EndTime='2026-09-06 15:00'} -ErrorAction SilentlyContinue | Format-Table TimeCreated, Id -Wrap

# Salud de los dos HDD («sectores pendientes/reasignados», «errores CRC»): CrystalDiskInfo o smartctl.
```

Comprobado después en esa máquina: **Historial de archivos y Copia de seguridad de Windows, parados** (no es
un respaldo de Windows); el registro de Storport **vacío** — no concluyente, porque ese registro viene
desactivado de fábrica y no registra nada hasta que se activa. Quedan por revisar los **servicios de terceros**
que estén corriendo (agentes de copia de seguridad o sincronización, herramientas del disco) y si a esas horas
alguien **copiaba archivos de esas carpetas** por el escritorio remoto (RustDesk) o las tenía abiertas: leer un
archivo de 4 GB de un HDD de 5400 rpm a toda velocidad deja sus escrituras a cero.

```powershell
# Servicios en marcha que NO son de Windows: ahí estaría un agente de copia/sincronización
Get-CimInstance Win32_Service | Where-Object { $_.State -eq 'Running' -and $_.PathName -notlike '*\Windows\*' } |
  Format-Table Name, DisplayName, PathName -Wrap
```

También conviene mirar **Configuración → Buscar en Windows → «Buscar mis archivos»**: en modo *Mejorado* el
indexador recorre TODAS las unidades y procesa cada grabación nueva de 4 GB.

La lista de servicios de terceros en marcha en esa máquina señala **dos sospechosos principales** y un
mecanismo a comprobar:

1. **Febooti Automation Workshop** (servicio `FebootiAutomationWorkshop`): un automatizador de tareas —vigilar
   carpetas, copiar/mover archivos, subir por FTP— que es exactamente lo que se instala en un grabador para
   sacar las capturas a otro sitio. Una tarea suya copiando grabaciones de 4 GB desde `D:`/`E:` explica de
   sobra los cuatro baches y que afectaran a los dos discos. Mirar en su gestor la lista de tareas (qué hacen
   y con qué carpetas) y su **registro de ejecuciones** a las 08:06, 09:48, 11:22 y 12:34 del 6/9.
2. **Dell SupportAssist** (`SupportAssistAgent`): hace **análisis de hardware y «optimizaciones» programados**
   —incluido un análisis de discos que los lee enteros— y por defecto los programa **semanalmente, a menudo en
   domingo**. El incidente fue un domingo. Mirar en SupportAssist → Historial (y su programación en
   Configuración), o `C:\ProgramData\Dell\SupportAssist\` para las fechas.
3. **Instantáneas VSS**: hay un escritor VSS de SQL Server activo (`SQLWriter`). Crear una instantánea de
   volumen **congela las escrituras de ese volumen** varios segundos sin dejar error en «Sistema». Comprobar
   con `vssadmin list shadows` si existen instantáneas de `D:`/`E:` y quién las crea.

Además hay cinco agentes de acceso remoto (RustDesk, AnyDesk, NoMachine, UltraVNC, Mesh Agent) y el servicio
de WSL: ninguno toca los discos por sí solo, pero cualquiera permite transferir archivos o ejecutar scripts;
`wsl --list --running` dice si hay una distribución activa haciendo algo.

Y la herramienta definitiva para la próxima vez: dejar corriendo un contador de rendimiento que registre la
latencia de escritura de los discos, y mirar qué proceso tenía la cola del disco cuando vuelva a pasar:

```powershell
# Registra cada 5 s la latencia y la cola de los discos físicos en un CSV (dejar en marcha durante una grabación)
Get-Counter -Counter '\PhysicalDisk(*)\Avg. Disk sec/Write','\PhysicalDisk(*)\Current Disk Queue Length' -SampleInterval 5 -MaxSamples 5760 |
  Export-Counter -Path "$env:USERPROFILE\Desktop\discos-$(Get-Date -Format yyyyMMdd).csv" -FileFormat CSV -Force
```
En cuanto la alarma «El disco de destino no responde» aparezca en pantalla, abrir el **Monitor de recursos →
Disco** y anotar qué proceso encabeza «Actividad de disco»: ese es el culpable.

Sea cual sea el resultado, tres medidas baratas para un equipo que graba desatendido sobre HDD: **excluir la
carpeta de grabación** (`D:\capturer1`) de la protección en tiempo real de Defender y del indexador de
búsqueda; **desactivar la optimización programada** de ese disco (Optimize Drives → Cambiar configuración); y
vigilar la alarma nueva «El disco de destino no responde»: si reaparece, el disco es el problema.

## Qué se ha cambiado en la aplicación por esto

Además de los sucesos de auditoría (abajo), **el vigilante ya no mata a FFmpeg cuando el que no responde es
el disco**. Antes de matar, comprueba si la carpeta de destino acepta una escritura sincronizada (sonda de
escritura directa a disco, sin caché, con tiempo máximo). Si el disco no responde: se **espera** con la alarma
«El disco de destino no responde — la grabación espera sin cortar», la auditoría registra `StorageStalled`
(crítico) y, cuando el disco vuelve, `StorageStallCleared` con cuánto duró; FFmpeg reanuda solo, como hizo el
canal B. Si el disco responde y aun así FFmpeg no escribe, entonces sí es FFmpeg el colgado y se mata como
antes. Con este cambio, el 6/9 el canal A habría perdido unos cuadros, no 22 minutos.

## Recuperar el segmento 9

El vídeo está en el archivo; solo falta el índice. Se puede **reconstruir** con una herramienta que tome como
referencia un archivo sano con los mismos parámetros de codificación —aquí sirve cualquiera de los otros 16—:
[untrunc](https://github.com/anthwlock/untrunc) (`untrunc referencia_8.mp4 dañado_9.mp4`). No se ha probado
en este proyecto; la probabilidad de éxito es alta precisamente porque los hermanos son idénticos en
codificación. Hacerlo sobre una **copia**.

## Qué se ha cambiado a raíz de esto

La auditoría (📋 Actividad) **no decía nada** de la interrupción: mostraba «archivo cerrado», «archivo cerrado»,
como si nada hubiera pasado; la única pista estaba en el registro técnico. Ahora:

- **`RecordingInterrupted`** (aviso): el proceso de grabación murió y la grabación siguió en una pieza nueva,
  con el código de salida (−1 = matado) y el motivo conocido.
- **`RecordingFileUnverified`** (error): qué archivo no se puede reproducir y cuánto ocupa; y el segmento queda
  marcado como **dañado** en el historial en vez de figurar como una pieza sana.

## Decisión pendiente: el formato

Con el vigilante corregido, **este incidente concreto ya no se repetiría**: un disco que se cuelga se espera, no
se remata. Queda el riesgo residual del modo actual (MP4 estándar): una caída *de verdad* del proceso FFmpeg
—un cuelgue real, un fallo del driver de captura, un cierre forzado del equipo sin SAI— sigue costando el
segmento en curso, porque su índice se escribe al final. Es un riesgo mucho menor que el que se acaba de
cerrar, pero conviene decidirlo con conocimiento. Opciones, de menos a más cambio:

1. **Dejarlo como está** y asumir que una caída del proceso cuesta el segmento en curso (hasta 30 min), con la
   alarma en pantalla como aviso. Es lo que pasó aquí.
2. **Segmentos más cortos** (10–15 min): mismo riesgo, menos material por pieza. Sin coste.
3. **fMP4 para las grabaciones segmentadas**: una caída pierde ≤ 1 s en vez del segmento. A cambio, el
   Explorador no muestra la duración de las piezas y la búsqueda en VLC es por estimación (peor en archivos
   de 4 GB). Cambio de configuración, sin código.
4. **fMP4 + finalizar cada segmento al cerrarse** (reescritura a MP4 estándar en segundo plano, a baja
   prioridad): robustez del 3 y archivos «normales» del 1. Cuesta una lectura+escritura extra por segmento
   (≈ 4 GB cada 30 min por canal); con 4 canales en disco mecánico conviene medirlo antes. Requiere código.
