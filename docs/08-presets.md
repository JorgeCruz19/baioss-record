# 08 · Presets de grabación / encoding

Sistema de **presets** (formatos y perfiles preconfigurados) inspirado en Marsis Ingest: el
operador elige un formato profesional sin conocer los parámetros técnicos de FFmpeg. Cada
preset define la receta completa y **genera automáticamente su línea de comandos**.

## Interfaz de 3 paneles

Se abre desde la barra de título (**⚙ Presets de grabación**) → ventana
[`PresetManagerWindow`](../src/Baioss.Record.App/Presets/PresetManagerWindow.xaml).

```mermaid
flowchart LR
    L["Panel izquierdo<br/>formatos / categorías"] --> C["Panel central<br/>presets del formato<br/>(★ favorito · búsqueda)"]
    C --> R["Panel derecho<br/>detalle<br/>+ aplicar al canal"]
```

- **Izquierda** — formatos: Todos, ★ Favoritos, MPEG-2, H.264, H.265/HEVC, DNxHD/DNxHR,
  ProRes, XDCAM, MXF OP1A, AVI, MKV, Audio, Streaming, Proxy, Archive.
- **Centro** — presets de la categoría, filtrados por la **búsqueda rápida**; estrella de favorito
  por fila; etiqueta "fábrica" en los built-in.
- **Derecha** — todos los parámetros del preset y un selector de **Canal A/B** con el botón *Aplicar*.
  La **línea de comandos FFmpeg** generada está oculta de fábrica; se muestra poniendo a `true`
  `UiFeatures.ShowFfmpegCommandLine` ([`UiFeatures.cs`](../src/Baioss.Record.App/UiFeatures.cs)) y recompilando.

Barra de herramientas: **Nuevo · Editar · Duplicar · Eliminar · Importar · Exportar**.

## Parámetros que define cada preset

| Parámetro | Campo | Mapeo FFmpeg |
|-----------|-------|--------------|
| Contenedor | `Container` | muxer `-f` (mp4/mov/mxf/mkv/mpegts/avi/mpeg/wav/mp3) |
| Códec de video | `VideoCodec` | `-c:v` (libx264, libx265, hevc/h264/av1_nvenc, prores_aw, dnxhd, mpeg2video) |
| Códec de audio | `AudioCodec` | `-c:a` (aac, pcm_s24le, libfdk_aac, libopus, libmp3lame, mp2) |
| Resolución | `Width`/`Height` | filtro `scale=W:H` (vacío = nativa) |
| FPS | `FrameRateNum/Den` | `-r N/D` (vacío = la de la fuente) |
| Bitrate | `VideoBitrateMbps` | `-b:v` (en DNxHD clásico elige el CID; ProRes/DNxHR lo ignoran) |
| Max bitrate | `MaxBitrateMbps` | `-maxrate` + `-bufsize` (VBV) |
| GOP | `GopSize` | `-g` |
| Pixel Format | `PixelFormat` | `-pix_fmt` (yuv420p, yuv422p, *p10le; Auto = el del códec) |
| Audio Sample Rate | `AudioSampleRate` | `-ar` |
| Audio Channels | `AudioLayout` | `-ac` (1/2/6/8) |
| Audio Bitrate | `AudioBitrateKbps` | `-b:a` |
| Field Order | `ScanType` | `setfield`/`-field_order` (tt/bb) + `-flags +ilme+ildct` (x264, MPEG-2) o `+ildct` (ProRes, DNxHD) |
| Control de tasa | `RateControl` | CBR / VBR / CRF-CQ |

> El campo `AudioOnly` produce comandos sin video (`-vn`) para los presets de la categoría Audio.

## Catálogo de fábrica (94 presets)

[`PresetCatalog`](../src/Baioss.Record.Application/Presets/PresetCatalog.cs) — todos verificados
grabando material real (ver más abajo).

| Categoría | Presets |
|-----------|---------|
| **MPEG-2** (6) | PS SD PAL · PS SD NTSC 480i59.94 · TS HD 1080i25 · TS HD 1080i59.94 · TS SD PAL · TS SD NTSC 480i59.94 |
| **H.264** (26) | **CPU (libx264):** HD 720p50 · 720p59.94 · FullHD 1080p23.98 · 1080p25 · 1080p29.97 · 1080p50 · 1080p59.94 · 1080p60 · 4K UHD 2160p50 · 2160p59.94 · **entrelazado:** 1080i25 MP4 · 1080i25 TS · 1080i59.94 MP4 · 1080i59.94 TS · SD PAL 576i25 · SD NTSC 480i59.94 · **GPU NVIDIA (NVENC):** 720p50 · 720p59.94 · 1080p23.98 · 1080p25 · 1080p29.97 · 1080p50 · 1080p59.94 · 1080p60 · 4K 2160p50 · 4K 2160p59.94 |
| **H.265/HEVC** (10) | **CPU (libx265):** FullHD 1080p25 · 1080p59.94 · 4K UHD 2160p50 10-bit · 2160p59.94 10-bit · **GPU NVIDIA (NVENC, 8 bits):** 1080p25 · 1080p29.97 · 1080p50 · 1080p59.94 · 4K 2160p50 · 4K 2160p59.94 |
| **DNxHD/DNxHR** (9) | **DNxHR (progresivo):** LB 1080p25 (offline) · SQ 1080p25 · HQ 1080p25 · HQ 1080p59.94 (MOV) · HQX 1080p25 10-bit · 444 1080p25 10-bit · **DNxHD (entrelazado):** 185 1080i25 (MXF) · 185 1080i25 (MOV) · 220 1080i59.94 (MOV) |
| **ProRes** (10) | 422 Proxy 1080p25 · 422 LT 1080p25 · 422 1080p25 · 422 HQ 1080p25 · 4444 1080p25 · 4444 XQ 1080p25 · 422 HQ 4K 2160p25 · 422 HQ 1080p59.94 · 422 HQ 1080i25 · 422 HQ 1080i59.94 |
| **XDCAM** (4) | HD422 1080i25 50 Mbps · HD 1080i25 35 Mbps · HD422 1080i59.94 50 Mbps · HD 1080i59.94 35 Mbps |
| **MXF OP1A** (14) | XDCAM HD422 50 (1080i25) · DNxHR HQ 1080p25 · **59.94 (NTSC):** XDCAM HD422 1080i59.94 · 1080p59.94 · 1080p29.97 · 1080p23.98 · 720p59.94 · 1080p60 · DNxHD 220 1080i59.94 · DNxHR HQ 1080p59.94 · DNxHR HQX 1080p59.94 10-bit · **50 Hz (PAL):** XDCAM HD422 1080p50 · 720p50 · DNxHR HQ 1080p50 |
| **AVI** (2) | H.264 1080p25 · H.264 1080i25 |
| **MKV** (3) | H.264 1080p25 · H.264 1080i25 · HEVC 4K 10-bit |
| **Audio** (3) | WAV PCM 48k 24-bit · AAC 256k (M4A) · MP3 320k |
| **Proxy** (2) | H.264 540p · H.264 360p |
| **Archive** (2) | HEVC 1080p CRF20 · ProRes 422 1080p |
| **Streaming** (3) | IPTV H.264 720p TS (UDP) · RTMP H.264 1080p · SRT H.264 1080p |

### Presets por GPU NVIDIA (NVENC)

Los **H.264 NVENC** y **HEVC NVENC** graban con el codificador de la tarjeta NVIDIA y dejan libre la CPU; el resto de
H.264/HEVC de fábrica codifica por CPU (libx264/libx265). Los H.264 NVENC son los gemelos por GPU de los H.264 de CPU:
misma resolución, cadencia, bitrate y GOP (lo comprueba `PresetCatalogTests`); todos en MP4 + AAC.

- **Solo progresivos:** NVENC no codifica entrelazado. Para 1080i/576i/480i siguen los de x264, MPEG-2, ProRes y DNxHD
  clásico (DNxHR no: el codificador de FFmpeg no admite entrelazado en sus perfiles; ver más abajo).
- **Señal entrelazada con un preset progresivo** (una DeckLink en 1080i59.94 grabando con un NVENC 1080p): la rama de
  grabación **desentrelaza** con `bwdif` antes de escalar, con el orden de campos que la tarjeta da al abrir
  (`SignalInfo.Scan`; el relé en crudo no conserva la marca en los cuadros). Si el preset dobla la cadencia (1080i59.94 →
  1080p59.94) sale un cuadro por campo. Antes se grababan los campos entretejidos en cuadros progresivos: peine en todo lo
  que se mueve.
- **8 bits:** NVENC pide `p010` para 10 bits y no acepta el `yuv420p10le` del catálogo, así que los HEVC 4K de 10 bits
  siguen siendo de CPU.
- **Píxel Auto:** el motor le entrega a NVENC `nv12`, su formato nativo, al final de la rama de grabación.
- **Driver:** lo fija la compilación de FFmpeg, que descarga el cliente (no va en el instalador). Las de SDK 13.1
  (ffmpeg-autobuild desde la del 2026-06-14, como la de `tools\ffmpeg`) piden **≥ 610**, que Turing y posteriores sí
  reciben. Maxwell, Pascal y Volta no pasan de la rama R580: usan FFmpeg 8.0 estable, de ffmpeg-stable-autobuild (SDK
  13.0, **≥ 570**; también sirve la de ffmpeg-autobuild del 2026-03-12), y no una de ffmpeg-autobuild del 2026-03-15 al
  2026-04-06, que con imagen fija o negro deja ilegibles los MP4/MOV de QuickSync. Ver [FFMPEG.md](FFMPEG.md).
- **Sin NVIDIA**, o con un driver más antiguo que el que exige la compilación de FFmpeg, el canal **degrada solo**
  (`EncoderFallbackChain`, alarma *EncoderFallback*): H.264 → QuickSync → AMF → CPU (libx264); HEVC → QuickSync
  (`hevc_qsv`) → CPU (libx265). El escalón QuickSync de HEVC importa: libx265 no llega a 1080p29.97 en tiempo real en un
  Core i7-8700 (medido con una DeckLink: 267 cuadros perdidos en 15 s), la GPU integrada sí. Lo permite el píxel Auto:
  cada codificador recibe su formato nativo, porque un `yuv420p` fijo haría fallar a `h264_qsv`. Lo cubre
  `BuildLive_CatalogNvencPresets_EveryFallbackStepGetsItsNativePixelFormat`.
- **No todo es un codificador que no abre** (`RecordingFailureDetector`): si el contenedor rechaza lo que pide el preset
  (FFmpeg no puede escribir la cabecera: AAC en MXF, PCM de 24 bits en MPEG-PS, ProRes en MP4…) o no abre un codificador
  que no es el de vídeo (el audio con un bitrate que no admite), degradar no arregla nada y una pieza nueva fallaría
  igual: la grabación se **detiene sola** con la alarma roja *RecordingFailed* (hasta la próxima grabación), el motivo en
  la auditoría (`EncoderFailed`) y la parada por error, sin dejar el archivo vacío; el preview sigue. Igual cuando el
  codificador de vídeo no abre y ya no hay a qué degradar. Antes el «Could not open encoder before EOF» del rawvideo del
  preview se tomaba por el codificador de vídeo, se degradaba en vano y, al llegar a CPU, el canal se quedaba «Grabando»
  sin proceso ni alarma. Lo cubren `MuxerRefusalTests` (FFmpeg real) y `RecordingFailureDetectorTests`.
- En la ventana de presets, escribir **NVENC** en el buscador los lista todos.

```text
# H.264 NVENC · FullHD 1080p29.97
ffmpeg ... -filter_complex "[0:v]scale=1920:1080[mainout]" -map "[mainout]" -map 0:a? \
  -c:v h264_nvenc -preset p5 -tune hq -rc cbr -b:v 16000000 -g 50 -bf 0 -no-scenecut 1 \
  -c:a aac -ar 48000 -b:a 256000 -ac 2 -r 30000/1001 ... -f mp4 -y <salida>

# HEVC NVENC · 4K UHD 2160p59.94
ffmpeg ... -filter_complex "[0:v]scale=3840:2160[mainout]" ... \
  -c:v hevc_nvenc -preset p5 -tune hq -rc vbr -b:v 45000000 -maxrate 60000000 -bufsize 120000000 -g 50 -bf 0 ...
```

### DNxHD clásico y DNxHR (Avid)

- **DNxHR** (`VideoCodec.DnxHr`): por perfil (`-profile:v dnxhr_lb/sq/hq/hqx/444`), cualquier resolución y **solo
  progresivo**: el `dnxhd` de FFmpeg rechaza la DCT entrelazada en sus perfiles («Interlaced encoding is not supported for
  DNxHR profiles»). Los antiguos presets «DNxHR HQ · 1080i…» guardaban los dos campos en cuadros progresivos (el MXF los
  declaraba progresivos; medido con una DeckLink en 1080i59.94).
- **DNxHD clásico** (`VideoCodec.DnxHd`): un CID fijo por formato (tamaño, barrido, 8/10 bits) y **el bitrate elige el
  CID**. FFmpeg busca la clave en la lista del CID sin mirar la cadencia; los Mbps reales son el tamaño de cuadro × la
  cadencia, por eso Avid llama distinto al mismo CID en 59.94i y en 50i:

  | 1920×1080i | a 29.97 (59.94i) | a 25 (50i) | CID | píxel |
  |------------|------------------|------------|-----|-------|
  | DNxHD 145 / 120 | 145 Mbps | 121 Mbps | 1242 | `yuv422p` |
  | DNxHD 220 / 185 | 220 Mbps | 183,5 Mbps | 1243 | `yuv422p` |
  | DNxHD 220x / 185x | 220 Mbps | 183,5 Mbps | 1241 | `yuv422p10le` |

  El builder emite `-profile:v dnxhd -b:v <clave> -flags +ildct` tras un `setfield`: cuadros entrelazados TFF de verdad.
  Comprobado con la DeckLink: en MXF `field_order tt`; en MOV el muxer de FFmpeg declara el entrelazado de DNxHD en el
  átomo `ARES` de Avid y no en `fiel`, así que ffprobe dice `unknown`, pero el cuadro decodificado es entrelazado TFF.
- **En AVI, con `-tag:v AVdn`** (el FourCC de Avid). Sin él, FFmpeg escribe el DNxHD clásico con FourCC 0 y el archivo se
  relee como `rawvideo`: ilegible y sin error. Con él se relee como `dnxhd` (medido con FFmpeg 8.0 y con el incluido, en
  archivo único, en segmentos y por `tee`). DNxHR no lo necesita: ya sale con `AVdh`.
- **A prueba de bitrates libres** (`FfmpegArgumentBuilder.DnxhdBitrateMbps`, con los 13 CID que el FFmpeg empaquetado
  acepta sin `-strict experimental`): una clave válida se respeta tal cual; cualquier otra —con FFmpeg sería «video
  parameters incompatible with DNxHD» y la grabación no arrancaría— se ajusta al CID cuyos Mbps reales a esa cadencia
  quedan más cerca, y sin bitrate se toma el CID de más calidad. Un DNxHD sin CID clásico (4K, SD, 4:4:4 —el `dnxhd` de
  FFmpeg no codifica DNxHD 444— o tamaño desconocido) graba DNxHR por perfil, lo que hacía antes. Detalle: FFmpeg no
  acepta «220» para el CID 1235 (1080p de 10 bits) aunque dé 220 Mbps a 29.97; el builder pide 185.
- **Un DNxHR entrelazado se graba como su DNxHD.** Los canales que aplicaron los antiguos presets guardan su copia del
  perfil (DNxHR entrelazado), igual que cualquier preset propio: el motor los graba como el DNxHD equivalente —SQ →
  145/120, HQ → 220/185, HQX → 220x/185x— sin que nadie vuelva a aplicar el preset (el badge del canal mantiene el nombre
  antiguo hasta entonces). LB y 444 no tienen equivalente entrelazado (no hay DNxHD 1080i por debajo de 145 ni DNxHD 444
  en FFmpeg) y siguen en DNxHR progresivo.
- **Presets renombrados (2026-10-16):** «DNxHR HQ · 1080i25 (MXF)» → «DNxHD 185 · 1080i25 (MXF)», «DNxHR HQ · 1080i25
  (MOV)» → «DNxHD 185 · 1080i25 (MOV)», «DNxHR HQ · 1080i59.94 (MOV)» → «DNxHD 220 · 1080i59.94 (MOV)» y «MXF OP1A · DNxHR
  HQ 1080i59.94» → «MXF OP1A · DNxHD 220 1080i59.94». Conservan el GUID del nombre anterior (`Make(..., formerName:)`), así
  que un favorito sigue en su sitio, y la misma clase de bitrate que DNxHR HQ (CID 1243, 917 504 bytes por cuadro).

```text
# DNxHD 220 · 1080i59.94 (MOV), grabando de una DeckLink
ffmpeg ... -filter_complex "[0:v]split=2[vrec][vprev];[vrec]scale=1920:1080:interl=1,setfield=mode=tff,format=yuv422p[vmain];..." \
  -map [vmain] -map 0:a:0? -c:v dnxhd -profile:v dnxhd -b:v 220000000 -flags +ildct -pix_fmt yuv422p \
  -c:a pcm_s24le -ar 48000 -ac 2 -r 30000/1001 -field_order tt ... -f mov -y <salida>
```

### ProRes: prores_aw, en tiempo real

`VideoCodec.ProRes` graba con **prores_aw** (antes prores_ks). En el PC de referencia (Core i7-8700, 6 núcleos/12 hilos)
prores_ks no llega a tiempo real: con los 12 hilos codifica 1080i29.97 HQ a 0,8–1,2× y Proxy/LT aún más despacio, así que
todos los presets ProRes perdían cuadros en vivo (prueba con DeckLink: 149–302 cuadros perdidos en 15 s). prores_aw hace lo
mismo a 5–6×. Medido con 240 cuadros reales de TV en directo (DeckLink, 1080i29.97, `-flags +ildct`, 12 hilos):

| Perfil | prores_aw | prores_ks | prores_aw: Mbps · PSNR | prores_ks: Mbps · PSNR |
|--------|-----------|-----------|------------------------|------------------------|
| 422 Proxy | 192 fps (6,4×) | 23 fps (0,76×) | 24,6 · 44,3 dB | 23,8 · 40,3 dB |
| 422 LT | 157 fps (5,2×) | 22 fps (0,72×) | 38,0 · 52,5 dB | 45,4 · 49,9 dB |
| 422 | 149 fps (5,0×) | 26 fps (0,87×) | 50,4 · 59,1 dB | 59,3 · 53,6 dB |
| 422 HQ | 169 fps (5,6×) | 37 fps (1,24×) | 66,5 · 64,6 dB | 76,8 · 64,4 dB |
| 4444 | 90 fps (3,0×) | 19 fps (0,64×) | 83,3 · 66,7 dB | 100,8 · 71,7 dB |
| 4444 XQ | 93 fps (3,1×) | 22 fps (0,72×) | 97,3 · 70,5 dB | 105,6 · 75,4 dB |

(SSIM ≥ 0,9992 en todos salvo Proxy: 0,9950 prores_aw, 0,9910 prores_ks. Los Mbps quedan lejos de los nominales de Apple
—HQ 1080i29.97 ≈ 220— porque ProRes es VBR y la señal de TV es blanda.) El proceso de grabación completo (filtros +
preview + codificación) sobre el mismo clip, en núcleos a tiempo real: 422 HQ 1080i29.97 ≈ 2,9 (3,2× de margen); 422 HQ
1080p59.94 desde 1080i (`bwdif`, un cuadro por campo) ≈ 5 (1,7×); 4444 XQ 1080p25 ≈ 3,4 (2,5×). prores_ks HQ, solo el
codificador: ≈ 9.

- **Perfiles:** prores_aw admite los seis con su fourcc (`apco`/`apcs`/`apcn`/`apch`/`ap4h`/`ap4x`) y la DCT entrelazada
  (`-flags +ildct`). Codifica en BFF los cuadros marcados BFF y en TFF el resto, y el MOV toma el orden de la marca del
  primer cuadro (sin marca declara `bt`): de ahí el `setfield` de la rama de grabación y de la carta de ajuste, que sin él
  salía con el contenedor en BFF (y en x264, con los cuadros en BFF). Comprobado con ffprobe: cuadros
  `interlaced_frame=1`, `top_field_first=1`.
- **Croma por perfil** (`FfmpegArgumentBuilder.ProResFormat`): prores_aw no abre un 4444/XQ con 4:2:2 (EINVAL «need
  YUV444P10 input») y con 4:4:4 convierte un 422 en 4444 por su cuenta, así que el formato de píxel lo fija el perfil
  —4444/XQ `yuv444p10le`, el resto `yuv422p10le`— y no un formato suelto del preset. Auto = 422 HQ, o 4444 si el preset
  pide 4:4:4.
- **Calidad:** de Proxy a HQ, igual o mejor PSNR que prores_ks con menos bitrate; en 4444/XQ algo menos (66–70 dB frente a
  72–75), muy por encima de lo visible. prores_aw ajusta el cuantificador por slice dentro del rango de cada perfil (HQ:
  1–6) y no impone un tope de tamaño de cuadro: con material muy complejo un archivo puede pasar del bitrate nominal antes
  que perder calidad.
- **Compatibilidad:** el flujo es ProRes estándar (mismos fourcc y cabeceras; el fabricante del cuadro dice `fmpg` en vez
  del `Lavc` de prores_ks). Los dos son implementaciones de FFmpeg, no codificadores con licencia de Apple: en eso no cambia
  nada. Verificado aquí que FFmpeg/ffprobe leen perfil y entrelazado; **no** se ha podido abrir en Final Cut, Premiere ni
  Avid (este PC no los tiene). prores_aw es el que FFmpeg registra también con el nombre genérico `prores`.
- **Descartado:** prores_ks con cuantificador fijo (`-qscale:v`) también es rápido (6,7× en HQ), pero un cuantificador fijo
  ignora la tasa del perfil (un Proxy dejaría de pesar como un Proxy) y el tamaño dependería solo del contenido.
- **Límite: 4K.** «ProRes 422 HQ · 4K 2160p25» no llega a tiempo real en el i7-8700 ni con prores_aw: desde una señal 1080i
  (desentrelazar + escalar a 4K) va a 0,73×, y solo codificar 4K HQ a 25 cuadros pide unos 6 núcleos, los seis físicos de
  esa CPU. Hace falta un procesador con más núcleos (y la Duo 2, 3G-SDI, no recibe 4K para probarlo con una fuente real).

```text
# ProRes 422 HQ · 1080i59.94
ffmpeg ... "[vrec]scale=1920:1080:interl=1,setfield=mode=tff,format=yuv422p10le[vmain]" ... \
  -map [vmain] -c:v prores_aw -profile:v 3 -flags +ildct -pix_fmt yuv422p10le ... -r 30000/1001 -field_order tt -f mov ...
```

### Presets entrelazados: cada campo por su lado

Un preset entrelazado (1080i, 576i, 480i) graba los dos campos de cada cuadro, cada uno de su instante. La rama de grabación
(`FfmpegArgumentBuilder.BuildLive`) los trata por separado en tres casos que antes los estropeaban (medido el 2026-10-18 con
`idet` en la imagen y en el croma de lo grabado, con `FactoryPresetRecordingTests`):

- **Escalar y convertir el croma, por campos (`scale=…:interl=1`).** Sin ello, el escalado vertical mezclaba los dos campos
  (un «H.264 · SD PAL» o un «MPEG-2 · SD PAL» desde el 1080i59.94 de la DeckLink salía sin campos, con el movimiento doblado)
  y, aun sin cambiar de tamaño, la conversión del croma de 4:2:2 (uyvy422 de la tarjeta) a 4:2:0 promediaba el de los dos
  campos: color fantasma en todo lo que se mueve en los 1080i de H.264, MPEG-2 TS y XDCAM HD 35. Los 4:2:2 (XDCAM HD422,
  ProRes, DNxHD) no cambian de croma; van igual.
- **Fuente con el otro orden de campos (`fieldorder`).** La NTSC de la DeckLink va con el campo inferior primero y los presets
  480i con el superior: cambiar solo la marca (`setfield`) los reproducía en el orden inverso, el movimiento a saltos atrás y
  adelante. `fieldorder` desplaza la imagen una línea; como no admite croma submuestreado en vertical, la conversión al formato
  del codificador va detrás, otra vez por campos.
- **Fuente progresiva al doble de cadencia (`interlace`).** Un 1080p59.94 (un emisor NDI) con un preset 1080i29.97, o un
  1080p50 con uno 1080i25: `-r` tiraba un cuadro de cada dos y marcaba los demás como entrelazados, con los dos campos del mismo
  instante (la mitad del movimiento). Ahora cada cuadro grabado lleva un campo de un cuadro de la fuente y el otro del
  siguiente. Con una fuente con huecos (NDI) el relleno va delante, a la cadencia de la fuente, como con `bwdif` por campo.
- **Solo donde swscale puede:** escalar por campos a un destino 4:2:0 de alto no múltiplo de 4 (486, 574, 578, 1082) hace caer a
  FFmpeg (segfault, medido con FFmpeg 8.0): ahí el motor convierte como antes (`CanScaleByFields`). El validador ya no acepta
  un entrelazado 4:2:0 a esos altos; queda la NTSC de 486 líneas grabada a su tamaño.
- **El aviso de cadencia** («CADENCIA ⚠ 59,94 → 29,97») ya no sale al pasar de campos a cuadros (1080i29.97 → 1080p59.94, un cuadro
  por campo) ni al tejerlos (1080p59.94 → 1080i29.97): no se descarta ni se repite nada (`FrameRateConversion.ByFields`), y la
  alarma de frames perdidos no descuenta descartes que no hay.

```text
# MPEG-2 TS · SD NTSC 480i59.94, grabando la NTSC de una DeckLink (720×486, campo inferior primero)
ffmpeg ... "[vrec]scale=720:480:interl=1,setfield=mode=bff,fieldorder=tff,scale=interl=1,format=yuv420p[vmain]" ...

# XDCAM HD422 · 1080i59.94 50 Mbps, grabando un 1080p59.94 por NDI (el relleno de los huecos, delante)
ffmpeg ... "[vrec]fps=60000/1001,scale=1920:1080,interlace=scan=tff,scale=interl=1,setfield=mode=tff,format=yuv422p[vmain]" ...
```

## Línea de comandos generada (ejemplos reales)

[`FfmpegCommandPreview`](../src/Baioss.Record.Engine.FFmpeg/FfmpegCommandPreview.cs) produce
exactamente lo que ejecutará el motor, con entrada/salida simbólicas:

```text
# H.264 Broadcast · FullHD 1080p25
ffmpeg -hide_banner -progress pipe:1 -stats_period 1 -i <entrada> \
  -filter_complex "[0:v]scale=1920:1080[mainout]" -map "[mainout]" -map 0:a? \
  -c:v libx264 -preset veryfast -b:v 16000000 -maxrate 16000000 -bufsize 40000000 -g 50 \
  -pix_fmt yuv420p -c:a aac -ar 48000 -b:a 256000 -ac 2 -r 25/1 \
  -af ebur128=peak=true -movflags +faststart -f mp4 -y <salida>

# XDCAM HD422 · 1080i25 50 Mbps (MXF OP1A)
ffmpeg ... -filter_complex "[0:v]scale=1920:1080:interl=1,setfield=mode=tff[mainout]" ... \
  -c:v mpeg2video -b:v 50000000 -minrate 50000000 -maxrate 50000000 -bufsize 100000000 -g 12 \
  -flags +ilme+ildct -pix_fmt yuv422p -c:a pcm_s24le -ar 48000 -ac 2 -field_order tt -f mxf -y <salida>

# MXF · XDCAM HD422 1080p59.94 50 (60000/1001 progresivo)
ffmpeg ... -filter_complex "[0:v]scale=1920:1080[mainout]" -map "[mainout]" -map 0:a? \
  -c:v mpeg2video -b:v 50000000 -minrate 50000000 -maxrate 50000000 -bufsize 100000000 -g 15 \
  -pix_fmt yuv422p -c:a pcm_s24le -ar 48000 -ac 2 -r 60000/1001 -f mxf -y <salida>

# Audio · WAV PCM 48k 24-bit (solo audio)
ffmpeg ... -i <entrada> -vn -map 0:a? -c:a pcm_s24le -ar 48000 -ac 2 -af ebur128=peak=true -f wav -y <salida>
```

## Arquitectura

```mermaid
flowchart TD
    CAT["PresetCatalog<br/>(94 built-in)"] --> STORE["IPresetStore / JsonPresetStore<br/>built-in + custom (JSON)"]
    STORE --> VM["PresetManagerViewModel<br/>filtrar · CRUD · favoritos · import/export"]
    VM -->|ToProfile| PROF["RecordingProfile"]
    PROF --> BLD["FfmpegArgumentBuilder<br/>línea de comandos / argv"]
    VM -->|Aplicar| CH["IConfigurableRecording.Profile<br/>(Canal A/B)"]
```

- [`EncodingPreset`](../src/Baioss.Record.Application/Presets/EncodingPreset.cs) — modelo plano,
  serializable a JSON; `ToProfile()` lo traduce al `RecordingProfile` de dominio.
- [`JsonPresetStore`](../src/Baioss.Record.Infrastructure/Presets/JsonPresetStore.cs) — combina los
  built-in (solo lectura) con los personalizados; persiste estos y los favoritos en
  `data/presets.json` (junto a la base de datos).
- Al **Aplicar**, el preset inyecta un `RecordingProfile` completo en el canal vía
  `IConfigurableRecording.Profile`, conservando la identidad persistida del canal.

## CRUD, favoritos e import/export

- **Nuevo / Editar / Duplicar / Eliminar** — los built-in son de solo lectura; para personalizarlos
  se **duplican** (quedan como custom editables). El editor es
  [`PresetEditorWindow`](../src/Baioss.Record.App/Presets/PresetEditorWindow.xaml).
- **Favoritos** — la estrella marca cualquier preset (incluido un built-in); se persiste.
- **Importar / Exportar JSON** — intercambio entre equipos. Estructura exportada:

```json
[
  {
    "Name": "H.264 Broadcast · FullHD 1080p25",
    "Category": "H264",
    "Container": "Mp4",
    "VideoCodec": "H264x264",
    "Width": 1920, "Height": 1080,
    "FrameRateNum": 25, "FrameRateDen": 1,
    "VideoBitrateMbps": 16, "MaxBitrateMbps": 20, "GopSize": 50,
    "PixelFormat": "Yuv420p", "ScanType": "Progressive", "RateControl": "ConstantBitrate",
    "AudioCodec": "Aac", "AudioLayout": "Stereo", "AudioSampleRate": 48000, "AudioBitrateKbps": 256
  }
]
```

Los enums se serializan como texto (estables ante reordenamientos). Al importar, cada preset
recibe una identidad nueva como personalizado.

### Presets que no podrían grabar (contenedor × códec)

[`RecordingProfileValidator`](../src/Baioss.Record.Application/Recording/RecordingProfileValidator.cs) rechaza un perfil que
FFmpeg no puede grabar: bitrate o GOP a cero, resolución impar… un **contenedor que no lleva el códec de vídeo o el de
audio** y una **frecuencia de muestreo** que el audio o el contenedor no admiten. Lo aplican el editor (al pulsar
**Aceptar**: lista los motivos y no cierra), el gestor (no **aplica** a un canal un preset propio o importado que lo
incumpla), el panel del canal (un **⚠ rojo** en el badge PRESET, con el motivo en el tooltip, si el perfil guardado del canal
no puede grabar) y el pre-vuelo de Grabar (manual, programada o por la API: aviso y auditoría «No se pudo iniciar», sin
sesión ni archivo). Los mensajes van en el idioma de la aplicación y proponen contenedores (para el vídeo), el audio que
lleva el contenedor o las frecuencias que graban.

Medido el 2026-10-18 con FFmpeg 8.0 y con el incluido (git-2026-06-14), iguales: 2 s de cada códec en cada contenedor,
releídos con `ffprobe` y una decodificación entera. *Rechaza* = el muxer no abre la salida y la grabación no arranca;
*ilegible* = FFmpeg escribe el archivo **sin error**, pero no se puede leer.

| Familia | AVI | MXF | MP4 | MOV | MKV | TS | PS |
|---|---|---|---|---|---|---|---|
| **HEVC** (x265, QuickSync, NVENC) | ilegible (FourCC 0 → `rawvideo`) | rechaza | ✓ | ✓ | ✓ | ✓ | ✓ |
| **AV1** (NVENC) | ✓ (`AV01`) | rechaza | ✓ | rechaza («only supported in MP4») | ✓ | ilegible (datos privados) | ilegible |
| **ProRes** | ✓ | ✓ | rechaza | ✓ | ✓ | ilegible (datos privados) | ilegible |
| **DNxHD/DNxHR** | ✓ (`AVdn`, `AVdh`) | ✓ | rechaza | ✓ | ✓ | ilegible (datos privados) | ilegible |
| **H.264**, **MPEG-2** | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |

- WAV y MP3 solo llevan audio: con vídeo el muxer rechaza la pista, así que piden **Solo audio**.
- HEVC degrada dentro de HEVC (NVENC → QuickSync → libx265), así que un HEVC en AVI o MXF fallaba en cualquier equipo. AV1
  solo lo codifica av1_nvenc (NVIDIA Ada): sin ella el canal degrada a libx264 y graba H.264, pero en una Ada el mismo
  preset no graba.
- Lo cubren `RecordingProfileValidatorTests` (la matriz entera) y, con el FFmpeg real, `ContainerCodecTests` (sin la regla
  FFmpeg no deja vídeo legible; con ella el canal rechaza el perfil; lo que el mensaje propone graba).

**Audio.** Medido el 2026-10-18 con el motor real (`FfmpegChannelEngine` y el argv del producto) y FFmpeg 8.0 y el incluido:
4 s de un tono con cada códec en cada contenedor, con vídeo y en solo audio, releídos con `ffprobe` y decodificados enteros.
Los dos FFmpeg dan lo mismo salvo FDK-AAC en AVI y WAV (¹).

| Audio | AVI | MXF | MP4 | MOV | MKV | TS | PS | WAV | MP3 |
|---|---|---|---|---|---|---|---|---|---|
| **PCM** (24 bits) | ✓ | ✓ (con vídeo) | ✓ (como AAC) | ✓ (²) | ✓ | ✓ (como AAC) | rechaza | ✓ | rechaza |
| **AAC** | ✓ | rechaza | ✓ | ✓ | ✓ | ✓ | rechaza | ilegible | rechaza |
| **FDK-AAC** | rechaza (¹) | rechaza | ✓ | ✓ | ✓ | ✓ | rechaza | rechaza (¹) | rechaza |
| **Opus** | rechaza | rechaza | ✓ | rechaza | ✓ | ✓ | rechaza | rechaza | rechaza |
| **MP3** | ✓ | rechaza | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| **MP2** | ✓ | rechaza | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | rechaza |

- El muxer MXF solo lleva PCM (pasa el audio por `pcm_rechunk`) y necesita una pista de vídeo, la primera: en **Solo audio**
  no graba con ningún códec. El de PS solo admite MP1/MP2/MP3, AC-3, DTS y PCM de 16 bits; el MP3, solo MP3.
- PCM en MP4 y TS se graba como AAC (`FfmpegCodecMap.EffectiveAudioEncoder`): en MP4 FFmpeg 8.0 lo escribiría como `ipcm`
  (ISO/IEC 23003-5), que casi ningún editor lee, y en TS haría falta SMPTE 302M. En PS el motor no lo promueve.
- (²) **PCM en MOV va tal cual** desde el 2026-10-21. Antes también pasaba a AAC (una regla del primer día, sin medida
  detrás), y los presets ProRes y DNx en MOV, que dicen «MOV, PCM», grababan AAC. Medido ese día con FFmpeg 8.0 y con el
  incluido: el muxer escribe el audio QuickTime `in24` (descripción de sonido v1 con `enda`, little-endian, y `chan` con la
  distribución), o `lpcm` v2 por encima de 65535 Hz. Con más de 2 canales en una pista el incluido escribe `lpcm` v2, como
  pide la especificación QuickTime (commit de FFmpeg 9e71ea2d60, 2026-05-19), y 8.0 sigue con `in24` v1, que esa
  especificación no admite para más de 2 canales: FFmpeg la relee igual, y si un editor no la abre quedan MXF, MKV o
  *PairsAsTracks* (el modo de pistas no cambia solo por eso). ffprobe lo ve como `pcm_s24le (in24)` de 24 bits, con el
  nombre de cada pista. Las muestras de 24 bits salen idénticas a las de la fuente con ProRes 422 HQ y en solo audio:
  estéreo, mono, 5.1, 7.1, una pista de 16 canales y 8 pistas estéreo, en MOV estándar, en fMP4 y en fMP4 con el remux del
  Detener, en segmentos y de 10 Hz a 768 kHz. Matando el proceso a los 6 s (fMP4), lo que queda es idéntico a la fuente
  salvo el último fragmento a medias, como con AAC. Ocupa 2,3 Mb/s por par (18,4 con 16 canales), poco al lado del vídeo de
  estos presets. Lo cubren `FfmpegArgumentBuilderTests`, `RecordingProfileValidatorTests` y, con el FFmpeg real,
  `ContainerCodecTests` (PCM en MOV, con vídeo y en solo audio) y `DecklinkHardwareTests` (los 16 canales de la tarjeta en
  una pista y en 8 pares).
- En **segmentos fMP4** el MOV fragmentado no lleva lista de edición: si el primer paquete de audio de un segmento empieza
  después del corte del vídeo, FFmpeg lo pone en 0 y alarga su duración, y queda adelantado lo que sobresale. Con AAC son
  ≤ 21 ms (paquetes de 1024 muestras); con PCM, hasta un paquete de los que llegan (≤ 33–40 ms desde una DeckLink). En MOV
  estándar (`Recording:FragmentedMp4 = false`, lo que lleva la app) la lista de edición deja el audio en su sitio.
- AAC en WAV: el WAV no delimita los paquetes y el del codificador nativo sale sin error, pero no se decodifica.
- (¹) El FDK-AAC del incluido sale en ADTS y los muxers RIFF (AVI, WAV) no lo aceptan («ADTS is only supported with codec tag
  0x1610»); FFmpeg 8.0 sí lo graba. Se rechaza en los dos: un preset no puede grabar en un equipo y en otro no.
- Sin la regla, con vídeo el muxer no escribe la cabecera, el motor lo toma por un codificador de vídeo que no abre y el canal
  se queda «Grabando» con un archivo de 0 B y sin alarma; en solo audio relanza piezas de 0 B. El mensaje propone el audio que
  el contenedor lleva con su identificador estándar: PCM en MXF y WAV; MP2 o MP3 en PS; PCM, MP3 o MP2 en AVI; AAC o PCM en
  MOV; MP3 en MP3.

**Frecuencia de muestreo.** El editor no la enseña (los presets graban a 48 kHz), pero un preset importado o editado a mano
(`AudioSampleRate` en el JSON), o el perfil guardado de un canal, puede traer otra, y el motor la pasa tal cual (`-ar`).
Medido el 2026-10-18 con el motor real y FFmpeg 8.0 y el incluido, iguales: 4 s de cada audio en cada contenedor que lo
lleva, con vídeo y en solo audio, de 7350 a 384000 Hz (y 47952 y 768000 con PCM), releídos y decodificados enteros.

| Audio (codificador) | Frecuencias que graba (Hz) |
|---|---|
| **PCM** (`pcm_s24le`) | cualquiera (medido de 10 a 768000), salvo en MXF (solo 48000) y en MP4 y TS (es AAC: las de AAC) |
| **AAC** (`aac`) | 7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000 |
| **FDK-AAC** (`libfdk_aac`) | las de AAC menos 7350 |
| **Opus** (`libopus`) | 8000, 12000, 16000, 24000, 48000 (el archivo declara 48000: Opus siempre decodifica a 48 kHz) |
| **MP3** (`libmp3lame`) | 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000; en MP4, de 16000 para arriba |
| **MP2** (`mp2`) | 16000, 22050, 24000, 32000, 44100, 48000, con un bitrate de la tabla de esa frecuencia |

- Con otra frecuencia el codificador no abre («Specified sample rate 44100 is not supported by the libopus encoder»). El muxer
  MXF solo implementa 48 kHz («only 48khz is implemented») y el MP4 no lleva MP3 por debajo de 16 kHz («muxing mp3 at 8000hz
  is not standard»; el MOV sí).
- AAC, FDK-AAC, Opus y MP3 ajustan solos el bitrate a lo que cabe a esa frecuencia (256 kb/s a 8 kHz graban). MP2 no: solo
  abre con los kb/s de su versión MPEG, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 o 384 a 32, 44,1 y 48 kHz
  y 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144 o 160 a 16, 22,05 y 24 kHz («bitrate 256 is not allowed in mp2»):
  un MP2 a 24 kHz con los 256 kb/s de sus presets no graba, ni uno a 48 kHz con 200.
- Sin la regla pasa lo mismo que con un audio que el contenedor no lleva: con vídeo, «Grabando» con 0 B; en solo audio,
  piezas de 0 B. El mensaje propone, de menor a mayor, las frecuencias que graban con ese audio en ese contenedor o, con
  MP2, los kb/s de esa frecuencia.
- Lo cubren `RecordingProfileValidatorTests` (cada audio en cada contenedor a 17 frecuencias, y la tabla de MP2) y, con el
  FFmpeg real, `ContainerCodecTests` (una fila por causa, sin y con la regla; una por propuesta; y que las listas del
  validador son las que declara ese FFmpeg).

### Presets propios: lo demás que el editor comprueba

Además de contenedor × códec y la frecuencia de muestreo, el validador rechaza las combinaciones que FFmpeg no graba como las
pide el preset. Medido el 2026-10-18/19 con el motor real (`FfmpegChannelEngine`, sin el validador) y FFmpeg 8.0: 3 s de cada
combinación, un eje cada vez, releídos enteros con `ffprobe`, `idet` y las marcas de los cuadros decodificados
(`PresetCombinationSweepTests`, 547 combinaciones; la regla es que lo que el validador acepta graba lo que pide).

| Qué | Lo que hace FFmpeg sin la regla | La regla |
|---|---|---|
| **Cadencia** fuera de 1…120 fps (25/1001: la copia de un preset 29.97 a la que el editor solo dejaba cambiar el numerador) | graba un cuadro cada 40 s | de 1 a 120 cuadros por segundo |
| **Cadencia en MXF** que el muxer no conoce (1, 10, 12, 15, 20, 40, 150…) | no escribe la cabecera («Unsupported frame rate»): no arranca | las de su tabla: 23.976, 24, 25, 29.97, 30, 47.952, 48, 50, 59.94, 60, 71.928, 72, 75, 89.91, 90, 95.904, 96, 100, 119.88 y 120 |
| **Cadencia en MPEG-2** que no codifica (13, 26, 55…) | la cambia sin avisar por la más cercana (13 → 12) | las que declara mpeg2video |
| **Píxel** que el códec no codifica | lo cambia sin avisar: MPEG-2 a 10 bits sale en 8, ProRes/DNx en 4:2:0 salen en 4:2:2, H.264 por QuickSync solo hace 4:2:0 de 8 bits | los suyos |
| **Perfil intra de la otra familia** (ProRes 4444 XQ en un DNxHR) | colaba su 4:4:4 y FFmpeg no abría `dnxhr_hq` (también arreglado en el motor) | un perfil de su familia o Auto |
| **Perfil intra contra el píxel** (DNxHR HQ en 10 bits, HQX u 444 en 8, ProRes 4444 en 4:2:2) | DNxHR no abre (código -22); ProRes graba el del perfil | el píxel del perfil o Auto |
| **Entrelazado** con NVENC, QuickSync, AMF o x265 | los dos campos en cuadros progresivos: H.264 por NVENC los declara progresivos y se ve peine; HEVC solo lo dice el contenedor | x264, MPEG-2, ProRes o DNxHD |
| **DNx entrelazado** sin CID entrelazado (SD, 720p, 4:4:4, DNxHR LB) | campos en cuadros progresivos | 1920×1080 (8 y 10 bits) o 1440×1080 (8 bits) |
| **Entrelazado 4:2:0** con un alto que no es múltiplo de 4 (578, 486, 1082) | x264 no abre («height not divisible by 4»); escalarlo por campos hace caer a FFmpeg, así que el motor ahí lo convierte como antes, mezclando el croma | un alto múltiplo de 4 |
| **MPEG-2** a un ancho o alto múltiplo de 4096 (DCI 4K) | no abre | otro tamaño |
| **DNxHR** por debajo de 256×120 | no abre («Input dimensions too small») | 256×120 o más |
| **MP2/MP3 en 5.1 o 7.1** | el codificador no abre: no arranca | mono o estéreo |
| **Opus** por encima de 256 kb/s por canal | «The bit rate … is unsupported»: no arranca | hasta 256 kb/s por canal |

- Lo que depende de la tarjeta (NVENC en 4:2:2 o 10 bits de H.264, HEVC por QuickSync en 4:4:4, AMF sin GPU AMD, AV1 sin una
  NVIDIA Ada) no se rechaza: el canal **degrada** con la alarma *EncoderFallback* al codificador por CPU, que lo graba. Con un
  formato que no es 4:2:0 de 8 bits la degradación de H.264 se salta QuickSync y AMF (`EncoderFallbackChain.Next(codec, píxel)`):
  los dos lo cambiaban a nv12 sin avisar, y un H.264 NVENC 4:2:2 de 10 bits en la P400 acababa en un 4:2:0 de 8 bits de QuickSync.
- **Solo intra por NVENC (GOP 1):** h264_nvenc no abre con `-g 1` («Gop Length should be greater than number of B frames + 1»)
  y el canal degradaba a QuickSync; el motor lo pide con `-g 0`, que es solo intra (todos los cuadros I).
- **Campo FPS del editor:** se escribe la cadencia («25», «29.97» o «29,97», «59.94», «23.976» o la fracción «30000/1001»;
  vacío = la de la fuente) y se guarda exacta (`PresetFrameRate`). Antes era un entero que iba al numerador: un preset nuevo no
  podía ser 29.97 y en la copia de uno 29.97 el campo decía «30000».
- Lo cubren `RecordingProfileValidatorTests` (cada regla, con su mensaje), `PresetFrameRateTests` y, con el FFmpeg real,
  `PresetCombinationSweepTests` (`BAIOSS_COMBO_SWEEP=1`, ~75 min; `BAIOSS_COMBO_ONLY` con trozos de los nombres repite unas
  filas, `BAIOSS_COMBO_ACCEPTED_ONLY=1` graba solo lo que el validador acepta y `BAIOSS_COMBO_REPORT` deja el TSV). Último
  resultado (2026-10-19, P400 y sin GPU AMD): de las 547 el validador acepta 432, y graban lo que piden; 28 degradan con su
  alarma al codificador por CPU, todas por la tarjeta (AV1 y AMF, H.264 NVENC en 10 bits o 4:2:2, HEVC NVENC o QuickSync en
  4:2:2 o 4:4:4 y un HEVC NVENC de 64×64).

## Verificación

El smoke test [`tools/Baioss.Record.SmokeTest`](../tools/Baioss.Record.SmokeTest/Program.cs)
genera la línea de comandos de los 94 presets y **graba material real** con cada uno (≤ 1080p;
los 10 presets en 4K se omiten de la grabación):

```
Resultado grabación: 84 OK, 0 fallidos.
```

Cubre MPEG-2 PS/TS, H.264/265 (incl. **familia de cadencias 59.94/60/50/29.97/23.98** en MP4/TS),
**H.264/HEVC por NVENC**, DNxHR HQ/HQX, ProRes, XDCAM, **MXF OP1A (familias 59.94 y 50 Hz: 1080i/1080p/720p en XDCAM HD422 y
DNxHR)**, AVI, MKV, WAV/AAC/MP3, Proxy, Archive e IPTV/RTMP/SRT; todos producen archivos válidos
(verificados con `ffprobe`).

> Los presets **NVENC** necesitan una GPU NVIDIA en el equipo del smoke test: el smoke test usa
> `FfmpegRecorderEngine`, que no degrada de codificador (la degradación está en el motor de los canales). Último
> resultado (2026-09-24): GTX 1650 Ti con driver 610.62 y el FFmpeg empaquetado. Los 4K NVENC se verificaron en la app:
> HEVC NVENC 2160p59.94 grabó 826 frames en 13,78 s (59,94 fps), a la vez que H.264 NVENC 1080p29.97 en el otro canal.

Con una DeckLink de verdad, [`DecklinkHardwareTests`](../tests/Baioss.Record.IntegrationTests/DecklinkHardwareTests.cs)
(opcional: `BAIOSS_DECKLINK="DeckLink Duo (2)"`) graba 15 s por preset con el mismo cableado que la aplicación y comprueba
el archivo: continuidad, cadencia, cuadros entrelazados TFF **en el códec** (no solo en el contenedor) y que el relé no
descarta nada mientras graba, es decir, que el equipo da abasto en tiempo real. Para ProRes/DNxHD cubre ProRes 422 HQ
1080i59.94, 422 Proxy 1080p25, 4444 XQ 1080p25, 422 HQ 1080p59.94, DNxHD 220 1080i59.94 (MOV y MXF OP1A) y un canal con el
perfil DNxHR 1080i antiguo (`Recording_WithALegacyInterlacedDnxHrProfile_RecordsInterlacedDnxhd`). Último resultado
(2026-10-19, Core i7-8700, FFmpeg 8.0, TV en directo en Duo (1) y Duo (2), Duo (3) sin señal; main con los entrelazados por
campos, el Detener por fin de entrada y un medidor por canal): 44 de 45 (la grabación larga, omitida), sin un cuadro descartado.
Los ocho entrelazados graban TFF en el códec a 29,97. TwoInputs, dos canales a la vez: 3 de 3, con relevos de 0,4–0,8 s y el
archivo cerrado 0,17–0,48 s después, por fin de entrada. Matar el proceso de grabación deja la pieza fMP4 legible y la MP4
estándar sin índice pero avisada, y los 16 medidores marcan cada uno su canal. Aparte, en f8410d3, con idet cuadro a cuadro en la
imagen y en el croma: el croma de los entrelazados en 4:2:0 peina TFF donde peina la imagen (antes de f8410d3, en ningún cuadro),
y los progresivos bajan del 60–100 % de peine de la señal al 0–5 % del archivo. Con la emisora en un anuncio en PsF (idet
«progresivo») la prueba no juzga el peine de esa fila, y sin movimiento ni audio (fuera de emisión) idet no clasifica cuadros y
omite el peine y el nivel de audio (lo anota en la salida).

### Los 94 presets grabando en vivo

[`FactoryPresetRecordingTests`](../tests/Baioss.Record.IntegrationTests/FactoryPresetRecordingTests.cs) (opcional:
`BAIOSS_PRESET_SWEEP=1`, ~65 min) graba 10 s con cada preset de fábrica por el canal completo —el pre-vuelo del validador y el
motor unificado, con la cola de escritura y el fMP4 de la aplicación— desde una fuente en vivo, y relee lo grabado entero
(`RecordedFileCheck`, el mismo que usa el barrido de combinaciones): el códec, el perfil, el píxel, el tamaño, la cadencia, el
barrido (`idet` en la imagen y en el croma, y la marca de los cuadros decodificados) y el audio; que se decodifica entero sin
errores y con el audio tan largo como el vídeo; y que va en tiempo real (el proceso al 97 % o más, sin la alarma de frames
perdidos y sin degradar el codificador). Tres fuentes: la del formato del propio preset, la de las DeckLink de este equipo
(1080i59.94 TFF en uyvy422) y, para los 26 entrelazados, un 1080p al doble de su cadencia como el de un emisor NDI, que se teje.
`BAIOSS_PRESET_ONLY` («NVENC», «1080i59.94|ProRes») y `BAIOSS_PRESET_FEEDS` («DeckLink,Own») repiten unas filas;
`BAIOSS_PRESET_REPORT` deja una fila por preset en un TSV y `BAIOSS_PRESET_KEEP` conserva lo grabado.

Último resultado (2026-10-19, Core i7-8700 de 6 núcleos y 12 hilos, Quadro P400, FFmpeg 8.0): 201 de 214 filas. Los 94 graban
el códec, el perfil, el píxel, el tamaño, la cadencia, el barrido y el audio que dicen, y se leen enteros; los 26 entrelazados,
también desde el 1080p al doble. Lo que no llega a tiempo real son seis presets que codifica la CPU, cuya descripción lo avisa y
remite a su gemelo NVENC, y el H.264 NVENC 4K59.94 desde la DeckLink:

| Preset | Desde su formato | Desde la DeckLink (1080i59.94) |
|---|---|---|
| H.264 · 4K UHD 2160p50 (x264) | 0,55–0,63× | 0,72× |
| H.264 · 4K UHD 2160p59.94 (x264) | 0,46–0,53× | 0,42× |
| HEVC · 4K UHD 2160p50 10-bit (x265) | 0,16–0,20× | 0,19× |
| HEVC · 4K UHD 2160p59.94 10-bit (x265) | 0,15–0,17× | 0,16× |
| MKV · HEVC 4K 10-bit (x265) | 0,17–0,18× | 0,19× |
| HEVC · FullHD 1080p59.94 (x265) | 0,58–0,71× | 0,52× |
| H.264 NVENC · 4K UHD 2160p59.94 | 1,04× | 0,94×: desentrelazar y escalar a 4K van por la CPU (5,5 núcleos) |

- 2026-10-20, con el grafo de filtros en 2 hilos (4 al escalar a 4K para la GPU; ver «Hilos del grafo de filtros» en
  [01-arquitectura](01-arquitectura.md)): el H.264 NVENC 4K59.94 desde la DeckLink llega (1,06× con 3,8 núcleos; antes del
  cambio, ese mismo día, 1,06× con 5,1) y medido a ritmo real con un volcado de la tarjeta va a 1,000× con 2,7 núcleos (4,6 con el
  grafo en 12 hilos). Ojo con la velocidad de este barrido: sale del tiempo de medios que FFmpeg da a escalones de un segundo, en
  unos 7 s de cada fila, y una misma fila con la misma línea de comandos dio 0,91× en una pasada y 1,09× en otra; los presets que
  van justos dieron 19 de 50 filas a tiempo real antes del cambio y 28 después. Para márgenes pequeños, `FilterThreadsCostTests`.
- Por debajo del tiempo real el vídeo se queda corto: de 10 s grabados, entre 2,3 y 8,6 s. x264 y x265 ya van con
  `-preset veryfast`; los que pide un 4K o un 1080p59.94 por CPU necesitan más núcleos o el gemelo NVENC (el HEVC NVENC 4K59.94
  llega desde la DeckLink, 1,03×). Una DeckLink Duo 2 no entrega 4K: grabarla en 4K es escalar el HD.
- Los que más CPU piden y llegan: ProRes 422 HQ 4K25 (8,1–8,7 núcleos, 1,02–1,04×), Archive HEVC 1080p (7,7), HEVC 1080p25
  (7,4) y H.264 Broadcast 1080p59.94 (6,6 desde la DeckLink, 1,02×).
- Lo que encontró en el motor (el croma y los campos de los entrelazados, el orden de campos, el tejido de un progresivo al
  doble, el perfil intra de otra familia, la degradación según el píxel y el GOP 1 de NVENC) está arreglado y descrito arriba.

## Extender el catálogo

- **Nuevo preset de fábrica** → añadir un `Make(...)` en `PresetCatalog`.
- **Nuevo códec/contenedor** → ampliar los enums de dominio + `FfmpegCodecMap` (+ rama en
  `FfmpegArgumentBuilder` si requiere parámetros específicos).
- **Nuevo parámetro** → añadir el campo a `EncodingPreset` y `RecordingProfile`, y emitirlo en el builder.

> Nota: los presets de **Streaming** definen hoy el *encode* y el protocolo sugerido; el push real
> (SRT/RTMP/UDP por el muxer `tee`) se conecta como `StreamTarget` del perfil — ver
> [01 · Arquitectura](01-arquitectura.md) y el `BuildTeeTarget` del builder.
