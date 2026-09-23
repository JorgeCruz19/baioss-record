// Tipos de la API REST de Baioss Record (http://127.0.0.1:5005/api/v1). Los enums de .NET llegan como ENTEROS
// (serialización por defecto de System.Text.Json), salvo donde la API los proyecta a texto (grabaciones y eventos).

export const RecordingState = {
  Idle: 0, Starting: 1, Recording: 2, Paused: 3, Stopping: 4, Error: 5, Recovering: 6,
} as const
export type RecordingState = (typeof RecordingState)[keyof typeof RecordingState]

/** De dónde viene una grabación (entero en /channels; en /recordings y /events la API lo da en texto). */
export const SessionTrigger = { Unknown: 0, Manual: 1, Scheduled: 2, Api: 3 } as const
export type SessionTrigger = (typeof SessionTrigger)[keyof typeof SessionTrigger]

export const SignalState = { NoSignal: 0, Unstable: 1, Locked: 2 } as const
export type SignalState = (typeof SignalState)[keyof typeof SignalState]

export const AlarmType = {
  SignalLoss: 0, VideoBlack: 1, VideoFreeze: 2, AudioSilence: 3, DiskLow: 4, DiskCritical: 5, Slate: 6,
  EncoderFallback: 7, FramesDropped: 8, RecordingUnverified: 9, DiskEmergency: 10, DiskStalled: 11,
} as const
export type AlarmType = (typeof AlarmType)[keyof typeof AlarmType]

export const StorageHealth = { Unknown: 0, Ok: 1, Warning: 2, Critical: 3, Emergency: 4 } as const
export type StorageHealth = (typeof StorageHealth)[keyof typeof StorageHealth]

export const RetentionAction = { Delete: 0, Archive: 1 } as const
export type RetentionAction = (typeof RetentionAction)[keyof typeof RetentionAction]

export interface Resolution { width: number; height: number }
export interface FrameRate { numerator: number; denominator: number }
export interface Bitrate { bitsPerSecond: number }
export interface Timecode { hours: number; minutes: number; seconds: number; frames: number; dropFrame: boolean }

export interface SignalInfo {
  state: SignalState
  resolution: Resolution | null
  frameRate: FrameRate | null
  audioLayout: number | null
  hasAudio: boolean
  timecode: Timecode | null
  bitrate: Bitrate | null
  formatLabel: string | null
  /** Canales de audio que entrega la fuente (2 salvo DeckLink/NDI multicanal). */
  audioChannels: number
  /** Qué se graba de ellos, en palabras («Par 3-4 de 8»), solo cuando hay más de un estéreo. */
  audioSelectionLabel: string | null
  /** Pares (1-based) que van al archivo; los medidores (`audio`) traen TODOS los canales capturados. */
  audioSelectedPairs: number[] | null
  /** Pistas de audio que llevará el archivo, en palabras («8 pistas»; en el idioma de la aplicación); null con estéreo. */
  audioTracksLabel?: string | null
}

export interface RecorderStats {
  inputFps: number
  outputFps: number
  droppedFrames: number
  duplicatedFrames: number
  bitrate: Bitrate
  bufferHealth: number
  timecode: Timecode
  frameCount: number
  recordedBytes: number
}

export interface AudioMeter { peakDb: number; rmsDb: number; clipping: boolean }
export interface ChannelAlarm { type: AlarmType; message: string; since: string; isCritical: boolean }
export interface StorageInfo {
  freeBytes: number
  totalBytes: number
  /** TimeSpan de .NET («02:15:00») o null fuera de grabación. */
  estimatedRemaining: string | null
  freeGiB: number
  totalGiB: number
}

/** GET /channels y GET /channels/{id}/status. */
export interface ChannelStatus {
  channelId: string
  key: string
  recordingState: RecordingState
  signal: SignalInfo
  stats: RecorderStats
  sessionId: string | null
  audio: AudioMeter[] | null
  alarms: ChannelAlarm[] | null
  storage: StorageInfo | null
  inputName: string | null
  /** Preset de grabación vigente del canal (con el que se grabará al pulsar Grabar). */
  presetName: string | null
  /** Resumen técnico de ese preset («H264x264 · 8 Mbps · nativa · Mp4»), en el idioma de la aplicación. */
  profileSummary: string | null
  /** Origen de la grabación EN CURSO (null en reposo). Una programada ya tiene nombre: al detenerla no se le pide otro. */
  sessionTrigger?: SessionTrigger | null
}

/**
 * Respuesta de «detener» cuando se pidió un nombre. `pending` = el archivo aún se está optimizando y se renombrará solo
 * al acabar. Si no se aplicó, `detail` dice por qué: not-recording · scheduled · unsupported · not-renamed.
 */
export interface StopResult { renamed: boolean; pending: boolean; fileName: string | null; detail: string | null }

// ---------- Programación: tareas automáticas de grabación de cada canal ----------
// TODAS las horas son de PARED del equipo que graba, sin zona («20:00:00», «2026-09-20T20:00:00»): una tarea «diaria a las
// 20:00» son las 20:00 de ese equipo todo el año. No se convierten a instantes del navegador: se pintan tal cual.

export type Recurrence = 'Once' | 'Daily' | 'Weekly'
export type Weekday = 'Mon' | 'Tue' | 'Wed' | 'Thu' | 'Fri' | 'Sat' | 'Sun'
/** running = grabando ahora · scheduled = tiene una próxima ejecución · paused · done = única y ya pasó. */
export type TaskState = 'running' | 'scheduled' | 'paused' | 'done'
export type TodayStatus = 'scheduled' | 'running' | 'recorded' | 'skipped'

export interface ScheduledTask {
  id: string
  channelId: string
  channelKey: string | null
  title: string
  recurrence: Recurrence
  weekdays: Weekday[]
  /** Fecha (yyyy-MM-dd) de la primera —o única— ocurrencia. */
  date: string
  startTime: string
  endTime: string
  durationSeconds: number | null
  endsNextDay: boolean
  segmentMinutes: number | null
  enabled: boolean
  state: TaskState
  nextRun: string | null
  lastRun: string | null
  runningUntil: string | null
  today: { start: string; end: string | null; status: TodayStatus } | null
}

/** Una grabación programada EN MARCHA (la realidad del scheduler, no una deducción por la hora). */
export interface ActiveTask {
  jobId: string
  channelId: string
  channelKey: string | null
  sessionId: string
  title: string
  startedAt: string
  endsAt: string
  /** Calculado por el equipo que graba con SU reloj. */
  remainingSeconds: number
}

/** GET /schedule. `now` es el reloj del equipo que graba; `utcOffsetMinutes`, su desfase (para avisar si el navegador está en otra zona). */
export interface ScheduleList { now: string; utcOffsetMinutes: number; jobs: ScheduledTask[]; active: ActiveTask[] }

/** Lo que se envía al crear o editar. `date` solo con «Once»; `weekdays` solo con «Weekly». */
export interface TaskDraft {
  channelId: string
  title: string
  recurrence: Recurrence
  date: string | null
  startTime: string
  endTime: string
  weekdays: Weekday[]
  segmentMinutes: number | null
  operator: string | null
}

export type ProtectionLevel = 'None' | 'Important' | 'Protected'
export type Trigger = 'Unknown' | 'Manual' | 'Scheduled' | 'Api'
export type StopReason = 'Unknown' | 'Operator' | 'Api' | 'ScheduledEnd' | 'ScheduledSkip' | 'DiskFull' | 'Shutdown' | 'Error'

/** GET /recordings. */
export interface RecordingSummary {
  id: string
  channelId: string
  startedAt: string
  endedAt: string | null
  durationSeconds: number
  totalBytes: number
  files: number
  protection: ProtectionLevel
  operator: string | null
  trigger: Trigger
  stopReason: StopReason
  scheduledJobId: string | null
}

export type Severity = 'Debug' | 'Info' | 'Warning' | 'Error' | 'Critical'

/** GET /events. */
export interface EventEntry {
  id: number
  timestamp: string
  severity: Severity
  category: string
  channelId: string | null
  operator: string | null
  message: string
  payloadJson: string | null
}

/** GET /license. */
export interface LicenseInfo {
  state: number
  daysRemaining: number | null
  summary: string
  machineCode: string
  canStartRecording: boolean
  licensedChannels: number
}

export interface VolumeStatus { label: string; freeBytes: number; totalBytes: number; usedPercent: number; health: StorageHealth }

/** GET /storage/status: el disco PEOR en los campos planos + desglose por disco. */
export interface StorageSnapshot {
  freeBytes: number
  totalBytes: number
  usedPercent: number
  health: StorageHealth
  isEmergency: boolean
  volumeCount: number
  worstLabel: string | null
  volumes: VolumeStatus[] | null
  freeGiB: number
}

/** GET/PUT /storage/settings. */
export interface StorageSettings {
  retentionEnabled: boolean
  retentionDays: number
  minFreeGB: number
  minFreePercent: number
  intervalMinutes: number
  action: RetentionAction
  archivePath: string | null
  warnPercent: number
  criticalPercent: number
  emergencyPercent: number
  autoCleanupOnEmergency: boolean
  stopNewRecordingsOnEmergency: boolean
}
