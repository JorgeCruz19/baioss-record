# Manual de usuario — Baioss Record

> Guía práctica para operar Baioss Record. Está escrita en lenguaje sencillo, pensada para el día a día del operador. No necesitas saber nada técnico para usar el programa.
>
> *(Hay una versión en inglés en `USER-MANUAL.md`; las dos se mantienen a la par.)*

---

## 1. ¿Qué es Baioss Record?

Baioss Record es un **grabador de vídeo profesional para varios canales a la vez**. Cada canal es como una grabadora independiente: puedes conectarle una fuente de vídeo (una tarjeta de captura SDI, una cámara USB, una señal NDI…) y grabarla, verla en vivo y programar grabaciones automáticas.

Está diseñado para funcionar **24 horas al día, los 7 días de la semana** sin vigilancia: si una fuente pierde señal, si el proceso de grabación falla o si se llena el disco, el programa reacciona solo y te avisa.

**Ideas clave:**
- Cada canal (A, B, C, D…) es independiente. Lo que le pase a uno no afecta a los demás.
- Siempre ves una **vista previa en vivo** de cada canal, esté grabando o no.
- Puedes grabar **a mano** (botón Grabar) o **de forma programada** (a una hora fija, una vez o repetida).
- El programa **se cuida solo**: vigila la señal, el disco y la salud de cada grabación.

---

## 1.1. Primer arranque: instalar FFmpeg (una sola vez)

Baioss Record usa **FFmpeg** como motor de grabación. Por motivos de licencia de ese componente no viene dentro
del instalador: hay que descargarlo y dejarlo en su carpeta. Es un momento y **solo se hace una vez**.

1. Descarga una compilación de FFmpeg para **Windows de 64 bits** (la página oficial, <https://ffmpeg.org/download.html>,
   enlaza las compilaciones mantenidas para Windows). Si vas a capturar con tarjetas **Blackmagic DeckLink**,
   asegúrate de que la compilación incluya soporte «decklink»: no todas lo traen.
2. Descomprime el archivo y busca dentro (normalmente en una carpeta `bin`) **`ffmpeg.exe`** y **`ffprobe.exe`**.
3. Copia esos **dos** archivos a la carpeta `tools\ffmpeg\` de la instalación (por defecto
   `C:\Baioss\Record\tools\ffmpeg\`). Ahí encontrarás también un archivo `FFMPEG-LEEME.txt` con estos mismos pasos.
4. Abre Baioss Record.

**¿Cómo sé si falta?** Al abrir el programa aparece un aviso que lo dice y muestra la carpeta exacta. Mientras
falte, el programa funciona en **modo de demostración**: se ve todo y puedes configurarlo, pero **no graba**.

## 1.2. Idioma de la aplicación

Baioss Record habla **español e inglés**. La primera vez elige solo el idioma de **Windows** (si tu Windows está
en cualquier variante de español, arranca en español; en cualquier otro caso, en inglés).

Puedes cambiarlo cuando quieras en **🛠 Configuración → IDIOMA**. El cambio se aplica **al instante**, sin cerrar
el programa ni interrumpir ninguna grabación, y se recuerda para los próximos arranques.

> Los archivos de registro (`logs\`) se mantienen siempre en español: están pensados para el soporte técnico.

---

## 1.3. Nota sobre NDI®

Baioss Record puede grabar fuentes **NDI®**. NDI® es una marca registrada de Vizrt NDI AB; puedes obtener más
información y sus herramientas en <https://ndi.video/>. Baioss Record no es un producto de Vizrt NDI AB.

---

## 2. La pantalla principal

Al abrir el programa ves una fila con **un panel por cada canal**. Arriba del todo está la **barra de título** con el nombre "BAIOSS RECORD" y, a la derecha, los botones para abrir las distintas configuraciones.

### 2.1. El panel de un canal

Cada panel te muestra, de arriba a abajo:

- **Cabecera:** la letra del canal (A, B…), su nombre, y el **formato de la señal** que está entrando (por ejemplo "1920×1080"). A la derecha:
  - Una etiqueta de **estado de señal**:
    - 🟢 **Verde ("LOCK" / con señal):** todo bien, hay señal estable.
    - 🟠 **Ámbar ("inestable"):** la señal llega con problemas.
    - ⚪ **Gris ("SIN SEÑAL"):** no hay señal.
  - Un distintivo rojo **"● REC"** que **parpadea mientras el canal está grabando**.

- **Cuatro cajas de métricas:**
  - **FPS OUT:** cuántos cuadros por segundo se están grabando.
  - **BITRATE:** la "cantidad de datos" del vídeo (a más bitrate, más calidad y más peso).
  - **DROPPED:** cuadros perdidos. Si sube mucho, el equipo va justo de potencia o de disco.
  - **DISCO / ESTADO:** mientras grabas, el **tiempo que queda** de espacio en disco; en reposo, el estado del canal (Inactivo, Grabando…).

- **La vista previa (el monitor):** la imagen en vivo del canal. Sobre ella verás:
  - Un **marco rojo que pulsa** cuando está grabando (como el "tally" de un plató).
  - El nombre de la **entrada activa** (la fuente conectada) abajo a la izquierda.
  - El **timecode** (contador de tiempo de grabación) grande, abajo a la derecha.
  - Si algo va mal, un **aviso** en la parte de arriba (ver *Alarmas* más abajo).

- **Franja de audio:** los **medidores de sonido** (izquierda "L" y derecha "R"), con su nivel en dBFS. Si el sonido satura, aparece un aviso rojo **"CLIP"**.

- **Botones de grabación (transporte):**
  - **● Grabar:** empieza a grabar a mano.
  - **■ Detener:** para la grabación manual (solo aparece cuando estás grabando a mano).
  - **⏏ Detener grabación automática:** aparece solo si hay una grabación **programada** en curso; sirve para saltarte *esa* grabación sin afectar a las siguientes.

- **Recuadro de programación:** muestra la grabación programada que está **en curso** ahora mismo (en verde, "EN CURSO"), o "Sin grabación en curso". El botón **🕒 Mostrar programación** abre la lista completa del día.

### 2.2. Grabar y detener a mano

1. Pulsa **● Grabar** en el canal que quieras. Empieza a grabar de inmediato y el marco del monitor se pone rojo.
2. Cuando termines, pulsa **■ Detener**.
3. Al detener, el programa te **pregunta con qué nombre guardar** la grabación. Escribe un nombre (o deja el que propone) y confirma. Si el nombre ya existe, le añade un número para no pisar nada.

> Si no pones nombre, se guarda como `Canal_fecha_hora` (por ejemplo `A_20260721_203055.mp4`).

### 2.3. El indicador de almacenamiento (la pastilla 💾)

Arriba, junto al nombre del programa, hay una **pastilla con el espacio de disco**: por ejemplo `💾 250 GB · 78%`. Cambia de color según la salud del disco:
- **Normal:** hay espacio de sobra.
- 🟠 **Ámbar (aviso):** el disco empieza a llenarse.
- 🔴 **Rojo (crítico/emergencia):** queda muy poco espacio. Además aparece una **franja roja** muy visible en toda la parte superior.

Esta pastilla está visible **siempre**, incluso cuando no estás grabando, para que de un vistazo sepas cómo está el disco.

---

## 3. Las entradas — botón 🎛 Entradas

Aquí decides **qué fuente de vídeo se conecta a cada canal**.

**Tipos de entrada que admite el programa:**
- **DeckLink (SDI):** tarjetas de captura profesionales Blackmagic. Es la entrada típica de broadcast.
- **Cámara / capturadora USB (DirectShow):** webcams, capturadoras HDMI-USB, etc.
- **NDI:** vídeo por red (por ejemplo, la salida NDI de OBS u otra fuente NDI de tu red).
- **Clip de demostración:** un vídeo de ejemplo que trae el programa (útil para pruebas).

**Cómo asignar una entrada a un canal:**
1. Pulsa **🔍 Detectar dispositivos** para que el programa busque las tarjetas y cámaras conectadas.
2. En la fila del canal, elige:
   - **Entrada de vídeo:** la fuente (la tarjeta o cámara).
   - **Audio (DirectShow):** el micrófono/entrada de sonido, si tu fuente lo necesita.
   - **Modo / formato (DeckLink):** la resolución y cadencia de la señal SDI (por ejemplo "1080i 59.94").
3. Pulsa **Aplicar**. El canal se reconecta a la nueva fuente en caliente (suelta la anterior y abre la nueva).

> **Nota:** No se puede cambiar la entrada de un canal **mientras está grabando**. Detén la grabación primero.

---

## 4. Los formatos de grabación — botón ⚙ Presets de grabación

Un **preset** define **cómo se graba** el vídeo: el formato del archivo, la resolución, la calidad y el sonido. En vez de configurar cosas técnicas una a una, eliges un preset ya preparado.

**La ventana tiene tres columnas:**
- **Formatos (izquierda):** categorías para filtrar (MP4, MKV, ProRes, etc.).
- **Presets (centro):** la lista de presets. Los de fábrica llevan la etiqueta "fábrica". Puedes marcar tus preferidos con la **estrella ★**.
- **Detalle (derecha):** un resumen del preset seleccionado y la opción de aplicarlo.

**Cómo poner un formato a un canal:**
1. Busca y selecciona el preset que quieras (arriba hay un buscador 🔍).
2. Abajo a la derecha, en **"Aplicar a:"**, elige el canal (A, B…).
3. Pulsa **Aplicar al canal**.

**También puedes:**
- **＋ Nuevo / ✎ Editar / ⧉ Duplicar / 🗑 Eliminar:** crear tus propios presets a partir de los existentes.
- **⭱ Importar / ⭳ Exportar:** llevarte tus presets a otro equipo o guardarlos como copia.

> El preset solo cambia **la calidad y el formato del archivo**. La carpeta donde se guarda se configura aparte (ver punto 6).

---

## 5. La programación — botón 🕒 Programación

Sirve para que el programa **grabe solo a la hora que le digas**, una vez o de forma repetida.

**Para crear una grabación programada**, rellena el formulario de abajo:
- **Canal:** qué canal grabará.
- **Repetición:** "Una vez", "Cada día", "Cada semana"…
- **Fecha:** solo si es "Una vez".
- **Hora de inicio** y **hora de fin** (horas : minutos : segundos). La duración se calcula sola.
- **Días** (solo si es semanal): marca L, M, X, J, V, S, D.
- **Segmentar cada … minutos** (opcional): parte la grabación en trozos. Cada trozo queda como un archivo completo, así que **si uno se corrompe no pierdes toda la grabación**. Muy recomendable en grabaciones largas.
- **Título:** el nombre que llevará el archivo.

Pulsa **＋ Programar** para guardarla.

**La lista de programaciones** (abajo) muestra cada tarea con su horario y su **próxima ejecución**. En cada una puedes:
- **Editar** sus datos.
- **Activar / Desactivar** (pausarla sin borrarla).
- **Eliminar**.

**Arriba** hay una sección **"HOY"** con las grabaciones del día y su estado (programada / en curso / grabada).

**Otras opciones (arriba a la derecha):**
- **⬆ Importar / ⬇ Exportar:** guardar toda la programación en un archivo (CSV para Excel o JSON como copia de seguridad) y volver a cargarla.
- **🔄 Actualizar:** refrescar la lista.

> Las grabaciones programadas se guardan con el nombre `fecha_Título` (por ejemplo `21-07-2026_Noticias.mp4`).

---

## 6. Carpeta de destino y carta de ajuste — botón 🛠 Configuración

Aquí configuras, **por cada canal**, dos cosas:

### 6.1. Carpeta de destino

Es la **carpeta donde se guardan las grabaciones** de ese canal.
- Pulsa **Examinar…** y elige la carpeta.
- La ruta que elijas **queda guardada para siempre**, hasta que la cambies. Aunque cierres y vuelvas a abrir el programa, cada canal sigue grabando en su carpeta.
- Si no configuras ninguna, se usa la carpeta por defecto (`recordings`, dentro de la carpeta del programa).

> **Consejo:** conviene una carpeta distinta por canal, o al menos tenerlo claro, porque el nombre del archivo lleva la letra del canal delante para distinguirlos.

### 6.2. Carta de ajuste al perder señal

Es una casilla: **"Carta de ajuste al perder señal (sigue grabando barras)"**.
- Si está **activada** y la fuente pierde señal en mitad de una grabación, el programa **sigue grabando** una pantalla de barras/aviso en lugar de cortar. Así el archivo no se interrumpe y queda constancia de que hubo un corte de señal.
- Si está **desactivada**, al perder señal la grabación simplemente se queda sin imagen nueva.

---

## 7. El almacenamiento — botón 🗄 Almacenamiento

Aquí controlas **qué pasa con el espacio del disco** a largo plazo. Todos los cambios se aplican **sin reiniciar**.

> **Seguro por defecto:** el programa **no borra nada** a menos que TÚ actives la retención o la auto-limpieza.

### 7.1. Retención automática

Sirve para **borrar (o archivar) las grabaciones viejas** y no quedarte sin espacio.
- **Activar retención automática:** la casilla que enciende todo esto.
- **Conservar (días):** borra lo que tenga más de X días. `0` = no borrar por antigüedad.
- **Mantener libre al menos (GB):** si el disco baja de esos GB libres, borra las más antiguas hasta recuperar espacio. `0` = desactivado.
- **Mantener libre al menos (%):** igual que lo anterior, pero en porcentaje. `0` = desactivado.
- **Revisar cada (minutos):** cada cuánto comprueba (mínimo 5).
- **Archivar en otra carpeta en vez de borrar:** en lugar de borrar, mueve las grabaciones viejas a otra carpeta (pulsa **Examinar…** para elegirla).

### 7.2. Alertas por espacio y modo emergencia

- **Aviso (% ocupado):** a partir de ese porcentaje, la pastilla de disco se pone en **ámbar**.
- **Crítico (% ocupado):** se pone en **rojo**.
- **Emergencia (% ocupado):** el nivel más grave (rojo + franja de aviso).
- **Auto-limpiar al entrar en emergencia:** si se enciende, cuando el disco llega a emergencia el programa **borra automáticamente lo más antiguo que NO esté protegido**.
- **Bloquear el inicio de nuevas grabaciones durante la emergencia:** impide arrancar grabaciones nuevas mientras el disco esté en emergencia (las que ya están en curso siguen).

> Los umbrales se ordenan solos: aviso ≤ crítico ≤ emergencia. Poner `0` desactiva ese umbral.

Recuerda pulsar **Guardar**.

---

## 8. El historial de grabaciones — botón 🗂 Grabaciones

Es la lista de **todo lo que has grabado**. Sirve para consultar, proteger y localizar archivos.

**Arriba** puedes filtrar por **rango de fechas** y por **canal**, y ves un resumen (cuántas grabaciones y cuánto ocupan).

**Cada fila** muestra: canal, **nombre del archivo**, fecha, hora, duración, tamaño, operador y el estado de **protección**.

**Botones de cada fila:**
- **🔒 Proteger:** marca la grabación como **protegida** (chip verde). Nunca la borrará la limpieza automática.
- **★ Importante:** la marca como importante (chip ámbar). También queda excluida de la limpieza.
- **○ Normal:** le quita la protección (vuelve a entrar en la limpieza automática).
- **📂:** abre la carpeta donde está el archivo.

> **Usa "Proteger" o "Importante"** en las grabaciones que no quieres perder nunca. La limpieza automática (punto 7) respeta siempre esas marcas.

---

## 9. Conceptos útiles (glosario sencillo)

- **Canal:** una grabadora independiente (A, B, C…). Cada una tiene su fuente, su formato y su carpeta.
- **Entrada / fuente:** de dónde viene el vídeo (tarjeta SDI, cámara, NDI…).
- **Preset:** la "receta" de calidad y formato con la que se graba.
- **Señal (LOCK):** que hay imagen entrando de forma estable. Verde = bien; ámbar = con problemas; gris = sin señal.
- **Carta de ajuste (barras):** la pantalla de aviso que se graba cuando se pierde la señal (si activaste esa opción).
- **Segmentar:** partir una grabación larga en varios archivos, para más seguridad.
- **Timecode:** el contador de tiempo de la grabación en curso.
- **Grabación 24/7:** modo de funcionamiento continuo, con vigilancia y recuperación automática.
- **Protección (Protegida / Importante / Normal):** marca que decide si una grabación puede borrarse en la limpieza automática o no.

### Alarmas que puedes ver en el monitor
El programa te avisa con un cartel sobre la vista previa cuando detecta:
- **Negro:** la imagen se ha quedado en negro.
- **Congelado:** la imagen no cambia (se ha "quedado pillada").
- **Silencio:** no hay sonido.
- **Carta de ajuste:** se está grabando la pantalla de aviso por pérdida de señal.
- **Disco:** queda poco espacio.

---

## 10. Consejos y preguntas frecuentes

**¿Cómo empiezo a grabar un canal desde cero?**
1. **🎛 Entradas** → asigna la fuente al canal y pulsa Aplicar.
2. **⚙ Presets de grabación** → elige el formato/calidad y aplícalo al canal.
3. **🛠 Configuración** → elige la carpeta de destino del canal.
4. En la pantalla principal, pulsa **● Grabar** (o programa la grabación en **🕒 Programación**).

**¿Se pierde la carpeta de destino al reiniciar?**
No. Una vez la eliges, **queda guardada** hasta que la cambies.

**¿El programa borra mis grabaciones solo?**
Solo si activas la **retención** o la **auto-limpieza** en 🗄 Almacenamiento. Por defecto no borra nada. Y **nunca** borra lo que marques como **Protegida** o **Importante**.

**¿Qué pasa si se cae la señal en mitad de una grabación?**
El archivo **no se pierde**. Si activaste la carta de ajuste, sigue grabando una pantalla de aviso; en cualquier caso el programa vigila y se recupera solo cuando la señal vuelve.

**¿Puedo grabar varios canales a la vez?**
Sí, ese es el objetivo. Cada canal graba de forma independiente.

**¿Por qué no puedo cambiar la entrada de un canal?**
Porque está grabando. Detén la grabación y podrás reasignarla.

---

*Baioss Record — grabación broadcast multicanal. Este manual describe el uso operativo del programa.*
