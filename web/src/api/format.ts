import { AlarmType, RecordingState, SignalState, StorageHealth } from './types'
import type { EventEntry, FrameRate, ProtectionLevel, Severity, StopReason, Timecode, Trigger } from './types'
import { LOCALES, getLang, tr, type Key } from '../i18n'

// Cifras, fechas y etiquetas en el idioma vigente. Se llaman al pintar desde componentes que ya se vuelven a pintar al
// cambiar de idioma (usan useT()), así que leer aquí el idioma «actual» es coherente con lo que hay en pantalla.
const locale = () => LOCALES[getLang()]
const pad = (n: number) => String(Math.max(0, Math.floor(n))).padStart(2, '0')

// ---------------------------------------------------------------- cifras

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let v = bytes
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v.toLocaleString(locale(), { maximumFractionDigits: i >= 3 ? 1 : 0 })} ${units[i]}`
}

export function formatBitrate(bps: number): string {
  if (!bps) return '—'
  return bps >= 1_000_000
    ? `${(bps / 1_000_000).toLocaleString(locale(), { maximumFractionDigits: 1 })} Mb/s`
    : `${Math.round(bps / 1000)} kb/s`
}

export const formatInt = (n: number): string => n.toLocaleString(locale())

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

/** TimeSpan de .NET («1.02:03:04.5» o «02:03:04») → «1 d 2 h 3 min» (las unidades son iguales en los dos idiomas). */
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
  return d ? d.toLocaleString(locale(), { dateStyle: 'short', timeStyle: 'medium' }) : '—'
}

/** «14 sept 2026» / «14 Sept 2026». */
export function formatDay(iso: string | null | undefined): string {
  const d = valid(iso)
  return d ? d.toLocaleDateString(locale(), { day: 'numeric', month: 'short', year: 'numeric' }) : '—'
}

/** «22:37». */
export function formatTime(iso: string | null | undefined): string {
  const d = valid(iso)
  return d ? d.toLocaleTimeString(locale(), { hour: '2-digit', minute: '2-digit' }) : '—'
}

/** «22:37:58». */
export function formatClock(iso: string | null | undefined): string {
  const d = valid(iso)
  return d ? d.toLocaleTimeString(locale(), { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '—'
}

const rtfByLang = new Map<string, Intl.RelativeTimeFormat>()
function rtf(): Intl.RelativeTimeFormat {
  const lang = getLang()
  let f = rtfByLang.get(lang)
  if (!f) { f = new Intl.RelativeTimeFormat(lang, { numeric: 'auto' }); rtfByLang.set(lang, f) }
  return f
}

/** «ahora», «hace 5 minutos», «ayer»… y, pasada una semana, la fecha. */
export function formatRelative(iso: string | null | undefined): string {
  const d = valid(iso)
  if (!d) return ''
  const diff = (d.getTime() - Date.now()) / 1000
  const abs = Math.abs(diff)
  if (abs < 45) return tr('relative.now')
  if (abs < 3600) return rtf().format(Math.round(diff / 60), 'minute')
  if (abs < 86_400) return rtf().format(Math.round(diff / 3600), 'hour')
  if (abs < 7 * 86_400) return rtf().format(Math.round(diff / 86_400), 'day')
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
  if (days === 0) return tr('day.today')
  if (days === 1) return tr('day.yesterday')
  const text = d.toLocaleDateString(locale(), { weekday: 'long', day: 'numeric', month: 'long', year: d.getFullYear() === today.getFullYear() ? undefined : 'numeric' })
  return text.charAt(0).toUpperCase() + text.slice(1)
}

// ---------------------------------------------------------------- etiquetas

const stateKeys: Record<RecordingState, Key> = {
  [RecordingState.Idle]: 'state.idle',
  [RecordingState.Starting]: 'state.starting',
  [RecordingState.Recording]: 'state.recording',
  [RecordingState.Paused]: 'state.paused',
  [RecordingState.Stopping]: 'state.stopping',
  [RecordingState.Error]: 'state.error',
  [RecordingState.Recovering]: 'state.recovering',
}
export const recordingStateLabel = (s: RecordingState): string => tr(stateKeys[s] ?? 'state.unknown')

const signalKeys: Record<SignalState, Key> = {
  [SignalState.NoSignal]: 'signal.none',
  [SignalState.Unstable]: 'signal.unstable',
  [SignalState.Locked]: 'signal.ok',
}
export const signalLabel = (s: SignalState): string => tr(signalKeys[s] ?? 'signal.label')

const alarmKeys: Record<AlarmType, Key> = {
  [AlarmType.SignalLoss]: 'alarm.signalLoss',
  [AlarmType.VideoBlack]: 'alarm.videoBlack',
  [AlarmType.VideoFreeze]: 'alarm.videoFreeze',
  [AlarmType.AudioSilence]: 'alarm.audioSilence',
  [AlarmType.DiskLow]: 'alarm.diskLow',
  [AlarmType.DiskCritical]: 'alarm.diskCritical',
  [AlarmType.Slate]: 'alarm.slate',
  [AlarmType.EncoderFallback]: 'alarm.encoderFallback',
  [AlarmType.FramesDropped]: 'alarm.framesDropped',
  [AlarmType.RecordingUnverified]: 'alarm.recordingUnverified',
  [AlarmType.DiskEmergency]: 'alarm.diskEmergency',
  [AlarmType.DiskStalled]: 'alarm.diskStalled',
}
/** Etiqueta de una alarma, o `fallback` (el texto de la aplicación) si es un tipo que el panel no conoce. */
export const alarmLabel = (a: AlarmType, fallback: string): string => (alarmKeys[a] ? tr(alarmKeys[a]) : fallback)

const healthKeys: Record<StorageHealth, Key> = {
  [StorageHealth.Unknown]: 'health.unknown',
  [StorageHealth.Ok]: 'health.ok',
  [StorageHealth.Warning]: 'health.warning',
  [StorageHealth.Critical]: 'health.critical',
  [StorageHealth.Emergency]: 'health.emergency',
}
export const healthLabel = (h: StorageHealth): string => tr(healthKeys[h] ?? 'health.unknown')

const triggerKeys: Record<Trigger, Key> = { Unknown: 'trigger.unknown', Manual: 'trigger.manual', Scheduled: 'trigger.scheduled', Api: 'trigger.api' }
export const triggerLabel = (t: Trigger): string => (triggerKeys[t] ? tr(triggerKeys[t]) : t)

const stopReasonKeys: Record<StopReason, Key> = {
  Unknown: 'stopReason.unknown',
  Operator: 'stopReason.operator',
  Api: 'stopReason.api',
  ScheduledEnd: 'stopReason.scheduledEnd',
  ScheduledSkip: 'stopReason.scheduledSkip',
  DiskFull: 'stopReason.diskFull',
  Shutdown: 'stopReason.shutdown',
  Error: 'stopReason.error',
}
export const stopReasonLabel = (r: StopReason): string => (stopReasonKeys[r] ? tr(stopReasonKeys[r]) : r)

/** Motivos de fin que NO son una parada normal: se destacan en la lista de grabaciones. */
export const abnormalStop: ReadonlySet<StopReason> = new Set<StopReason>(['DiskFull', 'Shutdown', 'Error'])

const protectionKeys: Record<ProtectionLevel, Key> = { None: 'protection.none', Important: 'protection.important', Protected: 'protection.protected' }
export const protectionLabel = (l: ProtectionLevel): string => tr(protectionKeys[l])
const protectionHintKeys: Record<ProtectionLevel, Key> = { None: 'protection.noneHint', Important: 'protection.importantHint', Protected: 'protection.protectedHint' }
export const protectionHint = (l: ProtectionLevel): string => tr(protectionHintKeys[l])

const severityKeys: Record<Severity, Key> = { Debug: 'severity.debug', Info: 'severity.info', Warning: 'severity.warning', Error: 'severity.error', Critical: 'severity.critical' }
export const severityLabel = (s: Severity): string => (severityKeys[s] ? tr(severityKeys[s]) : s)

/** Categorías (nombres de evento de dominio) del registro de actividad que el panel sabe nombrar, en este orden. */
export const CATEGORIES = [
  'RecordingStarted', 'RecordingStopped', 'RecordingRenamed', 'RecordingStartFailed', 'RecordingInterrupted', 'RecordingRecovered',
  'RecordingFileUnverified', 'RecordingPaused', 'RecordingResumed', 'SegmentCompleted', 'ScheduledRecordingSkipped', 'ScheduleChanged',
  'OrphanSessionsClosed', 'EncoderFailed', 'SignalLocked', 'SignalLost', 'AudioSilenceDetected', 'AudioClippingDetected', 'StorageLow',
  'StorageEmergencyEntered', 'StorageEmergencyCleared', 'StorageStalled', 'StorageStallCleared', 'RecordingPurged', 'RetentionSkipped',
  'PerformanceDegraded',
] as const
export type Category = (typeof CATEGORIES)[number]
const isKnownCategory = (c: string): c is Category => (CATEGORIES as readonly string[]).includes(c)
/** Nombre legible de una categoría; una que el panel no conozca se muestra con su nombre técnico. */
export const categoryLabel = (c: string): string => (isKnownCategory(c) ? tr(`category.${c}`) : c)

// ---------------------------------------------------------------- actividad: del payload a una frase

// En el payload (JSON del evento de dominio, claves en PascalCase) los enums van como ENTEROS.
const triggerByInt: Trigger[] = ['Unknown', 'Manual', 'Scheduled', 'Api']
const stopReasonByInt: StopReason[] = ['Unknown', 'Operator', 'Api', 'ScheduledEnd', 'ScheduledSkip', 'DiskFull', 'Shutdown', 'Error']
const changeKeys: Key[] = ['change.unknown', 'change.created', 'change.updated', 'change.deleted', 'change.paused', 'change.resumed']
const taskChangeKeys: Key[] = ['change.unknown', 'taskChange.created', 'taskChange.updated', 'taskChange.deleted', 'taskChange.paused', 'taskChange.resumed']

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
const files = (n: number) => `${n} ${tr(n === 1 ? 'unit.file' : 'unit.files')}`
const join = (...parts: Array<string | null | undefined | false>) => parts.filter(Boolean).join(' · ')

/** Una frase corta que cuenta el evento con sus datos, en vez del volcado técnico que guarda la aplicación. */
export function describeEvent(e: EventEntry): string {
  const p = parsePayload(e)
  if (!p) return ''
  switch (e.category) {
    case 'RecordingStarted': {
      const trigger = num(p.Trigger)
      return join(
        trigger !== null && triggerByInt[trigger] ? tr('describe.origin', { trigger: triggerLabel(triggerByInt[trigger]) }) : null,
        str(p.ScheduledJobTitle) && `«${str(p.ScheduledJobTitle)}»`,
        str(p.RecordingName),
      )
    }
    case 'RecordingRenamed': {
      const n = num(p.Files)
      return join(
        str(p.FileName) && `«${str(p.FileName)}»`,
        str(p.PreviousFileName) && tr('describe.before', { name: str(p.PreviousFileName)! }),
        n !== null && n > 1 && files(n),
      )
    }
    case 'RecordingStopped': {
      const reason = num(p.Reason)
      const n = num(p.Files)
      const bytes = num(p.TotalBytes)
      return join(
        str(p.Duration) && timeSpanToClock(str(p.Duration)),
        n !== null && files(n),
        bytes !== null && formatBytes(bytes),
        reason !== null && stopReasonByInt[reason] ? stopReasonLabel(stopReasonByInt[reason]) : null,
      )
    }
    case 'RecordingStartFailed':
    case 'EncoderFailed':
    case 'RetentionSkipped':
      return str(p.Reason) ?? ''
    case 'RecordingInterrupted':
      return join(str(p.Reason), num(p.ExitCode) !== null && tr('describe.exitCode', { n: num(p.ExitCode)! }))
    case 'RecordingFileUnverified':
    case 'SegmentCompleted': {
      const size = num(p.SizeBytes)
      return join(str(p.FilePath) && fileName(str(p.FilePath)!), size !== null && formatBytes(size))
    }
    case 'ScheduleChanged': {
      const change = num(p.Change) ?? 0
      return join(change > 0 && taskChangeKeys[change] ? tr(taskChangeKeys[change]) : null, str(p.ScheduledJobTitle) && `«${str(p.ScheduledJobTitle)}»`)
    }
    case 'ScheduledRecordingSkipped':
      return join(str(p.ScheduledJobTitle) && `«${str(p.ScheduledJobTitle)}»`, str(p.Reason))
    case 'OrphanSessionsClosed': {
      const n = num(p.Count)
      return n !== null ? `${n} ${tr(n === 1 ? 'describe.sessionClosed' : 'describe.sessionsClosed')}` : ''
    }
    case 'RecordingRecovered':
      return num(p.Attempt) !== null ? tr('describe.attempt', { n: num(p.Attempt)! }) : ''
    case 'StorageLow': {
      const free = num(p.FreeBytes)
      return join(
        free !== null && tr('storage.free', { bytes: formatBytes(free) }),
        str(p.EstimatedRemaining) && tr('describe.remainingApprox', { time: formatTimeSpan(str(p.EstimatedRemaining)) }),
      )
    }
    case 'StorageEmergencyEntered':
    case 'StorageEmergencyCleared': {
      const used = num(p.UsedPercent)
      const free = num(p.FreeBytes)
      return join(
        str(p.Volume) && used !== null && tr('describe.volumeAt', { volume: str(p.Volume)!, percent: Math.round(used) }),
        free !== null && tr('storage.free', { bytes: formatBytes(free) }),
      )
    }
    case 'StorageStalled':
      return str(p.Volume) ? tr('describe.stalled', { volume: str(p.Volume)! }) : ''
    case 'StorageStallCleared':
      return join(str(p.Volume), str(p.Duration) && tr('describe.after', { time: formatTimeSpan(str(p.Duration)) }))
    case 'RecordingPurged':
      return join(num(p.Files) !== null && files(num(p.Files)!), str(p.Reason))
    case 'AudioSilenceDetected':
      return str(p.ForDuration) ? tr('describe.during', { time: formatTimeSpan(str(p.ForDuration)) }) : ''
    case 'AudioClippingDetected':
      return num(p.PeakDb) !== null ? tr('describe.peak', { db: num(p.PeakDb)!.toFixed(1) }) : ''
    default:
      return ''
  }
}

const fieldKeys: Record<string, Key> = {
  ChannelId: 'field.channel', SessionId: 'field.session', Operator: 'field.operator', Trigger: 'field.trigger', Reason: 'field.reason',
  Duration: 'field.duration', Files: 'field.files', TotalBytes: 'field.size', FilePath: 'field.file', SizeBytes: 'field.size',
  ExitCode: 'field.exitCode', ScheduledJobTitle: 'field.scheduledJob', ScheduledJobId: 'field.scheduledJobId', RecordingName: 'field.name',
  Volume: 'field.volume', FreeBytes: 'field.freeBytes', UsedPercent: 'field.usedPercent', EstimatedRemaining: 'field.remaining',
  Index: 'field.segment', Count: 'field.count', Attempt: 'field.attempt', Occurrence: 'field.occurrence', Action: 'field.action',
  ForDuration: 'field.duration', PeakDb: 'field.peak', Resource: 'field.resource', Value: 'field.value', FileName: 'field.file',
  PreviousFileName: 'field.previousName', Change: 'field.change',
}

/** Los campos del evento como filas «etiqueta → valor», ya formateados, para el detalle de una entrada. */
export function payloadRows(e: EventEntry, channelKey: (id: string) => string | undefined): Array<{ label: string; value: string }> {
  const p = parsePayload(e)
  if (!p) return []
  const rows: Array<{ label: string; value: string }> = []
  for (const [key, raw] of Object.entries(p)) {
    if (key === 'OccurredAt' || raw === null || raw === undefined || raw === '') continue
    let value: string
    if (key === 'ChannelId' && typeof raw === 'string') value = channelKey(raw) ? tr('unit.channelKey', { key: channelKey(raw)! }) : raw
    else if (key === 'Trigger' && typeof raw === 'number') value = triggerByInt[raw] ? triggerLabel(triggerByInt[raw]) : String(raw)
    else if (key === 'Reason' && typeof raw === 'number') value = stopReasonByInt[raw] ? stopReasonLabel(stopReasonByInt[raw]) : String(raw)
    else if (key === 'Action' && typeof raw === 'number') value = tr(raw === 1 ? 'action.archive' : 'action.deleteVerb')
    else if (key === 'Change' && typeof raw === 'number') value = changeKeys[raw] ? tr(changeKeys[raw]) : String(raw)
    else if (key.endsWith('Bytes') && typeof raw === 'number') value = formatBytes(raw)
    else if (key === 'UsedPercent' && typeof raw === 'number') value = `${Math.round(raw)} %`
    else if (key === 'PeakDb' && typeof raw === 'number') value = `${raw.toFixed(1)} dBFS`
    else if (key === 'Duration' && typeof raw === 'string') value = timeSpanToClock(raw)
    else if ((key === 'EstimatedRemaining' || key === 'ForDuration') && typeof raw === 'string') value = formatTimeSpan(raw)
    else if (key === 'Occurrence' && typeof raw === 'string') value = formatDate(raw)
    else if (key === 'Index' && typeof raw === 'number') value = String(raw + 1)
    else value = typeof raw === 'object' ? JSON.stringify(raw) : String(raw)
    rows.push({ label: fieldKeys[key] ? tr(fieldKeys[key]) : key, value })
  }
  return rows
}
