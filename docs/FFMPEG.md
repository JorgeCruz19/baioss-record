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
- `PairsAsTracks`: un `pan=stereo` por par elegido y `-metadata:s:a:N title=Canales 5-6` (en MP4 aparece como `name`).
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
