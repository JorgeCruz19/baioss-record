# Baioss Record · Cliente web

Cliente web **sencillo** de Baioss Record: React + TypeScript (Vite), [TanStack React Query](https://tanstack.com/query),
Axios y [MUI](https://mui.com). Habla con la API REST de la aplicación (por defecto `http://127.0.0.1:5005/api/v1`) y con
sus WebSocket (`/ws/events`, `/ws/preview`). **A qué Record se conecta (IP y puerto) se elige en el propio panel**, sin
`.env` ni recompilar: ver [Conexión con el Record](#conexión-con-el-record-ip-y-puerto).

## Qué hace

| Pantalla | Qué muestra / permite |
|---|---|
| **Canales** | **Vista previa de baja resolución** de cada canal (se puede apagar) y su estado en vivo (grabando/inactivo, **preset de grabación vigente** en un badge, señal, cronómetro de grabación, FPS, bitrate, cuadros perdidos, alarmas, audio, disco) y **Grabar / Detener**. Al detener se puede **poner nombre al archivo** (mismo nombre sugerido que la aplicación; vacío = nombre automático; las programadas no lo piden). Con una fuente de 8/16 canales, los medidores se agrupan por pares y el par que se graba lleva la marca roja (`signal.audioSelectedPairs`). El nombre de operador queda en la auditoría (origen «API»). Si el canal tiene una **tarea automática en marcha**, la tarjeta la enseña (título, horario, lo que queda) y «Detener» sobre ella la **salta** solo esa vez (`POST /channels/{id}/schedule/skip`). |
| **Programación** | Tareas automáticas de grabación de cada canal (`GET/POST/PUT/DELETE /schedule`): bloque **Hoy**, una tarjeta por canal con **Añadir**, crear/editar en un diálogo (las reglas las aplica el Record —las mismas que su ventana— y el panel las traduce por `code` y marca el campo), pausar/reanudar, borrar con confirmación. Horas en **24 h**, siempre las del equipo que graba (aviso si el navegador está en otra zona). |
| **Grabaciones** | Historial (`GET /recordings`) con filtros por canal y periodo; origen (manual/programada/API), motivo de fin, tamaño, archivos; cambiar la **protección** (Normal / Importante / Protegida) frente a la limpieza automática. |
| **Actividad** | Registro de auditoría (`GET /events`) con filtros por periodo, canal, severidad mínima y categoría; cada fila se despliega con el detalle (payload). |
| **Almacenamiento** | Estado de cada disco de grabación y los ajustes de retención/alertas (`GET/PUT /storage/settings`), editables en caliente. |

La barra lateral (un cajón en el móvil) lleva la navegación y, abajo, el disco más lleno con su medidor, el resumen de la
licencia, **a qué Record está conectado el panel** (con el estado del canal en vivo; al pulsarlo se cambia la IP y el
puerto) y el selector de tema.

## Vista previa de baja resolución

Cada tarjeta de canal muestra la imagen del canal. La aplicación la **empuja por WebSocket**
(`/ws/preview/{id}?w=320&fps=5`): cada mensaje binario es un JPEG completo de 320 px, al ritmo que elija el operador en
la cabecera de **Canales** (se recuerda en el navegador):

| Ritmo | Red por canal (medido, carta de barras) | Para qué |
|---|---|---|
| 1 imagen/s · mínimo | ~42 kbps | Comprobar que entra la imagen correcta gastando casi nada |
| 5 imágenes/s (por defecto) | ~220 kbps | Seguimiento normal |
| 10 imágenes/s · fluido | ~435 kbps | Ver movimiento |

Con vídeo real las imágenes pesarán más que la carta de barras (estimación sin medir: del orden de 2 a 3 veces; la
etiqueta sobre la imagen da la cifra real de cada canal). En cualquier caso, muy lejos de
los ~184 Mbps del preview crudo (BGRA sin comprimir) que usa la aplicación de escritorio por su socket local: ese flujo
no puede ir a un navegador (es TCP crudo, con un único lector) ni conviene por red.

Está pensada para gastar poco en los dos lados:

- **El servidor marca el ritmo y no encola.** Captura un cuadro cuando toca enviarlo y no pide el siguiente hasta que el
  anterior salió: a un cliente lento o con mala red le llegan menos cuadros, sin retraso acumulado, y FFmpeg ni se
  entera (el navegador nunca lee de una salida de FFmpeg, solo de la memoria de la aplicación).
- **En el navegador** (`src/hooks/useChannelPreview.ts`): se pinta en un `<canvas>` por referencia, así que un cuadro
  nuevo no re-renderiza la tarjeta; si llega un cuadro mientras se decodifica otro, solo sobrevive el último; con la
  pestaña oculta o la tarjeta fuera de pantalla se **cierra el WebSocket**; y el interruptor **Vista previa** lo apaga
  del todo. Sobre la imagen se ve lo que cuesta de verdad: ritmo recibido y kbps de ese canal.
- **En la aplicación** (`PreviewSnapshotService`): captura y codifica **solo cuando alguien pide** —sin panel abierto no
  hay suscripción al preview, ni copias, ni JPEG—; varios clientes del mismo canal comparten la captura; los buffers
  salen de un pool; y un único hilo de prioridad baja codifica, de modo que nunca compite con la grabación. Medido con
  dos canales a 10 imágenes por segundo: sin aumento apreciable de CPU frente a tenerla apagada.

Si el WebSocket no abre **pero el Record sí responde por HTTP** (un proxy que no deja pasar WebSocket), el cliente recurre
solo al sondeo de `GET /channels/{id}/preview.jpg?w=320` (la etiqueta lo indica con «HTTP»; máx. 2 imágenes/s) y cada
30 s prueba a volver al WebSocket. Con el Record apagado o reiniciándose NO cambia de transporte: sigue llamando al
WebSocket y engancha en cuanto vuelve (antes, reiniciar el Record dejaba el panel en sondeo HTTP hasta recargar la
página). Una tarjeta solo conecta si lleva 250 ms a la vista: pasar por ella haciendo scroll no abre nada. Si el canal no
tiene imagen (canal simulado, entrada reasignándose) la aplicación envía el texto `unavailable` y la tarjeta lo dice
mientras sigue esperando.

## Diseño

Interfaz sobria y con aire: neutros cálidos, **un solo color de acento** (azul) y cuatro colores de estado reservados.

- **Tema claro y oscuro**, elegidos por separado (no es una inversión automática). Por defecto sigue al sistema; la
  elección del usuario se recuerda en el navegador (`src/components/ColorMode.tsx`, `src/theme.ts`).
- **El estado nunca va solo en el color**: siempre es un punto o icono de color + una etiqueta en tinta normal
  (`StatusTag`). El texto y las cifras no se tiñen. El punto que late queda reservado para «grabando».
- **Medidores** (`Meter`: audio, disco): el relleno lleva la severidad (acento → aviso → crítico) y la pista es el mismo
  tono aclarado; siempre acompañados del valor escrito.
- **Una acción principal por tarjeta**: el botón es «Grabar» o «Detener» según el estado. Detener pide confirmación
  diciendo qué va a pasar, y los botones nombran la acción («Seguir grabando» / «Detener grabación»).
- **Actividad en frases**, agrupada por día: cada entrada se redacta a partir de los datos del evento («00:02:03 · 1
  archivo · 121 MB · Detenida por API») y se despliega con sus campos ya formateados, en vez del volcado técnico.
- **Estados vacíos y sin conexión** que dicen qué pasa y qué hacer; esqueletos solo en la primera carga (nunca al
  refrescar); barra de «cambios sin guardar» en Almacenamiento.
- Tipografía **Inter** autoalojada (`@fontsource-variable/inter`, sin CDN). Se respeta `prefers-reduced-motion`.

## Idioma

El panel está en **español e inglés**, como la aplicación. La primera vez sigue el idioma del navegador (español si es
cualquier variante de español; si no, inglés); el selector ES/EN de la barra lateral lo cambia al instante y se recuerda
en el navegador (`localStorage`, clave `baioss.lang`). Todo texto pasa por `t()` (`src/i18n/`): `es.ts` define las
claves y `en.ts` tiene que tener exactamente las mismas (lo comprueba el compilador), con huecos `{nombre}`. Las cifras y
las fechas las formatea `Intl` con el locale del idioma (`es-ES` / `en-GB`, este último por su reloj de 24 h y sus fechas
día/mes), y los selectores de fecha y hora cargan el locale de dayjs y los textos de MUI X. Lo que viene del Record (nombre
de la entrada, resumen del preset, resumen de la licencia, mensajes de error de su API) llega en el idioma de la aplicación.

## Ejecutar

Requisitos: Node 18+ y la aplicación **Baioss Record abierta en este mismo equipo** (es quien expone la API).

```bash
cd web
npm install
npm run dev        # http://localhost:5173
```

`npm run build` genera `dist/`; `npm run preview` sirve esa build (http://localhost:4173) con el mismo proxy.
Si el 5173 ya lo usa otro proyecto, `PORT=5180 npm run dev` lo cambia (es también como el panel del navegador de Claude
Code le asigna un puerto libre: `.claude/launch.json` lleva `autoPort`).

## Conexión con el Record (IP y puerto)

Se elige **en el navegador, en marcha**: abajo en la barra lateral, la línea **«Record · …»** abre el diálogo
**Conexión con el Record** (también sale el botón «Cambiar conexión» en la pantalla de «Sin conexión»).

| Opción | Qué hace |
|---|---|
| **Este servidor** (por defecto) | Las peticiones van al mismo origen que sirve el panel, que las reenvía al Record (el proxy de Vite → `127.0.0.1:5005`). No hay nada que configurar ni en el panel ni en el Record. |
| **Otro equipo** | El navegador habla **directo** con `http://IP:puerto` (REST y WebSocket). Admite una IP, un nombre, `IP:puerto` o una URL pegada. **Probar conexión** lo comprueba antes de guardar y, si falla, dice qué revisar. |

Se guarda en ese navegador (`localStorage`, clave `baioss.connection`) y se aplica **en el acto**: consultas, WebSocket de
eventos y vistas previas se rehacen contra la dirección nueva, sin recargar. Implementación: `src/api/connection.ts`
(almacén reactivo), `src/api/client.ts` (la base se lee en cada petición) y `src/components/ConnectionDialog.tsx`.

Para «Otro equipo», el **Record** tiene que permitirlo (una vez, y reiniciarlo): **🛠 Configuración → Panel web y API**

1. **Permitir conexiones desde otros equipos de la red** (si el panel se abre en otro ordenador) y el **puerto**.
2. **Webs que pueden conectarse**: la dirección desde la que se abre ESTE panel en el navegador, tal cual sale en la
   barra de direcciones (p. ej. `http://192.168.1.50:5173`), o `*`. Sin esto el navegador bloquea las respuestas (CORS)
   aunque la red funcione: es el fallo típico, y «Probar conexión» lo dice con la dirección exacta que hay que permitir.

Notas:

- **La API no tiene contraseña**: abrirla a la red deja ver y manejar los canales a cualquiera que llegue al puerto.
  Solo en redes de confianza. Detalle en `docs/06-api-seguridad.md`.
- **Panel servido por https + Record por http no funciona**: el navegador bloquea el contenido mixto (el diálogo avisa).
  Sirve el panel por http o pon el Record detrás de un proxy TLS.
- `VITE_API_BASE_URL` (compilación, ver `.env.example`) queda solo como **valor de partida** para quien empaquete el
  panel apuntando a un Record fijo; lo que se elija en el diálogo manda sobre él.
- El proxy de Vite (`dev` y `preview`) sigue reenviando `/api` y `/ws` a `127.0.0.1:5005` para «Este servidor»;
  `BAIOSS_API=http://otra-ip:5005 npm run dev` cambia su destino.

## Notas de la API que condicionan el cliente

- Los enums de .NET llegan como **enteros** en `/channels` y `/storage/*` (ver `src/api/types.ts`); en `/recordings` y
  `/events` la API ya los proyecta a texto.
- `POST /channels/{id}/recording/start` exige un `profileId`, pero el canal graba siempre con el perfil vigente elegido en
  la aplicación de escritorio (el motor ignora ese id): el cliente envía el GUID vacío.
- Los mensajes del WebSocket no llevan el tipo de evento; el cliente los usa solo como señal de «algo cambió» para
  refrescar las consultas. Sin WebSocket, React Query sondea (canales cada 1 s, listas cada 10 s).
- `POST …/recording/stop` admite `{ name, operator }` y contesta `{ renamed, pending, fileName, detail }` (204 sin nombre).
  `pending` = el archivo aún se está optimizando y la aplicación lo renombrará al acabar: el aviso lo dice así. El campo
  del diálogo quita al vuelo lo que Windows no admite en un nombre (`\ / : * ? " < > |` y `%`).
- La API no tiene autenticación (pendiente en la aplicación); el cliente tampoco.
- Al cerrarse, el Record **corta** los WebSocket (no espera al cliente): el panel reconecta solo cuando vuelve.
- Las horas de la programación son de **pared** del equipo que graba, sin zona (`"20:00:00"`, `"2026-09-21T20:00:00"`):
  se pintan tal cual, nunca se convierten a la zona del navegador (`src/api/schedule.ts`). La fecha y las horas se eligen
  con `DatePicker`/`TimePicker` de MUI X (`@mui/x-date-pickers` + dayjs), en 24 h y con segundos como en la aplicación,
  con el locale del idioma del panel.
- React Query 5 devuelve una consulta SIN datos a «pendiente» en cada reintento: `useFirstLoad` (`hooks/queries.ts`)
  conserva el último error para que la pantalla de «Sin conexión» no parpadee con los esqueletos cada segundo.

## Estructura

```
web/
├─ vite.config.ts        # proxy /api y /ws → 127.0.0.1:5005
├─ public/favicon.svg
└─ src/
   ├─ theme.ts           # tokens de color (claro/oscuro), tipografía y estilos de los componentes MUI
   ├─ i18n/              # idioma (es/en): diccionarios tipados, t(), detección/selección y locale de los pickers
   ├─ api/               # conexión con el Record (IP/puerto en marcha), tipos de la API, cliente Axios, funciones por
   │                     # endpoint, formateadores y frases de la actividad
   ├─ hooks/             # hooks de React Query (consultas/mutaciones) y el WebSocket de eventos
   ├─ components/        # layout con barra lateral, diálogo de conexión, tarjeta de canal, piezas comunes
   │                     # (StatusTag, Meter…), tema, avisos
   └─ pages/             # Canales, Programación, Grabaciones, Actividad, Almacenamiento
```
