# FFmpeg: por qué no se distribuye y cómo lo instala el cliente

## La decisión

**Baioss Record NO empaqueta FFmpeg.** El instalador crea la carpeta `tools\ffmpeg\` con un archivo
`FFMPEG-LEEME.txt` / `FFMPEG-README.txt`, y es el **cliente** quien descarga los binarios y los deja ahí.

**El motivo es legal, no técnico.** El build de FFmpeg que se usa en desarrollo está compilado con
`--enable-gpl` **junto a** `--enable-nonfree --enable-libfdk-aac --enable-decklink`, y su propio archivo de
licencia lo dice sin rodeos:

> `This version of ffmpeg has nonfree parts compiled in. Therefore it is not legally redistributable.`

Esa combinación no se puede distribuir bajo ninguna licencia: bajo GPL no, porque incorpora partes
propietarias; bajo licencia propietaria tampoco, porque incorpora x264/x265 (GPL). Y no es un descuido de quien
lo compiló: **el soporte DeckLink de FFmpeg exige `--enable-nonfree`**, porque el SDK de Blackmagic es
propietario. Es decir, es nonfree justo por la funcionalidad que da valor al producto.

Lo prohibido es que **nosotros** lo distribuyamos. Que el cliente descargue y use ese mismo build en su equipo
es perfectamente legal, y es lo que hace todo el mundo que usa FFmpeg con tarjetas Blackmagic.

## Qué ve el cliente

1. **Al terminar la instalación**, la última pantalla del asistente le dice que falta ese paso, con la ruta
   exacta, y una casilla marcada abre esa carpeta en el Explorador.
2. **En la carpeta** encuentra `FFMPEG-LEEME.txt` (o `FFMPEG-README.txt` en inglés): qué descargar, dónde dejarlo y cómo comprobar que la
   compilación sirve (incluido el comando que lista las tarjetas DeckLink).
3. **Si abre el programa sin haberlo hecho**, aparece un aviso claro —con la ruta— explicando que arranca en
   modo de demostración y que no grabará hasta instalarlo. El aviso se repite en cada arranque mientras falte;
   es deliberado: un grabador que no graba en silencio es mucho peor que uno que insiste.

## Qué compilación descargar, y qué driver pide NVENC

**La recomendada es la de desarrollo:** las releases de
[AnimMouse/ffmpeg-autobuild](https://github.com/AnimMouse/ffmpeg-autobuild/releases) (GitHub, hechas con
ffmpeg-windows-build-helpers), archivo `ffmpeg-<commit>-<commit>-win64-nonfree.7z`. La variante *nonfree* es la que trae
`decklink` y `libfdk_aac`; las compilaciones más conocidas (gyan.dev, BtbN) son GPL/LGPL y no traen DeckLink. La de
`tools\ffmpeg` en desarrollo es la del 2026-06-14 (`git-2026-06-14-b1d2190`). La excepción son las NVIDIA Maxwell, Pascal
y Volta: para ellas, FFmpeg 8.0 estable, del mismo autor (ver abajo).

**El nivel de API de NVENC lo fija la compilación, no la versión de FFmpeg.** FFmpeg se compila contra las cabeceras
`nv-codec-headers` de un Video Codec SDK y, al abrir el codificador, exige que el driver admita al menos esa API. Si no la
admite, el codificador no abre:

```
[h264_nvenc] Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0
[h264_nvenc] The minimum required Nvidia driver for nvenc is 610.00 or newer
```

| Cabeceras (SDK) | Driver mínimo en Windows (`libavcodec/nvenc.c`) |
|---|---|
| 13.1 | 610.00 |
| 13.0 | 570 |
| 12.2 | 551.76 |
| 12.1 | 531.61 |
| 12.0 | 522.25 |

ffmpeg-windows-build-helpers compila la rama principal de nv-codec-headers, que pasó al SDK 13.1 el 2026-06-09 (la
etiqueta `n13.1.15.0` llegó el 2026-07-14). Por eso las releases de ffmpeg-autobuild **desde la del 2026-06-14 piden
driver ≥ 610**, y las releases **hasta la del 2026-04-06** (la última anterior; entre ambas no hay ninguna) **se conforman
con ≥ 570** (de esas, ver abajo cuál usar). Las herramientas usan esa misma rama al compilar FFmpeg estable (desde la
6.1), así que en [AnimMouse/ffmpeg-stable-autobuild](https://github.com/AnimMouse/ffmpeg-stable-autobuild/releases) vale
igual: el SDK lo decide la fecha de la release, no la versión de FFmpeg.

**Maxwell, Pascal y Volta no pasan de la rama R580.** NVIDIA cerró su soporte en esa rama (GeForce GTX 750/9xx/10xx,
Titan X/Xp/V, Quadro M y P, Quadro GV100), que llega a la API 13.0 de NVENC. Con una compilación de SDK 13.1, NVENC no
abre nunca en esas tarjetas. La app lo detecta al arrancar (`FfmpegLocator.ProbeVideoEncoderAsync` deja la razón en el
registro) y cada canal NVENC degrada solo (`EncoderFallbackChain`, alarma *EncoderFallback*) a QuickSync, AMF o CPU. No
se pierde nada, pero la CPU paga: x264 cuesta unos 4 núcleos lógicos por canal 1080i en un Core i7-8700. Turing y
posteriores (GTX 16xx, RTX, Quadro RTX y T, RTX A-series, Ada, Blackwell) sí reciben drivers ≥ 610. Kepler (driver 470)
no abre NVENC con ninguna de estas compilaciones.

**Para un cliente con Maxwell, Pascal o Volta: FFmpeg 8.0 estable**, exactamente la release
[`2025-09-03-16-39-ae69ef0-8.0`](https://github.com/AnimMouse/ffmpeg-stable-autobuild/releases/tag/2025-09-03-16-39-ae69ef0-8.0)
de ffmpeg-stable-autobuild, archivo `ffmpeg-8.0-ae69ef0-win64-nonfree.7z` (109 826 526 bytes; SHA-256
`7ad859c418b9dc0d954585092d3eb62a51921f63d44946ace1461039e22d7d14`, el que publica GitHub). Es la versión oficial
`n8.0` (libavcodec 62.11.100) compilada con el SDK 13.0, así que NVENC abre con driver ≥ 570 (probado con el 582.78). Su
`-buildconf` es idéntico al de la del 2026-06-14 (decklink, nonfree, fdk-aac, nvenc/nvdec, AMF, libmfx/QSV, libsrt,
x264/x265, svt-av1, dav1d, zimg, freetype/harfbuzz) y, como ella, es *nonfree*: la descarga el cliente, no se
redistribuye. Es la última release de ese proyecto a 2026-10-16, pero está hecho para publicar otra cuando cambian la
versión de FFmpeg o las herramientas, y una compilada después del 2026-06-09 traería el SDK 13.1 y pediría ≥ 610: de ahí
el «exactamente».

**La del 2026-03-12 también sirve** (era la recomendada antes): release
[`2026-03-12-13-56-afcde65-c6e5733`](https://github.com/AnimMouse/ffmpeg-autobuild/releases/tag/2026-03-12-13-56-afcde65-c6e5733)
de ffmpeg-autobuild, archivo `ffmpeg-afcde65-c6e5733-win64-nonfree.7z` (109 841 674 bytes; SHA-256
`5fc564b337a0facd398eb5d143c21f91b3b51fc80d06d94b8fb25dfed08fa797`, el que publica GitHub). Es FFmpeg
`git-2026-03-12-afcde65`, también con el SDK 13.0 y probada en el mismo equipo, pero **no trae AMF** (`--disable-amf`,
sin `h264_amf`/`hevc_amf`): no sirve para equipos con gráfica AMD. El resto de `-buildconf` es idéntico. Un equipo que ya
la tenga puede quedarse con ella.

**Las releases de ffmpeg-autobuild del 2026-03-15 al 2026-04-06 no:** también abren NVENC con driver 58x, pero llevan
un fallo de FFmpeg que deja **ilegibles los MP4/MOV de QuickSync**. El commit `6bc257e` («avformat/nal: remove trailing
zeroes from NALUs», 2026-03-15) quita los ceros finales de cada NAL al escribirla, pero sigue contando su tamaño original.
El muxer MP4 suma ese tamaño al de `mdat`, así que el archivo anuncia un `mdat` mayor que lo escrito: ffprobe salta más
allá del final y dice «moov atom not found». Lo arregló `2948acd` el 2026-05-09 (issue #23010), y ya está en las de
junio; FFmpeg 8.0 es anterior al fallo y no lo lleva. Le pasa a lo que acaba sus NAL en ceros, como el relleno del CBR
de QuickSync con imagen fija o negro; en lo medido, NVENC y x264 no lo provocan. Medido con la del 2026-04-06: un negro
de 8 s a 16 Mbps con `h264_qsv` o `hevc_qsv` no se lee, y con DeckLink la grabación por QuickSync (la degradación de un
canal NVENC sin sesión libre) salió con 52 KB y sin índice (la alarma «grabación sin verificar» lo avisó). Con la del
2026-03-12 y con la de junio, ese negro se lee; con la 8.0 se leen los 6 MP4/MOV sintéticos de QuickSync con negro o
imagen fija, y también la carta fija real de LiveU llegada por la DeckLink. Por eso, para Pascal, la 8.0 o la del
2026-03-12, y no una release de ffmpeg-autobuild posterior.

**Verificadas las dos el 2026-10-16** en el equipo de pruebas DeckLink (Quadro P400 con driver 582.78, Core i7-8700,
DeckLink Duo 2 con dos señales 1080i59.94 de emisión) con `DecklinkHardwareTests`, que monta el mismo cableado que la app.
Las cifras de los primeros puntos son de la del 2026-03-12; el último resume la pasada con la 8.0:

- **Los 16 presets NVENC de fábrica** (H.264 a 720p, 1080p y 2160p; HEVC a 1080p y 2160p) graban de verdad con NVENC: el
  archivo dice `h264_nvenc` o `hevc_nvenc` y no salta la alarma *EncoderFallback*. La cadencia es exacta (p. ej. 926 cuadros
  en 15,45 s a 59,94), sin huecos, sin descartes del relé y sin reabrir la tarjeta. Grabar responde en 0,01 s y Detener en
  0,5–0,7 s. Los 4K escalan desde 1080i en tiempo real con este P400 de 2 GB.
- **Desentrelazado:** todos llevan `bwdif` con la paridad de la señal, `send_field` en los seis que doblan la cadencia y
  `send_frame` en el resto. Esos días la emisión estaba fuera del aire (negro, rótulo fijo y silencio) y con eso no se
  puede medir el peine, así que se comprobó con una señal sintética 1080i 4:2:2 por la misma cadena (`bwdif` → `scale` →
  `nv12` → NVENC): 121 de 121 cuadros progresivos en todos los planos. Con emisión real lo había medido la sesión del
  2026-10-15 con QuickSync; el filtro es el mismo.
- **Dos canales NVENC a la vez** (1080p29.97 ×2, 1080p59.94 ×2, HEVC 29.97 + 59.94): sin descartes, con el codificador del
  P400 al 20–57 %. Lo que limita es la CPU, ver `REQUISITOS.md`.
- **Sesiones agotadas:** con las 8 ocupadas, el canal que no encuentra sesión degrada solo a `h264_qsv`, con la alarma, y
  su archivo es válido; el que la tenía sigue en NVENC.
- **Con FFmpeg 8.0**, la batería entera: 43 de 43 (solo queda fuera la grabación larga, que se lanza aparte). Sus 21
  archivos NVENC (13 `h264_nvenc` y 8 `hevc_nvenc`: los 16 presets, los dos pares de canales a la vez y el canal que
  conserva su sesión cuando se agotan) se codificaron en la GPU, en tiempo real y sin descartes, 4K 2160p50 y 2160p59.94
  incluidos; con las sesiones agotadas, el otro canal degradó a QuickSync con su alarma. En .NET 8.0.32, unitarias 969 de
  969 e integración 105 de 109: las cuatro que fallan son del banco de pruebas, no de la app (con la 8.0, el `out_time`
  de `-progress` va unos 1,2 s por detrás del reloj y lo alcanza al cerrar, y el emisor RTMP del test, con `-re`, solo
  se recupera a unos 32 fps).

**Comprobar una compilación** (es lo mismo que sondea la app al arrancar):

```
ffmpeg -hide_banner -f lavfi -i nullsrc -frames:v 1 -c:v h264_nvenc -f null -
```

Sin errores, NVENC abre. «Required: 13.1 Found: 13.0» quiere decir que la compilación es demasiado nueva para el driver.
Con `-v verbose` sale además «Loaded Nvenc version 13.0»: es la API que admite el **driver** (13.0 en la rama R580), no
la de la compilación, y lo imprimen todas, también la que luego falla.

### NVENC de Pascal: lo que no hace (y la app no le pide)

Medido en un Quadro P400 (GP107, 2 GB, driver 582.78) con las releases del 2026-03-12 y del 2026-04-06 (mismo SDK 13.0):

- **HEVC con B-frames:** con `-bf 2` el codificador no abre («No capable devices found»). La app manda siempre `-bf 0`.
- **AV1:** «Codec not supported» (AV1 por NVENC es de Ada en adelante).
- **Lo demás que usan los presets, sí:** `-preset p5 -tune hq`, CBR y VBR, `-no-scenecut 1`, HEVC Main10 (`p010le`).
- **Sesiones:** 8 en todo el equipo, como una GeForce con driver ≥ 551.76. Abrimos 7 propias más la de RustDesk; la
  octava propia falló con «OpenEncodeSessionEx failed: incompatible client key (21)», que `FfmpegEncoderError` reconoce. Un
  escritorio remoto que codifica con NVENC (RustDesk, Parsec) gasta una sesión.
- **Capacidad del codificador** (1080p, `p5`/`hq`, cuadros sintéticos): H.264 unos 408 fps y HEVC unos 255 fps.

## Qué cambia para el desarrollo

**Nada.** `tools\ffmpeg\` sigue igual en el repositorio y `scripts\publish.ps1` sigue copiando los binarios a
`publish\` para poder probar en local. La exclusión ocurre **solo al construir el instalador**
(`Excludes: tools\ffmpeg\*` en `installer\baioss-record.iss`), así que el paquete que se entrega al cliente no
los lleva y la carpeta de trabajo del desarrollador sí.

## Alternativa si algún día se quiere volver a empaquetar

Habría que compilar FFmpeg **sin** `fdk-aac` ni `decklink` (build GPL «limpio», sí redistribuible cumpliendo la
GPL: incluir el texto de licencia y ofrecer las fuentes) y capturar las tarjetas Blackmagic por **DirectShow**,
que la app ya soporta y que Blackmagic expone como dispositivo. Queda pendiente evaluar si por esa vía se
conserva el control de formato y la latencia que da `-f decklink`. Ver `CHECKLIST-VENTA.md`.

## Audio embebido multicanal (DeckLink con 8 o 16 canales)

El demuxer `decklink` captura **solo 2 canales** si no se le pide otra cosa (`-channels`, «from 2 to 16, default 2»;
solo admite 2, 8 y 16). Hasta la fase 1 del audio multicanal (2026-09-17) la app no pedía nada, así que una señal con
4, 6, 10 o 16 canales embebidos perdía todo menos el par 1-2, y los presets 5.1/7.1 solo «subían» ese estéreo a más
canales vacíos.

Cómo funciona ahora (`AudioSelection`, parámetros de la entrada):

- `audio_channels` = `2` | `8` | `16` | `auto`. Con `auto` se pide 16 y, si la tarjeta responde «Cannot enable audio
  input», el motor baja a 8 y luego a 2 y reconstruye el proceso (`FfmpegChannelEngine.TryReduceAudioChannelsAsync`).
- `audio_pairs` = `1` | `2` | `1,2` | `all` (pares 1-based: el par 2 son los canales 3-4).
- Con más de 2 canales de fuente, el par se elige con **`pan` dentro del grafo** y se reparte con `asplit` a medidores y
  grabación; **no se usa `-ac`**: con `-ac 2` FFmpeg mezcla los ocho canales de la tarjeta en el estéreo (medido: un tono
  presente solo en el canal 5 aparecía en el canal izquierdo). Con 2 canales de fuente la tubería es la de siempre.

```
[0:a:0]asplit=2[m0][r1];[m0]ebur128=peak=true,pan=stereo|c0=c4|c1=c5,silencedetect=…[amout];[r1]pan=stereo|c0=c4|c1=c5[arec1]
-map [amout] -f null -             (medidores: ebur128 mide los 8 canales; silencedetect vigila el par que se graba)
-map [vmain] -map [arec1] -c:v …  (grabación, sin -ac)
```

Sin tarjeta se prueba con un clip de 8 canales con un tono distinto por canal (`aevalsrc … :c=7.1`) y una entrada de
archivo con `audio_channels=8`; el par grabado se verifica con `pan=mono|c0=cN,bandpass=f=<tono>,astats`.

**Asistente «Medir audio»** (`FfmpegDeviceEnumerator.MeasureAudioAsync`): abre el dispositivo unos 3 s con
`-f decklink -channels N … -vn -af astats=measure_perchannel=Peak_level -f null -` (probando 16→8→2 si se pidió
«auto») y parsea el pico por canal de la salida de `astats`; el gestor de entradas enseña el nivel de cada par y
propone el primero con sonido (salvo que ya esté elegido «Todos los pares», que la medida no deshace). Usa el MISMO
`-format_code` que la fila tenga elegido: si el operador fijó un modo SDI es porque la autodetección no le sirve, y sin
él la medida no abriría. Exige la tarjeta libre (DeckLink es exclusiva): si un canal ya la captura, no hay medida.

**Modos de pistas** (`RecordingProfile.AudioTracks`, fase 3): con una fuente de más de 2 canales, el grafo reparte el
audio con `asplit` a los medidores (todos los canales capturados) y a una rama por pista:

- `Single`: un `pan` con la distribución del perfil (estéreo del par elegido; 5.1/7.1 con los primeros seis/ocho canales elegidos).
- `PairsAsTracks`: un `pan=stereo` por par elegido y `-metadata:s:a:N title=Canales 5-6`; en MP4/MOV, además,
  `-metadata:s:a:N handler_name=Canales 5-6`. Medido con FFmpeg 8.0: en MP4/MOV el `title` se escribe como un átomo
  `udta/name` de la pista que el demuxer de FFmpeg no lee (ffprobe no lo ve) y que, por eso, el remux a faststart del
  Detener pierde; ffprobe solo veía `handler_name=SoundHandler`. El `handler_name` (el nombre del `hdlr` de la pista) sí
  lo lee ffprobe y se conserva en el remux, en fMP4 y en cada segmento. MKV y AVI guardan el `title` (el `handler_name`
  ahí no se emite: MKV lo repetiría como etiqueta `HANDLER_NAME`); MXF y TS no guardan nombre de pista.
- `Multichannel`: un `pan` con todos los canales elegidos en UNA pista PCM de distribución sin nombre («8c», «16c»):
  MXF, MKV, AVI y WAV la escriben con cualquier recuento (probado con 16).

El modo pedido se **ajusta** a lo que el códec y el contenedor llevan sin estropear el audio (auditoría 2026-09-18,
medido con el FFmpeg empaquetado; `FfmpegArgumentBuilder.AudioRoutes`):

- **Multichannel con un códec con pérdida → una pista estéreo por par** (como `PairsAsTracks`). Una pista 5.1/7.1 en
  AAC o FDK-AAC codifica el canal 4 como **LFE** (banda limitada): un tono de 1,2 kHz en ese canal salía a **−89 dB**
  (los demás, a −9 dB). FDK-AAC con «quad» rematriza a 5.0 y pierde canales; «octagonal» en AAC también los mezcla. Y
  MP2/MP3, que solo admiten estéreo, **no fallan**: FFmpeg inserta un remuestreo y MEZCLA los ocho canales en el
  estéreo en silencio. (MP4/MOV/TS promueven PCM a AAC, así que ahí «multicanal» es siempre una pista por par.)
- **Varias pistas en un contenedor de un solo flujo de audio (WAV, MP3) → una sola**: con dos `-map` de audio el muxer
  aborta («wav muxer does not support more than one stream of type audio») y no se grabaría nada. Si es PCM (WAV), todos
  los canales elegidos van en una pista multicanal; si tiene pérdida (MP3), el primer par.
- **Single con una distribución que el códec no puede llevar** (5.1 con MP2) → estéreo del primer par, por lo mismo.

`Single` con un preset 5.1/7.1 y AAC sigue tratando el canal 4 como LFE **a propósito**: ese modo declara que el SDI trae
una mezcla 5.1 real (L R C LFE Ls Rs). Con pares discretos hay que usar `PairsAsTracks` o `Multichannel`.

Los títulos de pista (`Audio_TrackTitle`: «Canales 5-6» / «Channels 5-6») van en el idioma de la aplicación; un canal
suelto de una fuente con recuento impar (NDI de 3 canales) se duplica en L/R y se titula en singular.

La carta de ajuste genera el silencio con tantos canales como la fuente (`anullsrc=channel_layout=8c`) y le aplica los
MISMOS pans, así que sus piezas llevan exactamente las mismas pistas que las reales. Verificado en vivo con un clip de 8
tonos: `Multichannel` + MP4/AAC → cuatro pistas estéreo con nombre, cada canal con SU tono a −11 dB y el siguiente a
≤ −34 dB (incluido el canal 4, el que AAC 7.1 destrozaba); `Multichannel` + MKV/PCM → una pista `pcm_s24le` de 8 canales
con los ocho tonos intactos.

**Medidores por par (fase 4).** Con una fuente de más de 2 canales, `ebur128=peak=true` va ANTES de cualquier `pan`,
sobre el flujo completo: su línea `FTPK:` trae un true-peak POR CANAL (probado con 8 y 16 canales, también con las
distribuciones sin nombre `8c`/`16c` que da el demuxer decklink), así que un solo filtro alimenta un medidor por canal.
`FfmpegMeterParser` los parsea todos (antes solo dos) y trata `-inf` —silencio digital, que .NET no parsea— como el suelo
de −60 dBFS; antes esa línea se descartaba y los medidores se quedaban congelados en el último nivel. `silencedetect` va
DESPUÉS del `pan` al primer par elegido: la alarma de silencio habla de lo que se graba, no del resto del SDI.
`ChannelStatus.Audio` (API) lleva N medidores en el orden de la fuente y `SignalInfo.AudioSelectedPairs` dice qué pares
van al archivo; la app enseña un mini medidor por par (punto rojo + negrita = se graba) junto a los L/R grandes, que
siguen al primer par que se graba, y el cliente web una rejilla por pares con la misma marca. Verificado con un clip de 8
canales a −3, −9, −15… −45 dBFS: los ocho valores llegan en orden por la API y en las dos interfaces.

**NDI multicanal (fase 5).** `NdiCaptureSource.AudioChannelCount` es el recuento real que sirve el receptor (el `-ac N`
de su entrada `f32le` describe el flujo crudo; no es la mezcla de salida). Con más de 2 canales el builder sigue el MISMO
camino que con DeckLink (pan por pares, sin `-ac` de salida, medidores de todos los canales, modos de pistas, slate con
`anullsrc=channel_layout=Nc`); antes un NDI de 4 u 8 canales se mezclaba entero en el estéreo con `-ac 2`. El par se
elige en el gestor de entradas (`audio_pairs`; no hay `audio_channels`: NDI trae los que trae y un par inexistente cae al
1). Sin una fuente NDI multicanal a mano no se ha verificado en vivo: la lógica es la común, probada con el clip de 8 canales.

**NDI sin audio y NDI entrelazado.** La entrada `f32le` del audio NDI no tiene marcas de tiempo y FFmpeg abre las entradas
en serie: con una fuente que no trae audio (salidas de grafismo, muchas cámaras PTZ) se quedaba esperando en ella, sin
preview ni grabación. El receptor sirve silencio cuando pasa medio segundo sin audio con vídeo llegando
(`NdiAudioGapFiller`): cada cuadro lleva su duración de silencio, empezando por los que ya pasaron sin audio, así el
contador de muestras sigue al de cuadros —como las marcas que FFmpeg pone a estas entradas— y el audio real, si llega,
manda. El receptor pide `allow_video_fields = true`: con `false` el SDK fundía los campos y marcaba progresivo un 1080i
(seguía entretejido: preview con doble imagen y presets progresivos sin desentrelazar). Ahora un cuadro entretejido
(`frame_format_type_interleaved`, el campo superior primero) publica `SignalInfo.Scan = InterlacedTff` y se desentrelaza
como el de la DeckLink, y los campos sueltos (`field_0`/`field_1`) se tejen en cuadros entretejidos. Probado con
`NdiSyntheticSourceTests` (emisor NDI dentro de la prueba: progresivo con y sin audio, entretejido, campos sueltos) y con
las fuentes de la red del PC de pruebas (OBS 1080p, vMix 1080i, una salida de grafismo sin audio).

**NDI «entrelazado» a 60 y fuentes con alfa.** `frame_format_type_interleaved` es el 0 de la enumeración de NDI: un emisor que
deja la estructura a cero marca así sus cuadros progresivos. La salida de grafismo de Offside Engine manda un 1080 BGRA
«interleaved» a 60000/1000, que la interfaz enseñaba como «1920×1080 · 120i · NDI» y el canal desentrelazaba cuadro a cuadro.
Un entrelazado no pasa de 30 cuadros (60 campos) por segundo, así que el receptor solo cree la marca hasta 30 cuadros/s
(`NdiReceiver.IsInterleaved`): por encima es un progresivo («1920×1080 · 60p · NDI»); los campos sueltos y un 1080i29.97
siguen siendo entrelazados. Además, el BGRA de una fuente con alfa se pasa a UYVY en el receptor (`BgraToUyvy`: BT.709, BT.601 en
SD, rango limitado, croma por pares; el alfa fuera, ninguna salida lo graba) una vez por cuadro recibido: FFmpeg convertía desde
RGB cada cuadro servido —60 por segundo con la cadencia declarada, aunque la fuente mande ~4— y con los hilos de rebanada del
grafo esas conversiones salían carísimas. Medido con un emisor que imita a Offside, proceso del canal con preview y NVENC:
6,0 núcleos antes, 3,9 solo con la regla del barrido y 1,9 con las dos cosas. Probado con `NdiReceiverFormatTests`,
`BgraToUyvyTests` y la fila «como Offside» de `NdiSyntheticSourceTests`.

**NDI al Detener.** FFmpeg (7 y posteriores) no deja que una entrada se adelante a la salida más retrasada, y la más
retrasada es la del codificador de vídeo, que retiene cerca de un segundo (x264 `veryfast`: anticipación, cuadros B e hilos).
Con NDI el audio va en su propia entrada, así que FFmpeg lo lee por detrás del vídeo, y la «q» lo para en el acto: el audio
que aún esperaba en el socket se perdía y el archivo acababa con el audio 0,1–0,9 s más corto que el vídeo (medido con 8.0:
vídeo leído hasta 5,21 s y audio hasta 4,52–4,95 s). Al retirar un proceso que graba de una fuente NDI, el receptor le
entrega lo que ya tenía en cola y le cierra los dos sockets en el mismo instante (`NdiReceiver.EndStream`, `NdiFeed`):
FFmpeg vacía el codificador, lee el audio pendiente hasta ese instante y sale solo (EOF, código 0) en 0,1–0,25 s; si no
sale en 5 s, la «q» de siempre (`FfmpegProcessSupervisor.EndInput`). DeckLink, red y archivo traen vídeo y audio en una
sola entrada y siguen con la «q». Probado con `NdiSyntheticSourceTests` y con `SplitInputStopTests` (el mismo camino sin
el runtime NDI).
