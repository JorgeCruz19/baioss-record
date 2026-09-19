# Baioss Record · Cliente web

Cliente web **sencillo** de Baioss Record: React + TypeScript (Vite), [TanStack React Query](https://tanstack.com/query),
Axios y [MUI](https://mui.com). Habla con la API REST local de la aplicación (`http://127.0.0.1:5005/api/v1`) y con su
WebSocket de eventos (`/ws/events`).

## Qué hace

| Pantalla | Qué muestra / permite |
|---|---|
| **Canales** | **Vista previa de baja resolución** de cada canal (se puede apagar) y su estado en vivo (grabando/inactivo, **preset de grabación vigente** en un badge, señal, cronómetro de grabación, FPS, bitrate, cuadros perdidos, alarmas, audio, disco) y **Grabar / Detener**. Con una fuente de 8/16 canales, los medidores se agrupan por pares y el par que se graba lleva la marca roja (`signal.audioSelectedPairs`). El nombre de operador queda en la auditoría (origen «API»). |
| **Grabaciones** | Historial (`GET /recordings`) con filtros por canal y periodo; origen (manual/programada/API), motivo de fin, tamaño, archivos; cambiar la **protección** (Normal / Importante / Protegida) frente a la limpieza automática. |
| **Actividad** | Registro de auditoría (`GET /events`) con filtros por periodo, canal, severidad mínima y categoría; cada fila se despliega con el detalle (payload). |
| **Almacenamiento** | Estado de cada disco de grabación y los ajustes de retención/alertas (`GET/PUT /storage/settings`), editables en caliente. |

La barra lateral (un cajón en el móvil) lleva la navegación y, abajo, el disco más lleno con su medidor, el resumen de la
licencia, si el canal en vivo está conectado y el selector de tema.

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

Si el WebSocket no llega a abrir varias veces (un proxy que no lo deja pasar), el cliente recurre solo al sondeo HTTP de
`GET /channels/{id}/preview.jpg?w=320` (la etiqueta lo indica con «HTTP»). Si el canal no tiene imagen (canal simulado,
entrada reasignándose) la aplicación envía el texto `unavailable` y la tarjeta lo dice mientras sigue esperando.

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

## Cómo llega a la API (y por qué hay un proxy)

La API del Record escucha **solo en loopback** (`127.0.0.1:5005`) y **no envía cabeceras CORS**. Por eso el servidor
de Vite (tanto `dev` como `preview`) reenvía `/api` y `/ws` a esa dirección: el navegador ve un único origen y no hay
que tocar la aplicación. `BAIOSS_API=http://otra-ip:5005 npm run dev` cambia el destino del proxy.

Si algún día el cliente se sirve desde otro sitio (otro servidor, otra máquina), la API tendrá que escuchar en la red y
permitir CORS; entonces basta poner la dirección en `VITE_API_BASE_URL` (ver `.env.example`).

## Notas de la API que condicionan el cliente

- Los enums de .NET llegan como **enteros** en `/channels` y `/storage/*` (ver `src/api/types.ts`); en `/recordings` y
  `/events` la API ya los proyecta a texto.
- `POST /channels/{id}/recording/start` exige un `profileId`, pero el canal graba siempre con el perfil vigente elegido en
  la aplicación de escritorio (el motor ignora ese id): el cliente envía el GUID vacío.
- Los mensajes del WebSocket no llevan el tipo de evento; el cliente los usa solo como señal de «algo cambió» para
  refrescar las consultas. Sin WebSocket, React Query sondea (canales cada 1 s, listas cada 10 s).
- La API no tiene autenticación (pendiente en la aplicación); el cliente tampoco.

## Estructura

```
web/
├─ vite.config.ts        # proxy /api y /ws → 127.0.0.1:5005
├─ public/favicon.svg
└─ src/
   ├─ theme.ts           # tokens de color (claro/oscuro), tipografía y estilos de los componentes MUI
   ├─ api/               # tipos de la API, cliente Axios, funciones por endpoint, formateadores y frases de la actividad
   ├─ hooks/             # hooks de React Query (consultas/mutaciones) y el WebSocket de eventos
   ├─ components/        # layout con barra lateral, tarjeta de canal, piezas comunes (StatusTag, Meter…), tema, avisos
   └─ pages/             # Canales, Grabaciones, Actividad, Almacenamiento
```
