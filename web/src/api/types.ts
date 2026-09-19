// Tipos de la API REST de Baioss Record (http://127.0.0.1:5005/api/v1). Los enums de .NET llegan como ENTEROS
// (serialización por defecto de System.Text.Json), salvo donde la API los proyecta a texto (grabaciones y eventos).

export const RecordingState = {
  Idle: 0, Starting: 1, Recording: 2, Paused: 3, Stopping: 4, Error: 5, Recovering: 6,
} as const
export type RecordingState = (typeof RecordingState)[keyof typeof RecordingState]

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
