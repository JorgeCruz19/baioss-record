# Manual de audio multicanal — Baioss Record

*Cómo grabar señales SDI o NDI que traen 8 o 16 canales de audio: elegir qué pares se graban, medir lo que llega,
decidir cómo se guardan las pistas y leer los medidores. Está pensado para el operador; los detalles técnicos están en
`FFMPEG.md`.*

---

## 1. Lo básico en un minuto

Una señal SDI lleva el audio **embebido** en **pares** de canales: el par 1 son los canales 1-2, el par 2 los canales
3-4, y así hasta 16 canales (8 pares). En broadcast cada par suele llevar algo distinto: el **programa** en el 1-2, el
**sonido internacional** (sin locución) en el 3-4, **idiomas** o **comentarios** en los siguientes.

Hasta ahora el programa grababa siempre el par 1-2 y descartaba el resto. Ahora **tú decides**:

| Decisión | Dónde se toma | Qué significa |
|---|---|---|
| **Cuántos canales pedir a la tarjeta** | 🎛 Entradas → *Audio de la tarjeta* | 2, 8, 16 o **Automático** (los máximos que admita la tarjeta) |
| **Qué par (o pares) se graban** | 🎛 Entradas → *Par a grabar* | Un par concreto, o **Todos los pares** |
| **Cómo se guardan en el archivo** | ⚙ Presets → *Pistas de audio* | Una pista, una pista por par, o todos los canales en una pista |

Dos cosas que conviene tener claras desde el principio:

- **La tarjeta no sabe qué trae la señal.** Se le pide un número de canales y entrega ese número; los que no existen
  llegan en silencio. Por eso un par callado no significa que no exista, y por eso la elección la haces tú (con ayuda
  del botón **🎧 Medir audio**).
- **La elección queda fija mientras se graba.** Las pistas de un archivo no pueden cambiar a mitad. Si cambias el par,
  el cambio se aplica al reconectar la entrada (botón **Aplicar**), nunca sobre una grabación en curso.

---

## 2. Requisitos

- Una tarjeta **DeckLink** con audio embebido en el SDI. La tarjeta acepta pedir **2, 8 o 16** canales: si tu señal trae
  4 o 6, pide 8 (o deja *Automático*) y los canales que no existen quedan en silencio.
- Con **NDI** los canales los fija la fuente (por ejemplo, un OBS con 2 canales o un mezclador con 8): no se le pide
  nada, solo eliges el par a grabar.
- Para **medir** el audio, la tarjeta tiene que estar **libre**: ningún canal puede estar capturándola en ese momento
  (una DeckLink solo la puede abrir un proceso a la vez).

---

## 3. Paso a paso: configurar la entrada (🎛 Entradas)

1. Pulsa **🔍 Detectar dispositivos** y, en la fila del canal, elige la **DeckLink** (o la fuente NDI) como entrada de vídeo.
2. En **Audio de la tarjeta (DeckLink)** elige cuántos canales pedir:
   - **Automático (los que admita la tarjeta)** — recomendado. Pide 16 y, si la tarjeta no puede, baja sola a 8 y a 2.
     En el panel del canal verás con cuántos se ha quedado («Par 1-2 de 8 · PCM»).
   - **2 / 8 / 16 canales** — si sabes lo que trae tu instalación y quieres fijarlo.
3. Si no sabes qué lleva cada par, pulsa **🎧 Medir audio** (con la tarjeta libre). El programa escucha unos tres
   segundos y te enseña el nivel de pico de cada par, por ejemplo:

   > Canal A: la tarjeta entrega 8 canales; con sonido: pares 1-2, 3-4 · 1-2: −8 dB · 3-4: −20 dB · 5-6: silencio · 7-8: silencio

   Además **propone el primer par con sonido** y, si estabas en *Automático*, fija el recuento que la tarjeta aceptó.
   Un par se considera en silencio por debajo de −60 dBFS. La propuesta **no se guarda** hasta que pulses **Aplicar**.
4. En **Par a grabar** elige:
   - **Par 1-2, Par 3-4, …** — se graba solo ese par (estéreo). Es lo normal cuando quieres el programa o un idioma concreto.
   - **Todos los pares** — se graban todos los canales que entrega la tarjeta. Es lo que necesitas para guardar el
     programa y el internacional en pistas separadas, o para conservar todo el audio del SDI (ver el punto 4).
5. Pulsa **Aplicar**. El canal se reconecta a la entrada con la nueva configuración y el mensaje de abajo lo confirma
   («Canal A → DeckLink … · audio: par 3-4 (Automático)»). En el panel del canal, bajo el preview, aparece lo elegido:
   «**Par 3-4 de 8 · PCM**» o «**Los 8 canales · PCM**».

> **Ojo:** al abrir la ventana de Entradas, los desplegables muestran los valores por defecto (*Automático* y *Par 1-2*),
> no lo que tiene el canal ahora mismo. Si vuelves a aplicar una entrada, vuelve a elegir el audio y el par.

---

## 4. Paso a paso: decidir las pistas del archivo (⚙ Presets de grabación)

Abre el preset que use el canal (**✎ Editar**, o **＋ Nuevo** a partir de uno existente) y fíjate en el campo
**Pistas de audio**. Solo cuenta cuando la entrada entrega más de 2 canales:

| Modo | Qué produce | Cuándo usarlo | Formatos |
|---|---|---|---|
| **Single** | Una sola pista con lo elegido. Con un par → estéreo. Con *Todos los pares* y un preset **5.1 / 7.1** → los primeros 6 u 8 canales forman esa pista. | Lo de siempre: un estéreo. O un 5.1 embebido en los canales 1-6. | Todos |
| **PairsAsTracks** | **Una pista estéreo por cada par** grabado, cada una con su nombre («Canales 1-2», «Canales 3-4»…). Nada se mezcla. | Programa + internacional + idiomas, cada uno en su pista, listo para el editor. Necesita *Todos los pares*. | Todos. Con AAC (MP4/MOV/TS) cada pista suma bitrate; con PCM (MXF/MKV) no pierde nada. |
| **Multichannel** | **Todos los canales en una sola pista** multicanal, sin compresión. | Conservar el SDI entero para postproducción. Necesita *Todos los pares*. | **Solo con PCM: MXF, MKV, AVI o WAV**, hasta 16 canales. Con un códec con pérdida (AAC en MP4/MOV/TS, Opus, MP2, MP3) el programa guarda **una pista estéreo por par**, igual que *PairsAsTracks* (ver la nota de abajo). |

> **Por qué *Multichannel* no hace una pista 7.1 en MP4.** Los códecs con pérdida tratan el **canal 4** de una pista
> 5.1/7.1 como el canal de graves (LFE) y le quitan todo lo que no sea grave: si por ese canal viene, por ejemplo, el
> lado derecho del sonido internacional, quedaría inservible (lo medimos: −89 dB). MP2 y MP3, además, solo admiten
> estéreo y mezclarían los ocho canales sin avisar. Por eso, con esos códecs, el programa guarda siempre una pista
> estéreo por par: nada se mezcla y nada se recorta.

Recomendaciones prácticas:

- Para **conservar todo sin pérdida**, usa un preset con contenedor **MXF** o **MKV** (audio PCM). En MP4/MOV/TS el
  programa pasa el audio a AAC.
- En un preset de **solo audio** a **WAV** solo cabe una pista: con *PairsAsTracks* el programa guarda todos los canales
  elegidos en una pista WAV multicanal. A **MP3** (una pista y solo estéreo) va el primer par elegido.
- Si tu SDI trae un **5.1 de verdad** en los canales 1-6, usa *Single* con un preset 5.1 (ahí el canal 4 sí es el LFE).
  Si lo que trae son pares independientes, **no** uses un preset 5.1/7.1 con AAC: usa *PairsAsTracks*.
- Si eliges **Todos los pares** con una entrada de 16 canales y *PairsAsTracks*, el archivo tendrá 8 pistas estéreo,
  también las de los pares vacíos (en silencio). Con PCM no importa; con AAC ocupa algo más.
- Los **nombres de pista** («Canales 3-4», o «Channels 3-4» con la aplicación en inglés) se ven en Premiere, Resolve,
  VLC o MediaInfo, así el montador sabe qué es cada una.
- La **carta de ajuste** (cuando se pierde la señal) genera silencio con exactamente las mismas pistas, así el archivo
  no cambia de estructura a mitad.

Después, aplica el preset al canal (**Aplicar al canal**) como con cualquier otro.

---

## 5. Leer los medidores

En la franja **AUDIO dBFS** del panel del canal:

- Los **medidores grandes L / R** muestran el **primer par que se graba**, con su valor en dBFS y el aviso rojo
  **CLIP** si satura.
- Con una entrada de 8 o 16 canales aparece a su derecha un **mini medidor por cada par** («1-2», «3-4», …) con
  **todo** lo que entrega la tarjeta:
  - **Punto rojo + etiqueta en negrita** = ese par se graba.
  - Etiqueta gris = ese par **solo se mide** (no va al archivo). Sirve para ver de un vistazo si llega sonido por un par
    que no estás grabando, por ejemplo si te has equivocado de par.
  - Al pasar el ratón, el texto lo dice en palabras («Canales 3-4: no se graban (solo se miden)»).
- La **alarma de silencio** vigila el par que se graba, no el resto: un par vacío que no grabas no dispara nada.

En el **cliente web** el bloque *Audio · 8 canales de la fuente* muestra la misma rejilla por pares, con la marca roja en
los que se graban y la leyenda «● se graba · el resto solo se mide».

---

## 6. Casos típicos

**Solo el programa (par 1-2).** No hay que tocar nada: *Automático* + *Par 1-2* + tu preset de siempre.

**El programa viene por el par 3-4.** Entradas → 🎧 Medir audio → el programa propone *Par 3-4* → Aplicar. En el panel:
«Par 3-4 de 8 · PCM».

**Programa e internacional en pistas separadas.** Entradas → *Todos los pares* → Aplicar. Preset → *Pistas de audio* =
**PairsAsTracks** (idealmente MXF/MKV con PCM). El archivo lleva «Canales 1-2», «Canales 3-4», …

**Conservar todo el audio del SDI para postproducción.** Entradas → *Todos los pares*. Preset MXF o MKV con
*Pistas de audio* = **Multichannel**: una pista PCM con los 8 o 16 canales.

**Un 5.1 embebido en los canales 1-6.** Entradas → *Todos los pares*. Preset con *Canales* = **5.1** y *Pistas de
audio* = **Single**: los canales 1-6 forman la pista 5.1 (L, R, C, LFE, Ls, Rs, en ese orden).

**Fuente NDI con 4 canales.** Entradas → fuente NDI → *Par a grabar* (*Par 1-2*, *Par 3-4* o *Todos los pares*) →
Aplicar. No hay *Audio de la tarjeta*: los canales los pone la fuente.

---

## 7. Qué pasa si…

- **La tarjeta no admite 16 canales.** Con *Automático* el programa baja solo a 8 y luego a 2 y lo anota en el registro
  de actividad. Si habías fijado 16 a mano, el canal registra el error y no captura audio: vuelve a Entradas y elige
  8 o 2.
- **«Medir audio» no encuentra sonido.** Comprueba que ningún canal esté usando la tarjeta (tiene que estar libre),
  que la señal lleve audio embebido y que el nivel supere −60 dBFS. Si la tarjeta está ocupada, el mensaje de abajo lo dice.
- **Elegiste el par 5-6 pero la tarjeta solo entrega 2 canales.** Se graba el par 1-2 (mejor grabar el par que existe
  que no grabar nada). El panel muestra «2 canales · PCM».
- **Cambiaste el par y la grabación sigue igual.** Es lo esperado: la grabación en curso no cambia. El nuevo par se
  aplica al reconectar la entrada (Aplicar) y entra en la siguiente grabación.
- **Elegiste *Multichannel* y el MP4 trae varias pistas estéreo en vez de una multicanal.** Es lo esperado con AAC (ver la
  nota del punto 4). Para una sola pista con los 8 o 16 canales usa MXF o MKV (PCM).
- **«Medir audio» no abre la tarjeta aunque está libre.** Si tu señal necesita un *Modo / formato* concreto, elígelo antes
  de medir: la medida abre la tarjeta con ese mismo modo.

---

## 8. Para soporte: lo que hay por debajo

La elección se guarda en los parámetros de la entrada (tabla `InputSources`, columna `Parameters`, JSON):

- `audio_channels` = `2` | `8` | `16` | `auto` (solo DeckLink).
- `audio_pairs` = `1` | `2` | … | `all`. Desde la ventana de Entradas se elige un par o todos; una combinación concreta
  («1,3» = canales 1-2 y 5-6) solo se puede escribir aquí, a mano.

Sin estos parámetros el canal se comporta como siempre: 2 canales, par 1-2. El detalle del grafo de FFmpeg (selección
con `pan`, medidores con `ebur128`, modos de pistas, carta de ajuste) está en `docs/FFMPEG.md`, sección «Audio embebido
multicanal».
