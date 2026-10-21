# Requisitos de hardware y software

Qué necesita un equipo para que Baioss Record grabe sin sobresaltos, por escenario de uso (número de canales, formato,
entradas SDI/NDI/red) y con lo que cuestan las funciones de 2026: entradas SRT/RTMP, colchón de preview y relevo sin
corte al Grabar/Detener. Los números marcados como **medidos** salen de pruebas sobre los dos equipos del final del
documento (los de DeckLink, i7-8700 y Quadro P400, del equipo de pruebas DeckLink); los demás son estimaciones prudentes.

## Resumen rápido

| | Mínimo (1–2 canales 1080p) | Recomendado (4 canales) | 8 canales o 4K |
|---|---|---|---|
| CPU | 4 núcleos / 8 hilos (Intel Core i5 de 10.ª gen., Ryzen 5) | 8 núcleos físicos (Core i7 / Ryzen 7) | 12–16 núcleos (Core i9 / Ryzen 9 o Xeon) |
| GPU | NVIDIA con NVENC: Turing o posterior (GTX 1650 o superior) con driver ≥ 610, o Pascal/Maxwell (GTX 10xx, Quadro P) con la compilación de FFmpeg para driver 58x (ver Software); si no, QuickSync o CPU (x264) | NVIDIA RTX (Turing o posterior), driver ≥ 610 | RTX Ampere/Ada o RTX A-series/Quadro RTX (sesiones NVENC sin límite) |
| RAM | 8 GB | 16 GB | 32 GB |
| Disco de grabación | SSD del sistema + disco duro CMR 7200 rpm dedicado (o SSD SATA) | SSD del sistema + disco duro CMR 7200 rpm dedicado (SSD si los ProRes/DNx suman más de ≈ 45 MB/s) | NVMe ≥ 2 TB o RAID de discos CMR, dedicado |
| Red | 1 GbE | 1 GbE (2,5 GbE con varias fuentes NDI) | 10 GbE con NDI a full bandwidth |
| Sistema | Windows 10 21H2 / 11 x64 | Windows 11 x64, SAI | Windows 11 x64, SAI |

## Software

- **Windows 10 (21H2 o posterior) u 11, 64 bits.** La aplicación es de escritorio (WPF, Direct3D 11 para el preview) y
  necesita una sesión interactiva: no corre como servicio. Probada en Windows 11 (26200).
- **El instalador lleva** el runtime de .NET 8 (publicación autocontenida, `includedFrameworks` en
  `Baioss.Record.App.runtimeconfig.json`) y el runtime de NDI (`Processing.NDI.Lib.x64.dll`).
- **FFmpeg lo descarga el cliente** (la compilación con DeckLink no es redistribuible, ver `FFMPEG.md`): la de
  [ffmpeg-autobuild](https://github.com/AnimMouse/ffmpeg-autobuild/releases), archivo `…-win64-nonfree.7z` (DeckLink,
  libsrt, NVENC/QSV/AMF), o FFmpeg 8.0 estable con una NVIDIA Maxwell, Pascal o Volta (ver abajo), en `tools\ffmpeg`.
  Las instrucciones están en `FFMPEG-LEEME.txt`, en esa carpeta.
- **Driver NVIDIA** si se graba con NVENC. Lo que pide lo fija la compilación de FFmpeg, no la aplicación:
  - Las releases de ffmpeg-autobuild **desde la del 2026-06-14** usan el SDK de vídeo 13.1 y piden **driver ≥ 610**.
    Sirven para Turing y posteriores (GTX 16xx, RTX, Quadro RTX y T, RTX A-series…), que reciben ese driver.
  - **Maxwell, Pascal y Volta** (GTX 750/9xx/10xx, Titan X/Xp/V, Quadro M y P) se quedan en la rama de drivers **R580**
    (58x.xx), que llega a la API 13.0 de NVENC. Necesitan una compilación con el SDK 13.0 (driver ≥ 570): **FFmpeg 8.0**
    estable, exactamente la release del 2025-09-03 de
    [ffmpeg-stable-autobuild](https://github.com/AnimMouse/ffmpeg-stable-autobuild/releases/tag/2025-09-03-16-39-ae69ef0-8.0)
    (`ffmpeg-8.0-ae69ef0-win64-nonfree.7z`), con las mismas opciones que la de `tools\ffmpeg`, AMF incluido; probada en
    un Quadro P400 con el 582.78. También sirve la release de ffmpeg-autobuild del **2026-03-12**
    (`ffmpeg-afcde65-c6e5733-win64-nonfree.7z`), que no trae AMF. Las de ffmpeg-autobuild del 2026-03-15 al 2026-04-06
    también abren NVENC, pero dejan ilegibles los MP4/MOV de QuickSync (ver `FFMPEG.md`).
  - Kepler (GTX 600/700 salvo la 750, Quadro K; driver 470) no abre NVENC con ninguna de ellas.
  - Con un driver que no llega, NVENC no abre («Required: 13.1 Found: 13.0»): el canal degrada solo a QSV → AMF → CPU y
    avisa, y la CPU paga la codificación.
- **Sesiones NVENC:** las GeForce y las Quadro de entrada admiten **8 a la vez en todo el equipo** con driver ≥ 551.76 (con
  531.61–551.75, 5), justo el máximo de canales de la aplicación; un escritorio remoto que codifique con NVENC (RustDesk,
  Parsec) gasta una (**medido** en un Quadro P400: 7 propias + 1 de RustDesk). Las profesionales que NVIDIA da por
  «qualified» (p. ej. la Quadro RTX 4000; la lista está en la matriz de soporte de su Video Codec SDK) no tienen límite.
- **Blackmagic Desktop Video** (el driver de la tarjeta DeckLink, con su runtime de API) para entradas SDI/HDMI.
- **Cortafuegos:** abrir el puerto de cada entrada de red que escuche (UDP para SRT, TCP para RTMP) y, solo si se usa
  el panel web desde otros equipos, el puerto de la API (5005; la API no tiene contraseña: solo en redes de confianza).
- **Antivirus:** excluir la carpeta de grabaciones y `tools\ffmpeg\ffmpeg.exe`. Un análisis en tiempo real de archivos
  de varios GB compite con la grabación por el disco, y hay antivirus que bloquean el arranque de ffmpeg (la aplicación
  lo registra como «No se pudo lanzar FFmpeg» y reintenta).
- **Energía:** plan «Alto rendimiento», suspensión e hibernación desactivadas y sin apagado de discos: la aplicación no
  impide que Windows duerma y una suspensión corta la grabación. Apagar el monitor sí es seguro.
  Con el apagado de discos activo (Windows lo trae a 20 minutos), el disco de grabación se duerme si nadie escribe en él y
  la primera escritura de la siguiente grabación espera unos segundos a que despierte (visto en producción el
  2026-10-12: preview congelado, «Frames perdidos» y 2–4 s perdidos al inicio de grabaciones DeckLink). Desde
  2026-10 la grabación lleva una cola de escritura (`Recording:WriteQueueSeconds`, 10 s; necesita FFmpeg 6 o
  posterior) que absorbe esa espera sin congelar nada, pero el ajuste correcto sigue siendo «Apagar el disco duro tras:
  Nunca» (`powercfg /change disk-timeout-ac 0`).
- **Actualizaciones de Windows:** horas activas cubriendo el horario de grabación: un reinicio automático a mitad de
  una programada la corta, y la pieza MP4/MOV en curso puede quedarse sin índice, como con un corte de luz (ver SAI/UPS).
- **Reloj:** sincronizado por NTP si se usa segmentación por reloj o programación (las piezas se cortan a la hora).
- **SAI/UPS** siempre que se grabe algo irrepetible. Los MP4/MOV se escriben con el índice (`moov`) al final, al cerrar
  cada pieza, y un corte de luz deja ilegible la que se estaba escribiendo: hasta un segmento entero o, sin segmentación,
  toda la grabación (el vídeo sigue dentro, pero solo se recupera con herramientas externas). Una caída del proceso de
  grabación, que el SAI no evita, cuesta lo mismo; los segmentos cortos acotan la pérdida.

## Hardware por función

### CPU

Cada canal usa un proceso FFmpeg que decodifica la entrada, escala el preview (640×360), mide el audio (ebur128) y
codifica la grabación. Lo que pesa:

- **Codificación por CPU (x264):** 1080p25 «veryfast» ≈ 1–1,5 núcleos por canal; 1080p50/60 el doble. Con NVENC la
  codificación pasa a la GPU y el proceso queda en ≈ 0,2–0,4 núcleos por canal (decodificación + preview + medidores).
- **Señal 1080i (SDI) en un preset progresivo:** el desentrelazado (`bwdif`) y las conversiones de formato van por CPU
  aunque codifique NVENC. **Medido** con DeckLink 1080i59.94 en un Core i7-8700 (6 núcleos / 12 hilos): grabar con NVENC
  añade ≈ 1,3–1,7 núcleos lógicos por canal en 1080p29.97 y ≈ 2,4–3,1 en 1080p59.94 (un cuadro por campo), sobre ≈ 0,8 de
  captura y preview. x264 «veryfast» en 1080i con contenido real cuesta ≈ 4. Dos canales NVENC 1080p59.94 con sus
  previews dejan ese i7-8700 entre el 75 y el 99 % (9–12 de 12 núcleos lógicos): es su techo. Para más canales, presets
  1080p29.97/25 o entrelazados. Esas cifras son de antes del 2026-10-20, con el grafo de filtros de cada canal repartido en un
  hilo por CPU; desde entonces va en 2 (4 al escalar a 4K para la GPU; las señales 4K y el 4K codificado por CPU siguen igual)
  y el proceso de un canal, en el mismo equipo y con un informativo real a ritmo real, cuesta ≈ 0,34 núcleos sin grabar (antes
  ≈ 0,68), ≈ 0,8 grabando NVENC 1080p59.94 (antes ≈ 2,2), ≈ 2,9 dos canales así a la vez (antes ≈ 5,6) y ≈ 7,2 grabando x264
  1080p59.94 (antes ≈ 8,6). Son los procesos de los canales, sin el receptor de la tarjeta ni la aplicación
  (`FilterThreadsCostTests`; ver «Hilos del grafo de filtros» en [01-arquitectura](01-arquitectura.md)).
- **Entradas de red (SRT/RTMP) y NDI:** la decodificación es por CPU. H.264 1080p ≈ 0,3–0,5 núcleos; HEVC 1080p ≈ 1;
  4K HEVC ≈ 2–3 (para 4K conviene NVENC para grabar y pocos canales). **Medido:** un núcleo decodifica H.264 1080p a
  143–215 fps, 5–7 veces el tiempo real.
- **Relevo sin corte** (Grabar/Detener en entradas de red): durante 2–3 s conviven dos procesos del canal, es decir,
  una decodificación extra transitoria.
- **Preview:** render Direct3D 11 (cualquier GPU DirectX 11) con reserva por CPU si la GPU no está.

### GPU

- **NVENC** es lo que permite 4–8 canales 1080p con CPU modesta: una GTX 1650 Ti (**referencia**) graba 1080p H.264 a
  8 Mbps con la CPU al mínimo. HEVC y 4K cargan más el codificador; un motor NVENC de Turing/Ampere da para varios
  canales 1080p30 H.264. Para 8 canales 1080p50 o 4K, RTX de gama alta o RTX A-series.
- **Pascal también sirve**, con FFmpeg 8.0 estable (o la release de ffmpeg-autobuild del 2026-03-12; ver Software).
  **Medido** en un Quadro P400 (Pascal de entrada, 2 GB), con las dos: los 16 presets NVENC de fábrica graban en tiempo
  real desde una DeckLink 1080i59.94, 4K incluido, y dos canales a la vez no pierden nada (con la del 2026-03-12 ocupaban
  el 20–57 % de su codificador y unos 70 MB de memoria de vídeo por sesión 1080p). Límites de Pascal que la aplicación
  ya respeta: sin B-frames en HEVC y sin AV1, y 8 sesiones por equipo.
- El colchón de preview y el relé de red **no usan la GPU**.
- Sin NVIDIA, la aplicación usa Intel Quick Sync (QSV) o AMD AMF si los presets lo piden, y si no, x264 por CPU.

### RAM

- Base de la aplicación con 4 canales: 300–500 MB. Por canal: unos 30 MB de preview.
- **Entradas de red:** relé con pre-roll de hasta 32 MB por fuente, más el **colchón de preview** si se activa: 1 s ≈
  30 MB y hasta unos 80 MB con ráfagas; 2 s ≈ 60 MB y hasta 124 MB (**medido**, se libera al reasignar la fuente).
- 8 GB bastan para 2 canales; 16 GB para 4 con entradas de red; 32 GB para 8.

### Disco de grabación

Lo que más decide la estabilidad con varios canales. La configuración normal de un grabador es la de siempre: **SSD para
el sistema y un disco duro dedicado a las grabaciones.** Las cifras de disco duro de esta sección son estimaciones (los
dos equipos del final del documento solo tienen SSD).

Lo que la grabación pide al disco es la **escritura continua**, la suma de bitrates: 8 Mbps = 1 MB/s por canal
(3,6 GB/h); 20 Mbps = 9 GB/h; 50 Mbps = 22,5 GB/h; ProRes 422 HQ con un programa de TV, 340 Mbps = 42,5 MB/s
(≈ 150 GB/h) en 1080i59.94 y 650 Mbps = 81 MB/s (≈ 290 GB/h) en 1080p59.94. Grabar es escritura secuencial, lo que un
disco duro hace bien: uno de 7200 rpm sostiene 150–250 MB/s en las pistas exteriores y 80–120 MB/s en las interiores,
de sobra para 8 canales H.264/HEVC a 50 Mbps (50 MB/s en total), pero no para varios ProRes/DNx (ver «ProRes y DNx», más
abajo). Cerrar una pieza apenas añade nada: FFmpeg escribe su índice al final (el `moov` de los MP4/MOV), sin copiarla
ni reescribirla, así que la duración de los segmentos no cambia la carga del disco.

**ProRes y DNx** (MOV o MXF) son otra escala: salvo los ligeros, con un programa de TV escriben de 180 a más de 900
Mbps por canal, de 4 a 18 veces un H.264 de 50 Mbps, y hay que calcular con esa imagen, no con la de una prueba con
grafismo. DNx es de tasa fija, la del perfil a esa cadencia: DNxHD 220/185 y DNxHR HQ/HQX en 1080 dan 183,5 Mbps a 25
cuadros, 220 a 29,97, 367 a 50 y 440 a 59,94, y DNxHR 444, el doble. ProRes (prores_aw) es variable: con cámaras,
exteriores y rótulos llega a su presupuesto (en HQ, ≈ 11 Mbit por cuadro 1080 y ≈ 44 por cuadro 4K: ≈ 275 Mbps a 25
cuadros, ≈ 330 a 29,97, ≈ 660 a 59,94 y ≈ 1100 en 4K25) y puede pasarlo; con grafismo plano da del 25 al 40 % de eso.
**Medido** el 2026-10-20 en el equipo de pruebas DeckLink con imágenes de TV grabadas de sus tarjetas (platós,
exteriores, un informativo con grafismo); Mbps del vídeo, y MB/s y GB/h con la cifra alta (el audio suma poco: en PCM
de 24 bits, 2,3 Mbps por par):

| Preset | Vídeo, con un programa de TV | MB/s | GB/h | Canales en un disco duro de 7200 rpm |
|---|---|---|---|---|
| ProRes 422 HQ · 1080i59.94 | 330–340 Mbps (125 con grafismo plano) | 42,5 | 150 | uno |
| ProRes 422 HQ · 1080p25 y «Archive · ProRes 422 1080p» | ≈ 270 Mbps | 34 | 120 | uno (dos, sin margen) |
| ProRes 4444 · 1080p25 | 340–350 Mbps | 44 | 160 | uno |
| ProRes 4444 XQ · 1080p25 | 460–490 Mbps (135 con grafismo plano) | 61 | 220 | uno, sin margen |
| ProRes 422 HQ · 1080p59.94 | 535–650 Mbps (210 con grafismo plano) | 81 | 290 | ninguno |
| ProRes 422 HQ · 4K 2160p25 | 765–926 Mbps escalando el HD de la tarjeta; con una señal 4K, hasta su presupuesto (≈ 1100) | 116–137 | 420–490 | ninguno |
| DNxHD 220 1080i59.94 (MOV y MXF OP1A) | 220–223 Mbps | 28 | 100 | uno (dos, sin margen) |
| DNxHR HQ 1080p59.94 (MOV y MXF OP1A) y DNxHR HQX 1080p59.94 10-bit (MXF OP1A) | 440–443 Mbps | 55 | 200 | uno, sin margen |
| DNxHR 444 · 1080p25 10-bit | 367 Mbps | 46 | 165 | uno |

Los ProRes ligeros a 25 cuadros pesan menos: Proxy ≈ 45 Mbps, LT ≈ 100 y 422 ≈ 175 (6, 13 y 22 MB/s). Las cifras por
imagen y lo que piden de CPU están en [08-presets](08-presets.md): «Imagen detallada», en ProRes, y «Tiempo real», en
DNxHD/DNxHR. En el i7-8700 del equipo de pruebas, ProRes 422 HQ 1080p59.94, ProRes 422 HQ 4K25 y DNxHR HQX 1080p59.94
no llegan a tiempo real con un programa de TV, tenga el disco que tenga.

**Un disco duro de 7200 rpm**, calculado como en las recomendaciones (pistas interiores, 80–120 MB/s, y la mitad libre),
**aguanta un canal** de hasta ≈ 45 MB/s: un ProRes 422 HQ 1080i59.94 ocupa entre un tercio y la mitad de lo que el disco
da en sus pistas interiores. **Dos no:** dos ProRes 422 HQ 1080i59.94 son 85 MB/s, todo lo que da un disco lento en esas
pistas; irían bien mientras el disco está casi vacío (escribe en las pistas exteriores) y fallarían al llenarse. Dos
DNxHD 220 (55 MB/s) o dos ProRes 422 HQ 1080p25 (68), un DNxHR HQ/HQX 1080p59.94 (55) o un ProRes 4444 XQ (61) van sin
margen, y ProRes 422 HQ 1080p59.94 (81) y 4K (116–137) no caben ni solos. En capacidad, 1 TB son ≈ 6,5 h de ProRes 422
HQ 1080i59.94, ≈ 10 h de DNxHD 220 y ≈ 3,4 h de ProRes 422 HQ 1080p59.94.

Recomendaciones:

- **Configuración normal: SSD para el sistema y un disco duro CMR de 7200 rpm, interno (SATA) y dedicado a las
  grabaciones.** Sirve hasta 4 canales H.264/HEVC de hasta 50 Mbps, o un canal ProRes/DNx de hasta ≈ 45 MB/s
  (ProRes 422 HQ 1080i59.94, DNxHD 220, los de 25 cuadros salvo el 4444 XQ). Calcular con la cifra de las pistas
  interiores y dejar la mitad libre para lecturas (copias, reproducción) y baches.
- **SSD o NVMe para las grabaciones** cuando los ProRes/DNx sumen más de ≈ 45 MB/s (dos canales de los de la tabla, o
  uno de 1080p59.94, 4444 XQ o 4K), o con reproducción, copia o edición intensa desde el mismo disco mientras se graba.
  Para 8 canales, NVMe o un RAID de discos CMR. Con ProRes/DNx, un SSD que sostenga la escritura (TLC): uno QLC, una vez
  llena su caché, escribe como un disco duro.
- **CMR, nunca SMR:** los discos «shingled» (muchos de sobremesa y de archivo de 2–8 TB) se hunden con escritura
  sostenida. Clase vigilancia o empresarial: WD Purple / Red Plus / Ultrastar, Seagate SkyHawk / IronWolf / Exos.
- **Ni USB ni ahorro de energía:** las carcasas USB se duermen o se desconectan; el apagado de discos del plan de
  energía y la optimización de unidades de Windows (desfragmentado semanal) no deben coincidir con la grabación.
- **Disco del sistema separado** del de grabación: el registro, la base de datos y Windows no compiten con el vídeo.
- **Espacio libre:** el 10 % del volumen. La aplicación avisa al 80/90/95 % y, cuando quedan 2 GB por canal que graba
  en él o 3 minutos al ritmo de escritura, detiene esas grabaciones de forma ordenada para que cada pieza se cierre con
  su índice.
- Sin copias de seguridad ni sincronizaciones (OneDrive, historial de archivos) sobre la carpeta de grabación mientras
  se graba: el incidente de 2026-09-06 lo investigó como causa de latencias de disco.

### Red (SRT, RTMP y NDI)

- **1 GbE** para cualquier combinación de SRT/RTMP (un flujo son 5–50 Mbps). NDI a full bandwidth consume 125–150 Mbps
  por fuente 1080p60: con más de 4 fuentes NDI, 2,5 o 10 GbE y un switch que no sature.
- **SRT:** latencia del receptor ≥ 3–4 veces el tiempo de ida y vuelta al emisor (120 ms por defecto; en Internet,
  300–500 ms). Escuchar exige abrir el puerto UDP en el cortafuegos.
- **RTMP:** por TCP; un servidor que entrega a ráfagas se ve a saltos en el preview: activar el **colchón de preview**
  de esa fuente (1000–2000 ms; **medido** con un servidor real con paradas de 1,8 s: fluido con 2000). No afecta a la
  grabación ni a la sincronía de audio, que vienen dadas por el emisor.
- La conexión con el emisor la mantiene un receptor permanente por fuente: Grabar y Detener no la cortan, y cada
  entrada de red suma un proceso FFmpeg ligero (copia sin recodificar) al del canal.

### Entradas SDI/HDMI (DeckLink)

- Ranura PCIe libre del ancho que pida la tarjeta y su driver Desktop Video. En las tarjetas con conectores
  bidireccionales (Duo 2) revisar la configuración de entradas: con dos entradas por par se evita el bache de sincronía
  al iniciar/detener que se midió con cuatro.
- Un canal por entrada física; la tarjeta no admite dos programas abiertos sobre la misma entrada.

## Configuraciones orientativas

| Uso | CPU | GPU | RAM | Disco de grabación | Notas |
|---|---|---|---|---|---|
| 1 canal SRT/RTMP 1080p, MP4 8–12 Mbps | Core i5 / Ryzen 5 (4 núcleos) | GTX 1650 o x264 | 8 GB | Disco duro CMR dedicado o SSD SATA | Colchón de preview si el servidor va a ráfagas |
| 2 canales SDI 1080i/p, MP4 NVENC 20 Mbps | Core i5 / Ryzen 5; 6 núcleos / 12 hilos si la señal es 1080i y el preset 1080p50/59.94 | GTX 1650 Ti (o Pascal con FFmpeg 8.0 estable) | 16 GB | Disco duro CMR 7200 rpm dedicado | CPU y GPU del equipo de referencia; con 1080i el desentrelazado va por CPU (medido en el equipo DeckLink) |
| 4 canales mixtos (SDI + NDI + SRT), MP4 NVENC | Core i7 / Ryzen 7 (8 núcleos) | RTX 3060 o superior | 16–32 GB | Disco duro CMR 7200 rpm dedicado | 2,5 GbE si hay varias NDI |
| 8 canales 1080p50, MP4/MXF 50 Mbps | Core i9 / Ryzen 9 / Xeon (12–16 núcleos) | RTX 4070 o RTX A4000 | 32 GB | NVMe ≥ 2 TB o RAID de discos CMR | 50 MB/s de escritura continua; 10 GbE con NDI |
| 24/7 con programación y retención | Como el escenario correspondiente | — | +8 GB | Un 20 % de margen de espacio | SAI, NTP, horas activas de Windows, optimización de unidades fuera del horario, disco de sistema aparte |

## Equipo de referencia (donde se ha medido lo demás marcado como medido)

- Intel Core i5-10300H (4 núcleos / 8 hilos, 2,5 GHz), 24 GB de RAM, Windows 11 Home (26200).
- NVIDIA GeForce GTX 1650 Ti (4 GB, driver 610.62) e Intel UHD Graphics.
- SSD NVMe Micron 2210 de 512 GB, sistema y grabaciones en el mismo disco (no es lo recomendado); sin disco duro, así
  que las cifras de disco duro son estimadas.

## Equipo de pruebas DeckLink (medido el 2026-10-15/16)

- Dell Precision 3930 Rack: Intel Core i7-8700 (6 núcleos / 12 hilos), 8 GB de RAM, un solo SSD, Windows 11 Pro.
- NVIDIA Quadro P400 (Pascal, 2 GB, driver 582.78 de la rama R580) e Intel UHD Graphics 630 (QuickSync).
- Blackmagic DeckLink Duo 2 (Desktop Video 16.4) con dos señales 1080i59.94 de emisión.
- FFmpeg para NVENC: 8.0 estable de ffmpeg-stable-autobuild y la release de ffmpeg-autobuild del 2026-03-12 (con la de
  junio, NVENC no abre con este driver).
- Resultados, con las dos: los 16 presets NVENC en tiempo real, dos canales NVENC a la vez y la degradación a QuickSync
  con las sesiones agotadas (ver `FFMPEG.md`); cifras de CPU por canal en «CPU».

## Qué NO cubre

- Grabar y editar en el mismo equipo al mismo tiempo (un NLE compite por CPU, GPU y disco).
- Discos de red (SMB/NAS) como destino de grabación: la latencia de una red saturada se ve como disco colgado; si es
  imprescindible, red dedicada y SSD local como destino primario.
- Máquinas virtuales sin GPU dedicada (sin NVENC ni Direct3D el preview y la codificación caen a CPU).
