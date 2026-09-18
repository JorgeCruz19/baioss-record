import { AlarmType, RecordingState, SignalState, StorageHealth } from './types'
import type { EventEntry, FrameRate, ProtectionLevel, Severity, StopReason, Timecode, Trigger } from './types'

const pad = (n: number) => String(Math.max(0, Math.floor(n))).padStart(2, '0')

// ---------------------------------------------------------------- cifras

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let v = bytes
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v.toLocaleString('es-ES', { maximumFractionDigits: i >= 3 ? 1 : 0 })} ${units[i]}`
}

export function formatBitrate(bps: number): string {
  if (!bps) return '—'
  return bps >= 1_000_000
    ? `${(bps / 1_000_000).toLocaleString('es-ES', { maximumFractionDigits: 1 })} Mb/s`
    : `${Math.round(bps / 1000)} kb/s`
}

export function formatFps(fr: FrameRate | null | undefined): string {
  if (!fr || !fr.denominator) return '—'
  const v = fr.numerator / fr.denominator
  return Number.isInteger(v) ? String(v) : v.toFixed(2)
}

export function formatTimecode(tc: Timecode | null | undefined): string {
  if (!tc) return '00:00:00:00'
  return `${pad(tc.hours)}:${pad(tc.minutes)}:${pad(tc.seconds)}${tc.dropFrame ? ';' : ':'}${pad(tc.frames)}`
}

/** Segundos → «HH:MM:SS». */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds))
  return `${pad(s / 3600)}:${pad((s % 3600) / 60)}:${pad(s % 60)}`
}

const TIMESPAN = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/

/** TimeSpan de .NET («1.02:03:04.5» o «02:03:04») → «1 d 2 h 3 min». */
export function formatTimeSpan(ts: string | null | undefined): string {
  if (!ts) return '—'
  const m = TIMESPAN.exec(ts)
  if (!m) return ts
  const d = Number(m[1] ?? 0)
  const h = Number(m[2])
  const min = Number(m[3])
  const s = Number(m[4])
  if (!d && !h && !min) return `${s} s`
  const parts: string[] = []
  if (d) parts.push(`${d} d`)
  if (h) parts.push(`${h} h`)
  parts.push(`${min} min`)
  return parts.join(' ')
}

/** TimeSpan de .NET → «HH:MM:SS» (para duraciones de grabación). */
export function timeSpanToClock(ts: string | null | undefined): string {
  const m = ts ? TIMESPAN.exec(ts) : null
  if (!m) return '—'
  return formatDuration(Number(m[1] ?? 0) * 86400 + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4]))
}

// ---------------------------------------------------------------- fechas

const valid = (iso: string | null | undefined): Date | null => {
  if (!iso) return null
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? null : d
}

export function formatDate(iso: string | null | undefined): string {
  const d = valid(iso)
  return d ? d.toLocaleString('es-ES', { dateStyle: 'short', timeStyle: 'medium' }) : '—'
}

/** «14 sept 2026». */
export function formatDay(iso: string | null | undefined): string {
  const d = valid(iso)
  return d ? d.toLocaleDateString('es-ES', { day: 'numeric', month: 'short', year: 'numeric' }) : '—'
}

/** «22:37». */
export function formatTime(iso: string | null | undefined): string {
  const d = valid(iso)
  return d ? d.toLocaleTimeString('es-ES', { hour: '2-digit', minute: '2-digit' }) : '—'
}

/** «22:37:58». */
export function formatClock(iso: string | null | undefined): string {
  const d = valid(iso)
  return d ? d.toLocaleTimeString('es-ES', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '—'
}

const rtf = new Intl.RelativeTimeFormat('es', { numeric: 'auto' })

/** «ahora», «hace 5 minutos», «ayer»… y, pasada una semana, la fecha. */
export function formatRelative(iso: string | null | undefined): string {
  const d = valid(iso)
  if (!d) return ''
  const diff = (d.getTime() - Date.now()) / 1000
  const abs = Math.abs(diff)
  if (abs < 45) return 'ahora'
  if (abs < 3600) return rtf.format(Math.round(diff / 60), 'minute')
  if (abs < 86_400) return rtf.format(Math.round(diff / 3600), 'hour')
  if (abs < 7 * 86_400) return rtf.format(Math.round(diff / 86_400), 'day')
  return formatDay(iso)
}

/** Clave de día LOCAL («2026-09-14») para agrupar listas. */
export function dayKey(iso: string): string {
  const d = valid(iso)
  return d ? `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}` : ''
}

/** «Hoy», «Ayer» o «lunes, 14 de septiembre». */
export function dayLabel(iso: string): string {
  const d = valid(iso)
  if (!d) return ''
  const today = new Date()
  const startOf = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime()
  const days = Math.round((startOf(today) - startOf(d)) / 86_400_000)
  if (days === 0) return 'Hoy'
  if (days === 1) return 'Ayer'
  const text = d.toLocaleDateString('es-ES', { weekday: 'long', day: 'numeric', month: 'long', year: d.getFullYear() === today.getFullYear() ? undefined : 'numeric' })
  return text.charAt(0).toUpperCase() + text.slice(1)
}

// ---------------------------------------------------------------- etiquetas

export const recordingStateLabel: Record<RecordingState, string> = {
  [RecordingState.Idle]: 'Inactivo',
  [RecordingState.Starting]: 'Iniciando…',
  [RecordingState.Recording]: 'Grabando',
  [RecordingState.Paused]: 'En pausa',
  [RecordingState.Stopping]: 'Deteniendo…',
  [RecordingState.Error]: 'Error',
  [RecordingState.Recovering]: 'Recuperando…',
}

export const signalLabel: Record<SignalState, string> = {
  [SignalState.NoSignal]: 'Sin señal',
  [SignalState.Unstable]: 'Señal inestable',
  [SignalState.Locked]: 'Señal OK',
}

export const alarmLabel: Record<AlarmType, string> = {
  [AlarmType.SignalLoss]: 'Pérdida de señal',
  [AlarmType.VideoBlack]: 'Vídeo en negro',
  [AlarmType.VideoFreeze]: 'Vídeo congelado',
  [AlarmType.AudioSilence]: 'Silencio de audio',
  [AlarmType.DiskLow]: 'Disco bajo',
  [AlarmType.DiskCritical]: 'Disco crítico',
  [AlarmType.Slate]: 'Carta de ajuste',
  [AlarmType.EncoderFallback]: 'Codificador alternativo',
  [AlarmType.FramesDropped]: 'Cuadros perdidos',
  [AlarmType.RecordingUnverified]: 'Grabación sin verificar',
  [AlarmType.DiskEmergency]: 'Disco casi lleno',
  [AlarmType.DiskStalled]: 'Disco no responde',
}

export const healthLabel: Record<StorageHealth, string> = {
  [StorageHealth.Unknown]: 'Sin medir',
  [StorageHealth.Ok]: 'Con espacio',
  [StorageHealth.Warning]: 'Aviso',
  [StorageHealth.Critical]: 'Crítico',
  [StorageHealth.Emergency]: 'Casi lleno',
}

export const triggerLabel: Record<Trigger, string> = {
  Unknown: 'Sin registrar', Manual: 'Manual', Scheduled: 'Programada', Api: 'API',
}

export const stopReasonLabel: Record<StopReason, string> = {
  Unknown: 'Sin registrar',
  Operator: 'La detuvo el operador',
  Api: 'Detenida por API',
  ScheduledEnd: 'Fin de la programación',
  ScheduledSkip: 'Saltada por el operador',
  DiskFull: 'Disco lleno',
  Shutdown: 'Cierre de la aplicación',
  Error: 'Error',
}

/** Motivos de fin que NO son una parada normal: se destacan en la lista de grabaciones. */
export const abnormalStop: ReadonlySet<StopReason> = new Set<StopReason>(['DiskFull', 'Shutdown', 'Error'])

export const protectionLabel: Record<ProtectionLevel, string> = {
  None: 'Normal', Important: 'Importante', Protected: 'Protegida',
}

export const protectionHint: Record<ProtectionLevel, string> = {
  None: 'La limpieza automática puede borrarla',
  Important: 'Destacada; no se borra sola',
  Protected: 'No se borra nunca automáticamente',
}

export const severityLabel: Record<Severity, string> = {
  Debug: 'Depuración', Info: 'Información', Warning: 'Aviso', Error: 'Error', Critical: 'Crítico',
}

/** Nombre legible de las categorías (nombres de evento de dominio) del registro de actividad. */
export const categoryLabels: Record<string, string> = {
  RecordingStarted: 'Grabación iniciada',
  RecordingStopped: 'Grabación detenida',
  RecordingStartFailed: 'No se pudo iniciar la grabación',
  RecordingInterrupted: 'Grabación interrumpida',
  RecordingRecovered: 'Grabación recuperada',
  RecordingFileUnverified: 'Archivo sin verificar',
  RecordingPaused: 'Grabación en pausa',
  RecordingResumed: 'Grabación reanudada',
  SegmentCompleted: 'Segmento cerrado',
  ScheduledRecordingSkipped: 'Grabación programada omitida',
  OrphanSessionsClosed: 'Sesiones sin cerrar recuperadas',
  EncoderFailed: 'Fallo del codificador',
  SignalLocked: 'Señal detectada',
  SignalLost: 'Señal perdida',
  AudioSilenceDetected: 'Silencio de audio',
  AudioClippingDetected: 'Audio saturado',
  StorageLow: 'Queda poco espacio',
  StorageEmergencyEntered: 'Disco casi lleno',
  StorageEmergencyCleared: 'El disco vuelve a tener espacio',
  StorageStalled: 'El disco no responde',
  StorageStallCleared: 'El disco responde de nuevo',
  RecordingPurged: 'Grabación limpiada',
  RetentionSkipped: 'Limpieza omitida',
  PerformanceDegraded: 'Rendimiento degradado',
}
export const categoryLabel = (c: string) => categoryLabels[c] ?? c

// ---------------------------------------------------------------- actividad: del payload a una frase

// En el payload (JSON del evento de dominio, claves en PascalCase) los enums van como ENTEROS.
const triggerByInt: Trigger[] = ['Unknown', 'Manual', 'Scheduled', 'Api']
const stopReasonByInt: StopReason[] = ['Unknown', 'Operator', 'Api', 'ScheduledEnd', 'ScheduledSkip', 'DiskFull', 'Shutdown', 'Error']

export type Payload = Record<string, unknown>

export function parsePayload(e: EventEntry): Payload | null {
  if (!e.payloadJson) return null
  try {
    const v: unknown = JSON.parse(e.payloadJson)
    return v && typeof v === 'object' && !Array.isArray(v) ? (v as Payload) : null
  } catch { return null }
}

const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : null)
const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null)
const fileName = (path: string) => path.split(/[\\/]/).pop() ?? path
const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
const join = (...parts: Array<string | null | undefined | false>) => parts.filter(Boolean).join(' · ')

/** Una frase corta que cuenta el evento con sus datos, en vez del volcado técnico que guarda la aplicación. */
export function describeEvent(e: EventEntry): string {
  const p = parsePayload(e)
  if (!p) return ''
  switch (e.category) {
    case 'RecordingStarted': {
      const trigger = num(p.Trigger)
      return join(
        trigger !== null && triggerByInt[trigger] ? `Origen: ${triggerLabel[triggerByInt[trigger]]}` : null,
        str(p.ScheduledJobTitle) && `«${str(p.ScheduledJobTitle)}»`,
        str(p.RecordingName),
      )
    }
    case 'RecordingStopped': {
      const reason = num(p.Reason)
      const files = num(p.Files)
      const bytes = num(p.TotalBytes)
      return join(
        str(p.Duration) && timeSpanToClock(str(p.Duration)),
        files !== null && plural(files, 'archivo', 'archivos'),
        bytes !== null && formatBytes(bytes),
        reason !== null && stopReasonByInt[reason] ? stopReasonLabel[stopReasonByInt[reason]] : null,
      )
    }
    case 'RecordingStartFailed':
    case 'EncoderFailed':
    case 'RetentionSkipped':
      return str(p.Reason) ?? ''
    case 'RecordingInterrupted':
      return join(str(p.Reason), num(p.ExitCode) !== null && `código ${num(p.ExitCode)}`)
    case 'RecordingFileUnverified':
    case 'SegmentCompleted': {
      const size = num(p.SizeBytes)
      return join(str(p.FilePath) && fileName(str(p.FilePath)!), size !== null && formatBytes(size))
    }
    case 'ScheduledRecordingSkipped':
      return join(str(p.ScheduledJobTitle) && `«${str(p.ScheduledJobTitle)}»`, str(p.Reason))
    case 'OrphanSessionsClosed':
      return num(p.Count) !== null ? plural(num(p.Count)!, 'sesión cerrada al arrancar', 'sesiones cerradas al arrancar') : ''
    case 'RecordingRecovered':
      return num(p.Attempt) !== null ? `Intento ${num(p.Attempt)}` : ''
    case 'StorageLow': {
      const free = num(p.FreeBytes)
      return join(free !== null && `${formatBytes(free)} libres`, str(p.EstimatedRemaining) && `quedan ≈ ${formatTimeSpan(str(p.EstimatedRemaining))}`)
    }
    case 'StorageEmergencyEntered':
    case 'StorageEmergencyCleared': {
      const used = num(p.UsedPercent)
      const free = num(p.FreeBytes)
      return join(str(p.Volume) && used !== null && `${str(p.Volume)} al ${Math.round(used)} %`, free !== null && `${formatBytes(free)} libres`)
    }
    case 'StorageStalled':
      return str(p.Volume) ? `${str(p.Volume)} no acepta escrituras; la grabación espera sin cortar` : ''
    case 'StorageStallCleared':
      return join(str(p.Volume), str(p.Duration) && `tras ${formatTimeSpan(str(p.Duration))}`)
    case 'RecordingPurged':
      return join(num(p.Files) !== null && plural(num(p.Files)!, 'archivo', 'archivos'), str(p.Reason))
    case 'AudioSilenceDetected':
      return str(p.ForDuration) ? `Durante ${formatTimeSpan(str(p.ForDuration))}` : ''
    case 'AudioClippingDetected':
      return num(p.PeakDb) !== null ? `Pico de ${num(p.PeakDb)!.toFixed(1)} dBFS` : ''
    default:
      return ''
  }
}

const fieldLabels: Record<string, string> = {
  ChannelId: 'Canal', SessionId: 'Sesión', Operator: 'Operador', Trigger: 'Origen', Reason: 'Motivo', Duration: 'Duración',
  Files: 'Archivos', TotalBytes: 'Tamaño', FilePath: 'Archivo', SizeBytes: 'Tamaño', ExitCode: 'Código de salida',
  ScheduledJobTitle: 'Tarea programada', ScheduledJobId: 'Id. de la tarea', RecordingName: 'Nombre', Volume: 'Disco',
  FreeBytes: 'Espacio libre', UsedPercent: 'Ocupación', EstimatedRemaining: 'Tiempo restante', Index: 'Segmento',
  Count: 'Cantidad', Attempt: 'Intento', Occurrence: 'Ocurrencia', Action: 'Acción', ForDuration: 'Duración',
  PeakDb: 'Pico', Resource: 'Recurso', Value: 'Valor',
}

/** Los campos del evento como filas «etiqueta → valor», ya formateados, para el detalle de una entrada. */
export function payloadRows(e: EventEntry, channelKey: (id: string) => string | undefined): Array<{ label: string; value: string }> {
  const p = parsePayload(e)
  if (!p) return []
  const rows: Array<{ label: string; value: string }> = []
  for (const [key, raw] of Object.entries(p)) {
    if (key === 'OccurredAt' || raw === null || raw === undefined || raw === '') continue
    let value: string
    if (key === 'ChannelId' && typeof raw === 'string') value = channelKey(raw) ? `Canal ${channelKey(raw)}` : raw
    else if (key === 'Trigger' && typeof raw === 'number') value = triggerByInt[raw] ? triggerLabel[triggerByInt[raw]] : String(raw)
    else if (key === 'Reason' && typeof raw === 'number') value = stopReasonByInt[raw] ? stopReasonLabel[stopReasonByInt[raw]] : String(raw)
    else if (key === 'Action' && typeof raw === 'number') value = raw === 1 ? 'Archivar' : 'Borrar'
    else if (key.endsWith('Bytes') && typeof raw === 'number') value = formatBytes(raw)
    else if (key === 'UsedPercent' && typeof raw === 'number') value = `${Math.round(raw)} %`
    else if (key === 'PeakDb' && typeof raw === 'number') value = `${raw.toFixed(1)} dBFS`
    else if (key === 'Duration' && typeof raw === 'string') value = timeSpanToClock(raw)
    else if ((key === 'EstimatedRemaining' || key === 'ForDuration') && typeof raw === 'string') value = formatTimeSpan(raw)
    else if (key === 'Occurrence' && typeof raw === 'string') value = formatDate(raw)
    else if (key === 'Index' && typeof raw === 'number') value = String(raw + 1)
    else value = typeof raw === 'object' ? JSON.stringify(raw) : String(raw)
    rows.push({ label: fieldLabels[key] ?? key, value })
  }
  return rows
}
