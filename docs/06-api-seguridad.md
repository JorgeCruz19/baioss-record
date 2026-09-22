# 06 · API y seguridad

## REST API (automatización)

Base: `/api/v1`. Autenticación por token (Bearer) emitido en login. Todas las rutas de
mutación exigen rol con permiso; las de lectura, sesión válida.

| Método | Ruta | Descripción | Rol mínimo |
|--------|------|-------------|-----------|
| POST | `/channels/{id}/recording/start` | Inicia grabación con un perfil | Operador |
| POST | `/channels/{id}/recording/stop` | Detiene grabación | Operador |
| POST | `/channels/{id}/recording/pause` | Pausa | Operador |
| POST | `/channels/{id}/recording/resume` | Reanuda | Operador |
| GET | `/channels` | Lista canales con estado | Operador |
| GET | `/channels/{id}/status` | Estado consolidado (señal, stats, sesión, y el preset vigente: `presetName` + `profileSummary`) | Operador |
| GET | `/channels/{id}/preview.jpg?w=320` | Instantánea JPEG de BAJA resolución del preview (`w` = 160–640 px de ancho, 320 por defecto ≈ 5–20 KB; `Cache-Control: no-store`). La aplicación captura y codifica SOLO cuando se pide: sin clientes no cuesta nada. 404 si el canal no tiene preview | Operador |
| GET | `/inputs` | Fuentes disponibles / descubiertas | Operador |
| GET | `/storage?volume=D:\` | Espacio, tiempo restante, consumo por canal | Operador |
| GET | `/recordings?channel=&from=&to=` | Historial de grabaciones (paginado) | Supervisor |
| GET | `/events?days=&channel=&category=&severity=&take=` | Registro de auditoría (ver `AUDITORIA-GRABACIONES.md`) | Supervisor |
| POST | `/schedule` | Crea trabajo programado | Supervisor |

Ejemplo:

```http
POST /api/v1/channels/8f3c.../recording/start
Authorization: Bearer <token>
Content-Type: application/json

{ "profileId": "1a2b...", "operator": "jcruz" }
→ 200 OK { "sessionId": "9d4e..." }
```

El mapeo vive en `Api/ApiEndpoints.cs` (`MapBaiossApi`). Cada endpoint despacha un
comando/query CQRS — la API y la UI comparten exactamente la misma lógica de aplicación.

## Dónde escucha la API (dirección, puerto y CORS)

Por defecto, lo de siempre: **`http://127.0.0.1:5005`, solo este equipo, sin cabeceras CORS**. Abrirla es una decisión
explícita del administrador, en **🛠 Configuración → Panel web y API** (o editando `data/api-settings.json`), y se aplica
**al reiniciar**, porque el servidor enlaza su dirección al arrancar:

```json
{ "Host": "0.0.0.0", "Port": 5005, "AllowedOrigins": "http://192.168.1.50:5173" }
```

| Campo | Valores |
|---|---|
| `Host` | `127.0.0.1` = solo este equipo · `0.0.0.0` = toda la red · o una IPv4 concreta de este equipo. Cualquier otra cosa (nombre, IPv6, vacío) vuelve a `127.0.0.1`. |
| `Port` | 1–65535 (fuera de rango → 5005). |
| `AllowedOrigins` | Orígenes web que pueden llamar a la API **desde un navegador** (CORS), separados por comas; se normalizan a `esquema://host[:puerto]`. `*` = cualquiera. Vacío = ninguno (no se añade el middleware de CORS: comportamiento idéntico al de antes). |

La primera vez el archivo se siembra desde la configuración (`Api:Host`, `Api:Port`, `Api:AllowedOrigins`, o las
variables `Api__Host`…); después manda el archivo. Piezas: `Application/Network/ApiAccessSettings` (modelo + saneado),
`Infrastructure/Network/ApiAccessSettingsFile` (leer/guardar atómico + `CanBind`), `Api/ApiCors` y, en la aplicación,
`App.LoadApiAccess` + `ApiAccessViewModel`.

- **Red de seguridad al arrancar.** Si la dirección guardada no se puede enlazar (una IP que el equipo ya no tiene, un
  puerto ocupado), la aplicación **arranca igualmente** en `127.0.0.1` con el mismo puerto (y, si tampoco, en el 5005),
  lo registra como error y lo enseña en la ventana de Configuración. Un ajuste de red equivocado jamás impide grabar. El
  archivo no se toca: sigue diciendo lo que pidió el administrador.
- **CORS no es seguridad.** Solo decide qué PÁGINAS WEB pueden leer respuestas desde un navegador; `curl`, un script o
  cualquier programa lo ignoran. Los WebSocket no pasan por CORS. Lo que protege la API es dónde escucha (y la red).
- **⚠ La API no tiene autenticación** (ver más abajo). Con `Host` distinto de loopback, cualquiera que llegue al puerto
  puede ver los canales y grabar/detener. Solo en redes de confianza; nunca expuesta a Internet. El arranque lo deja
  escrito en el registro («ABIERTA A LA RED, sin autenticación»).
- **Registro.** El panel web sondea cada segundo, y ASP.NET Core escribía ~7 líneas informativas por petición (decenas de
  MB al día por panel abierto). `Microsoft.AspNetCore` se registra ahora solo desde *Warning*.

El panel web (`web/`) elige a qué Record se conecta desde el propio navegador (IP y puerto, sin recompilar): ver
`web/README.md`.

## WebSocket de preview de baja resolución

`GET /ws/preview/{id}?w=320&fps=5` (upgrade). Para el panel web: el SERVIDOR empuja un JPEG de `w` píxeles de ancho
(160–640; 320 por defecto) cada 1/`fps` segundos (1–15; 5 por defecto).

- Mensaje **binario** = un JPEG completo. Mensaje de **texto** `unavailable` = el canal no tiene imagen ahora mismo
  (canal simulado, entrada reasignándose); la conexión sigue abierta y los cuadros vuelven solos.
- **Sin colas**: el siguiente cuadro no se captura hasta que el anterior salió, así que un cliente lento recibe menos
  cuadros en vez de acumular retraso. Cada envío tiene un plazo de 5 s; si vence, la conexión se aborta.
- **Bajo demanda**: la captura y la codificación solo ocurren mientras hay un cliente conectado (o pidiendo
  `preview.jpg`). Tope de 32 conexiones simultáneas (503 si se supera).
- El cliente no envía datos; cerrar el socket detiene la captura.

## WebSocket de eventos

`GET /ws/events` (upgrade). Emite en tiempo real los eventos de dominio serializados a JSON,
alimentados por `IEventBus`:

- **Señal**: `SignalLocked`, `SignalLost`, `AudioSilenceDetected`, `AudioClippingDetected`.
- **Grabación**: `RecordingStarted/Stopped/Paused/Resumed`, `SegmentCompleted`,
  `EncoderFailed`, `RecordingRecovered`.
- **Sistema**: `StorageLow`, `PerformanceDegraded`.

```json
{ "type": "SegmentCompleted", "sessionId": "9d4e…", "index": 3,
  "filePath": "D:/rec/A_20260616_150000_003.mxf", "sizeBytes": 5368709120,
  "occurredAt": "2026-06-16T15:15:00Z" }
```

Permite a sistemas externos (MAM, playout, automatización de master control) reaccionar sin polling.

### Al cerrar la aplicación, el servidor corta los WebSocket

Los dos WebSocket (`/ws/events` y `/ws/preview`) se terminan en cuanto empieza el apagado (`ApplicationStopping`); el
cliente debe **reconectar** por su cuenta (el panel web lo hace). No es un detalle: antes esperaban a que el cliente
cerrara, y un panel web abierto retenía el apagado de Kestrel hasta su plazo (30 s). Como el cierre de la aplicación
para primero el host y DESPUÉS finaliza las grabaciones, todo bajo un tope de 20 s, el tope vencía antes de llegar a
ellas. Medido (2026-09-19) cerrando con un canal grabando y el panel abierto: 22 s, salida forzada, sesión sin cerrar
en BD y **archivo MP4 ilegible (`moov atom not found`)**. Con el arreglo: 2,8 s, sesión cerrada con motivo `Shutdown` y
archivo válido. Además, `HostOptions.ShutdownTimeout` baja a 8 s para que ningún cliente de la API pueda comerse el
tope. Lo fija `ApiEndpointsTests.Sockets_AreReleasedAsSoonAsTheApplicationStartsStopping…`.

## Seguridad

### Roles y permisos

| Permiso | Administrador | Supervisor | Operador |
|---------|:---:|:---:|:---:|
| Grabar / detener / pausar | ✓ | ✓ | ✓ |
| Programar / cambiar perfil-fuente | ✓ | ✓ | — |
| Definir retención / almacenamiento | ✓ | ✓ | — |
| Gestionar usuarios y roles | ✓ | — | — |
| Ver auditoría y logs | ✓ | ✓ | — |

`IAuthorizationPolicy.HasPermission(role, permission)` centraliza la matriz; los endpoints y
los comandos de UI la consultan antes de ejecutar.

### Autenticación

> **Estado real (2026-09-19): NO implementada.** Lo que sigue es el diseño previsto (pendiente A10 de la auditoría 24/7).
> Hoy la API acepta cualquier petición que llegue a su puerto, sin token ni TLS. Por eso escucha solo en loopback por
> defecto y abrirla a la red es una opción explícita, con aviso en la interfaz y en el registro (ver «Dónde escucha la
> API»). Si se va a usar fuera de una red de confianza, la autenticación debe hacerse ANTES.

- Login usuario/contraseña → token firmado (JWT) con rol y expiración.
- Contraseñas con **hash + salt** (PBKDF2/Argon2); nunca en claro ni reversibles.
- API local por defecto en `127.0.0.1`; exposición en red exige TLS y, opcionalmente, API key
  por cliente de automatización.

### Auditoría

Toda acción sensible (login, start/stop, cambios de configuración, borrado por retención) se
escribe append-only en `EventLogEntry` con `Operator`, `Timestamp`, `Category` y `PayloadJson`.
El registro es consultable por rango y exportable, y es independiente del log técnico de Serilog.
