# Auditoría de grabaciones

Toda grabación deja rastro: **quién la puso en marcha, cómo (a mano, por programación o por la API), qué
material produjo y por qué terminó**. También las que *no* llegaron a ocurrir, que es lo que suele buscarse
cuando falta un programa.

Es una traza para responder preguntas de después: *«¿quién grabó esto?»*, *«¿por qué se cortó lo de anoche a
las 3?»*, *«la programación de las 20:00 no dejó archivo, ¿qué pasó?»*.

## Dónde vive

| | |
|---|---|
| **Tabla** | `EventLog`, en `data\baioss.db` |
| **La escribe** | `EventLogWriter` (servicio de fondo), a partir de los eventos del bus |
| **Se consulta** | `GET /api/v1/events` (ver abajo) o directamente con cualquier cliente SQLite |
| **Se conserva** | 30 días (`EventLogWriter.RetentionDays`); la poda corre cada 6 h |

Cada entrada lleva: momento, severidad, categoría (el nombre del evento), canal, operador, un mensaje legible
y una **copia estructurada en JSON** (`PayloadJson`) para explotarla con herramientas.

> La retención de 30 días es higiene de la base de datos, **no** afecta a las grabaciones. Si tu operativa
> exige conservar la auditoría más tiempo, sube `RetentionDays` (0 = no podar nunca).

## Qué se registra de una grabación

| Evento | Cuándo | Qué responde |
|---|---|---|
| `RecordingStarted` | Arrancó | Quién, **cómo** (Manual / Scheduled / Api), qué tarea programada la disparó y con qué nombre |
| `RecordingStopped` | Terminó | **Por qué** terminó, cuánto duró, cuántos archivos y cuántos bytes dejó |
| `RecordingStartFailed` | No llegó a arrancar | El motivo (pre-vuelo, dispositivo, licencia…) |
| `ScheduledRecordingSkipped` | El programador omitió una ocurrencia | Por qué (el canal ya grababa, el canal no está disponible) |
| `SegmentCompleted` | Se cerró cada trozo | La ruta y el tamaño de cada archivo |
| `OrphanSessionsClosed` | Al arrancar | Que la ejecución anterior terminó de forma no controlada (corte de luz, cuelgue) |

Los dos últimos de la tabla son los que evitan el agujero clásico: **una grabación que no ocurre no deja
archivo, y sin estas entradas tampoco dejaría ninguna otra huella**.

### Cómo se puso en marcha (`Trigger`)

| Valor | Significa |
|---|---|
| `Unknown` | Grabación anterior a esta función: nadie registró su procedencia |
| `Manual` | El operador pulsó ● Grabar |
| `Scheduled` | La disparó una tarea de 🕒 Programación |
| `Api` | La pidió un sistema externo por REST |

Antes esto no era un dato fiable: manual y programada solo se distinguían porque el programador escribía la
cadena «Programación» en el campo de operador — y un usuario de Windows puede llamarse así. Ahora el
disparador viaja como tal, y las programadas llevan además el **id y el título de la tarea**, así que un
archivo se puede rastrear hasta la programación que lo generó.

### Por qué terminó (`StopReason`)

| Valor | Significa |
|---|---|
| `Operator` | El operador pulsó ■ Detener |
| `Api` | La detuvo un sistema externo |
| `ScheduledEnd` | Se cumplió la hora de fin de la programación |
| `ScheduledSkip` | El operador saltó esa ocurrencia (⏏) |
| `DiskFull` | La guarda de disco la detuvo para no corromper el archivo |
| `Shutdown` | Se cerró la aplicación con la grabación en curso |
| `Error` | Se abortó por un fallo |
| `Unknown` | No se declaró motivo — y en las grabaciones anteriores a esta función |

`DiskFull` y `Error` se registran con severidad **Warning** a propósito: en la revisión de una noche, una
grabación que se cortó sola tiene que destacar entre las decenas que terminaron con normalidad.

## Además, en el historial de grabaciones

Las mismas tres cosas se guardan en la propia sesión (tabla `Sessions`: `Trigger`, `StopReason`,
`ScheduledJobId`) y salen en `GET /api/v1/recordings`. La diferencia es de uso: el `EventLog` es la
**secuencia de lo que pasó** (incluidos los intentos fallidos); `Sessions` es el **estado final de cada
grabación**, para cruzarlo con el archivo.

## Consultarla desde la aplicación

Botón **📋 Actividad** de la ventana principal. Muestra la tabla en palabras —no el volcado técnico— con:

- **Filtros**: ventana temporal, canal, tipo (*Todo* / *Solo grabaciones* / *Solo incidencias*) y nivel mínimo.
- **Un chip de nivel por fila**: gris lo rutinario, ámbar los avisos, rojo los errores. Es lo que permite
  barrer una noche entera de un vistazo y quedarse solo con lo que se torció.
- **⬇ Exportar**: guarda en CSV *lo que se está viendo*, con los filtros aplicados (separador «;» y BOM, así
  que Excel lo abre directamente y con los acentos bien).

Es **solo lectura**: no se edita ni se borra nada desde ahí. Una auditoría que la propia aplicación puede
modificar no vale como auditoría; de la poda por antigüedad se encarga el escritor.

## Consultarla por API

```bash
curl "http://127.0.0.1:5005/api/v1/events?days=7"
```

| Parámetro | Para qué |
|---|---|
| `days` | Ventana hacia atrás (7 por defecto) |
| `channel` | Filtra por canal (GUID) |
| `category` | Un tipo de evento: `RecordingStarted`, `RecordingStopped`, `RecordingStartFailed`… |
| `severity` | `Info` \| `Warning` \| `Error` \| `Critical` — devuelve esa severidad **y las superiores** |
| `take` | Máximo de entradas (500 por defecto, tope 5000) |

Lo que suele pedirse:

```bash
# Todo lo que se torció esta semana
curl "http://127.0.0.1:5005/api/v1/events?days=7&severity=Warning"

# Programaciones que no llegaron a grabar
curl "http://127.0.0.1:5005/api/v1/events?days=30&category=ScheduledRecordingSkipped"
```

O directamente contra la base de datos:

```sql
SELECT Timestamp, Severity, Category, Operator, Message
FROM EventLog
WHERE Category LIKE 'Recording%'
ORDER BY Timestamp DESC LIMIT 50;
```

## Detalles de implementación que conviene conocer

**El origen es obligatorio.** `StartRecordingAsync` recibe un `RecordingOrigin` sin valor por defecto, y
`StopRecordingAsync` un motivo. Es deliberado: si fuesen opcionales, acabaría habiendo llamadas sin declarar
su procedencia y la auditoría tendría huecos silenciosos. Al añadir una forma nueva de iniciar o detener una
grabación, el compilador obliga a decir cuál es.

**La auditoría sobrevive al cierre de la aplicación.** El cierre para los servicios de fondo *antes* de
detener las grabaciones en curso (para que el programador no toque los canales mientras se finalizan los
archivos). Eso significa que los últimos eventos —los que dicen «esto se cortó porque se cerró el programa»—
se publican cuando el escritor ya está parado. Por eso `EventLogWriter` mantiene viva su suscripción al bus
después de pararse y, a partir de ahí, escribe cada evento en el acto en vez de encolarlo.

**Actualizar desde una versión anterior no pierde nada.** Las tres columnas nuevas de `Sessions` se añaden con
`ALTER TABLE` idempotente al arrancar. Las grabaciones anteriores quedan con `Trigger = Unknown` y
`StopReason = Unknown` —el 0 de ambos enumerados es `Unknown` justamente por esto—, que es exactamente lo que
se sabe de ellas: no se inventa un dato que nadie registró.

**Lo que NO es esto.** Los registros de `logs\` (Serilog) son otra cosa: diagnóstico técnico para soporte, en
español, con detalle de procesos y errores. La auditoría es la traza de negocio, consultable y estable.

**El texto de la ventana se compone en C#, no con enlaces `{loc:T}`**, así que al cambiar de idioma hay que
rehacerlo a mano: el ViewModel se suscribe a `Localizer.LanguageChanged`, reconstruye las etiquetas de los
filtros (conservando lo elegido) y recarga las filas. Sin eso la ventana quedaba a medias —cabeceras en un
idioma y filtros y filas en el otro—. Ver `LOCALIZACION.md`, «Dónde se esconde el texto sin traducir».

## Pendiente

- **Exportar a PDF/JSON** además de CSV, si algún cliente lo pide en un formato concreto.
- **Firmar o encadenar** las entradas (hash de la anterior) si alguna vez hace falta demostrar que el registro
  no se ha manipulado. Hoy es una tabla SQLite: quien tenga acceso al equipo puede editarla por fuera.
- La **API sigue sin autenticación** (solo escucha en loopback): mientras siga así, la auditoría es
  consultable por cualquier proceso local. Ver `AUDITORIA-24x7.md` (A10).
