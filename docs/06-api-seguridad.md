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
| GET | `/channels/{id}/status` | Estado consolidado (señal, stats, sesión) | Operador |
| GET | `/inputs` | Fuentes disponibles / descubiertas | Operador |
| GET | `/storage?volume=D:\` | Espacio, tiempo restante, consumo por canal | Operador |
| GET | `/recordings?channel=&from=&to=` | Historial de grabaciones (paginado) | Supervisor |
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

- Login usuario/contraseña → token firmado (JWT) con rol y expiración.
- Contraseñas con **hash + salt** (PBKDF2/Argon2); nunca en claro ni reversibles.
- API local por defecto en `127.0.0.1`; exposición en red exige TLS y, opcionalmente, API key
  por cliente de automatización.

### Auditoría

Toda acción sensible (login, start/stop, cambios de configuración, borrado por retención) se
escribe append-only en `EventLogEntry` con `Operator`, `Timestamp`, `Category` y `PayloadJson`.
El registro es consultable por rango y exportable, y es independiente del log técnico de Serilog.
