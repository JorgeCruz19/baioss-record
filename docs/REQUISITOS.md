# Requisitos de hardware y software

Qué necesita un equipo para que Baioss Record grabe sin sobresaltos, por escenario de uso (número de canales, formato,
entradas SDI/NDI/red) y con lo que cuestan las funciones de 2026: entradas SRT/RTMP, colchón de preview, clips en
caliente y finalización de cada pieza. Los números marcados como **medidos** salen de pruebas sobre el equipo de
referencia del final del documento; los demás son estimaciones prudentes.

## Resumen rápido

| | Mínimo (1–2 canales 1080p) | Recomendado (4 canales) | 8 canales o 4K |
|---|---|---|---|
| CPU | 4 núcleos / 8 hilos (Intel Core i5 de 10.ª gen., Ryzen 5) | 8 núcleos físicos (Core i7 / Ryzen 7) | 12–16 núcleos (Core i9 / Ryzen 9 o Xeon) |
| GPU | NVIDIA con NVENC (GTX 1650 o superior) y driver ≥ 610, o codificación por CPU (x264) | NVIDIA RTX (Turing o posterior), driver ≥ 610 | RTX Ampere/Ada o RTX A-series/Quadro (sesiones NVENC sin límite) |
| RAM | 8 GB | 16 GB | 32 GB |
| Disco de grabación | SSD del sistema + disco duro CMR 7200 rpm dedicado (o SSD SATA) | SSD del sistema + disco duro CMR 7200 rpm dedicado (SSD si ProRes/DNxHR/MXF o segmentos cortos) | NVMe ≥ 2 TB o RAID de discos CMR, dedicado |
| Red | 1 GbE | 1 GbE (2,5 GbE con varias fuentes NDI) | 10 GbE con NDI a full bandwidth |
| Sistema | Windows 10 21H2 / 11 x64 | Windows 11 x64, SAI | Windows 11 x64, SAI |

## Software

- **Windows 10 (21H2 o posterior) u 11, 64 bits.** La aplicación es de escritorio (WPF, Direct3D 11 para el preview) y
  necesita una sesión interactiva: no corre como servicio. Probada en Windows 11 (26200).
- **Nada más que instalar para el motor:** el instalador lleva el runtime de .NET 8 incluido (publicación
  autocontenida, `includedFrameworks` en `Baioss.Record.App.runtimeconfig.json`), FFmpeg (compilación propia con
  libsrt, NVENC/QSV/AMF y DeckLink, en `tools/ffmpeg`) y el runtime de NDI (`Processing.NDI.Lib.x64.dll`).
- **Driver NVIDIA ≥ 610** si se graba con NVENC: el FFmpeg empaquetado usa el SDK de vídeo 13.1 y con un driver anterior
  el codificador no abre (el canal degrada solo a QSV → AMF → CPU, avisando). Con driver ≥ 530 las GeForce admiten 8
  sesiones NVENC simultáneas, justo el máximo de canales de la aplicación; las RTX A-series/Quadro no tienen límite.
- **Blackmagic Desktop Video** (el driver de la tarjeta DeckLink, con su runtime de API) para entradas SDI/HDMI.
- **Cortafuegos:** abrir el puerto de cada entrada de red que escuche (UDP para SRT, TCP para RTMP) y, solo si se usa
  el panel web desde otros equipos, el puerto de la API (5005; la API no tiene contraseña: solo en redes de confianza).
- **Antivirus:** excluir la carpeta de grabaciones y `tools\ffmpeg\ffmpeg.exe`. Un análisis en tiempo real de archivos
  de varios GB compite con la grabación por el disco, y hay antivirus que bloquean el arranque de ffmpeg (la aplicación
  lo registra como «No se pudo lanzar FFmpeg» y reintenta).
- **Energía:** plan «Alto rendimiento», suspensión e hibernación desactivadas y sin apagado de discos: la aplicación no
  impide que Windows duerma y una suspensión corta la grabación. Apagar el monitor sí es seguro.
- **Actualizaciones de Windows:** horas activas cubriendo el horario de grabación (un reinicio automático a mitad de
  una programada cierra las piezas en curso; con fMP4 no se pierde material, pero sí la continuidad).
- **Reloj:** sincronizado por NTP si se usa segmentación por reloj o programación (las piezas se cortan a la hora).
- **SAI/UPS** siempre que se grabe algo irrepetible. Desde 2026-09 el MP4 se escribe fragmentado y un corte de luz
  pierde como mucho un segundo, pero el sistema y los discos siguen sin protección.

## Hardware por función

### CPU

Cada canal usa un proceso FFmpeg que decodifica la entrada, escala el preview (640×360), mide el audio (ebur128) y
codifica la grabación. Lo que pesa:

- **Codificación por CPU (x264):** 1080p25 «veryfast» ≈ 1–1,5 núcleos por canal; 1080p50/60 el doble. Con NVENC la
  codificación pasa a la GPU y el proceso queda en ≈ 0,2–0,4 núcleos por canal (decodificación + preview + medidores).
- **Entradas de red (SRT/RTMP) y NDI:** la decodificación es por CPU. H.264 1080p ≈ 0,3–0,5 núcleos; HEVC 1080p ≈ 1;
  4K HEVC ≈ 2–3 (para 4K conviene NVENC para grabar y pocos canales). **Medido:** un núcleo decodifica H.264 1080p a
  143–215 fps, 5–7 veces el tiempo real.
- **Relevo sin corte** (Grabar/Detener en entradas de red): durante 2–3 s conviven dos procesos del canal, es decir,
  una decodificación extra transitoria.
- **Clips y finalización:** copia sin recodificar; **medido** ≈ 3 s de un núcleo por GB, a prioridad baja. Nunca la GPU.
- **Preview:** render Direct3D 11 (cualquier GPU DirectX 11) con reserva por CPU si la GPU no está.

**Medido en el equipo de referencia (4 núcleos / 8 hilos):** cuatro canales 720p25 codificando por CPU con segmentación
de 5 s, finalización de cada segmento y un clip cada 4 s durante dos minutos, sin frames perdidos ni fallos.

### GPU

- **NVENC** es lo que permite 4–8 canales 1080p con CPU modesta: una GTX 1650 Ti (**referencia**) graba 1080p H.264 a
  8 Mbps con la CPU al mínimo. HEVC y 4K cargan más el codificador; un motor NVENC de Turing/Ampere da para varios
  canales 1080p30 H.264. Para 8 canales 1080p50 o 4K, RTX de gama alta o RTX A-series.
- El clip en caliente, la finalización, el colchón de preview y el relé de red **no usan la GPU**.
- Sin NVIDIA, la aplicación usa Intel Quick Sync (QSV) o AMD AMF si los presets lo piden, y si no, x264 por CPU.

### RAM

- Base de la aplicación con 4 canales: 300–500 MB. Por canal: unos 30 MB de preview.
- **Entradas de red:** relé con pre-roll de hasta 32 MB por fuente, más el **colchón de preview** si se activa: 1 s ≈
  30 MB y hasta unos 80 MB con ráfagas; 2 s ≈ 60 MB y hasta 124 MB (**medido**, se libera al reasignar la fuente).
- 8 GB bastan para 2 canales; 16 GB para 4 con entradas de red; 32 GB para 8.

### Disco de grabación

Lo que más decide la estabilidad con varios canales. La configuración normal de un grabador es la de siempre: **SSD para
el sistema y un disco duro dedicado a las grabaciones.** Las cifras de disco duro de esta sección son estimaciones (el
equipo de referencia solo tiene NVMe); las de SSD, medidas.

Tres cargas se suman sobre el disco de grabación:

1. **Escritura continua:** la suma de bitrates. 8 Mbps = 1 MB/s por canal (3,6 GB/h); 20 Mbps = 9 GB/h; 50 Mbps = 22,5
   GB/h; 100 Mbps (ProRes/DNxHR) = 45 GB/h. Grabar es escritura secuencial, lo que un disco duro hace bien: uno de
   7200 rpm sostiene 150–250 MB/s en las pistas exteriores y 80–120 MB/s en las interiores, de sobra para 8 canales
   H.264/HEVC a 50 Mbps (50 MB/s en total).
2. **Finalización de cada pieza (MP4/MOV):** al cerrarse un segmento se reescribe como MP4 normal: **medido** lee y
   escribe 1,7 veces el tamaño de la pieza cada uno (3,4× en total), una pieza a la vez en toda la aplicación. Va con
   prioridad de CPU baja, pero no de disco: en SSD no se nota (5–10 s por un segmento de 30 min a 8 Mbps, 1,8 GB); en
   un disco duro ocupa el cabezal 40–60 s por ese mismo segmento mientras los canales siguen escribiendo, y la caché de
   escritura de Windows absorbe el bache. Con 4 canales y corte a la hora son 3–4 minutos de fondo cada media hora;
   con segmentos de pocos minutos y varios canales la contención es casi continua. Por encima de
   `Recording:FaststartMaxGB` (4 GB) la pieza queda fragmentada, reproducible pero sin duración en el Explorador: con
   bitrates altos y segmentos largos, subir el tope (más E/S) o acortar los segmentos.
3. **Clips en caliente:** copian el tramo pedido (≈ 2–3× el tamaño del clip en lecturas y escrituras) leyendo el final
   del archivo en curso, que suele seguir en la caché; un clip de 10 minutos a 8 Mbps (600 MB) tarda 2–3 s en NVMe.
   No preocupan en disco duro. MXF no admite clips en caliente (sin índice hasta cerrarse).

Recomendaciones:

- **Configuración normal: SSD para el sistema y un disco duro CMR de 7200 rpm, interno (SATA) y dedicado a las
  grabaciones.** Sirve hasta 4 canales H.264/HEVC de hasta 50 Mbps con segmentos de 30 min o más. Calcular con la
  cifra de las pistas interiores y dejar la mitad libre para finalizaciones y lecturas.
- **SSD o NVMe para las grabaciones** cuando haya varios canales ProRes/DNxHR/MXF (100 Mbps o más cada uno),
  segmentos cortos (menos de 15 min) con varios canales, o reproducción, copia o edición intensa desde el mismo disco
  mientras se graba. Para 8 canales, NVMe o un RAID de discos CMR.
- **CMR, nunca SMR:** los discos «shingled» (muchos de sobremesa y de archivo de 2–8 TB) se hunden con escritura
  sostenida. Clase vigilancia o empresarial: WD Purple / Red Plus / Ultrastar, Seagate SkyHawk / IronWolf / Exos.
- **Ni USB ni ahorro de energía:** las carcasas USB se duermen o se desconectan; el apagado de discos del plan de
  energía y la optimización de unidades de Windows (desfragmentado semanal) no deben coincidir con la grabación.
- **Disco del sistema separado** del de grabación: el registro, la base de datos y Windows no compiten con el vídeo.
- **Espacio libre:** el 10 % del volumen y nunca menos de una pieza entera más 256 MB (lo que necesita la finalización
  para escribir la copia) más el clip más largo. La aplicación avisa al 80/90/95 % y puede parar por emergencia.
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
| 2 canales SDI 1080i/p, MP4 NVENC 20 Mbps | Core i5 / Ryzen 5 | GTX 1650 Ti | 16 GB | Disco duro CMR 7200 rpm dedicado | CPU y GPU del equipo de referencia |
| 4 canales mixtos (SDI + NDI + SRT), MP4 NVENC | Core i7 / Ryzen 7 (8 núcleos) | RTX 3060 o superior | 16–32 GB | Disco duro CMR 7200 rpm dedicado; SSD si los segmentos son cortos | 2,5 GbE si hay varias NDI |
| 8 canales 1080p50, MP4/MXF 50 Mbps | Core i9 / Ryzen 9 / Xeon (12–16 núcleos) | RTX 4070 o RTX A4000 | 32 GB | NVMe ≥ 2 TB o RAID de discos CMR | 50 MB/s de escritura continua más finalizaciones; 10 GbE con NDI |
| 24/7 con programación y retención | Como el escenario correspondiente | — | +8 GB | Un 20 % de margen de espacio | SAI, NTP, horas activas de Windows, optimización de unidades fuera del horario, disco de sistema aparte |

## Equipo de referencia (donde se ha medido todo lo marcado como medido)

- Intel Core i5-10300H (4 núcleos / 8 hilos, 2,5 GHz), 24 GB de RAM, Windows 11 Home (26200).
- NVIDIA GeForce GTX 1650 Ti (4 GB, driver 610.62) e Intel UHD Graphics.
- SSD NVMe Micron 2210 de 512 GB, sistema y grabaciones en el mismo disco (no es lo recomendado); sin disco duro, así
  que las cifras de disco duro son estimadas.
- Resultados: batería de estrés completa en verde (4 canales + clips + finalización, 20 ciclos de Grabar/Detener, 5
  caídas provocadas, RTMP a ráfagas con caídas del emisor); API a 265 peticiones/s con 20 clientes y p99 ≤ 55 ms
  mientras grababa y sacaba clips; finalización de 683 MB en 1,9 s; clip de 30 s en 0,6 s.

## Qué NO cubre

- Grabar y editar en el mismo equipo al mismo tiempo (un NLE compite por CPU, GPU y disco).
- Discos de red (SMB/NAS) como destino de grabación: la latencia de una red saturada se ve como disco colgado; si es
  imprescindible, red dedicada y SSD local como destino primario.
- Máquinas virtuales sin GPU dedicada (sin NVENC ni Direct3D el preview y la codificación caen a CPU).
