import axios from 'axios'
import { errorMessage } from './client'
import { LOCALES, getLang, tr, type Key } from '../i18n'
import type { Recurrence, ScheduledTask, TaskState, TodayStatus, Weekday } from './types'

// Textos y cálculos de la PROGRAMACIÓN (tareas automáticas). Todas las horas que llegan de la API son de PARED del equipo
// que graba, sin zona («20:00:00», «2026-09-20T20:00:00»). `new Date('2026-09-20T20:00:00')` las interpreta como hora
// local del navegador, así que sus componentes son exactamente los del texto: sirve para formatear sin mover la hora.

const locale = () => LOCALES[getLang()]

export const WEEKDAYS: Weekday[] = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
const initialKeys: Record<Weekday, Key> = { Mon: 'wd.Mon', Tue: 'wd.Tue', Wed: 'wd.Wed', Thu: 'wd.Thu', Fri: 'wd.Fri', Sat: 'wd.Sat', Sun: 'wd.Sun' }
const nameKeys: Record<Weekday, Key> = {
  Mon: 'wdName.Mon', Tue: 'wdName.Tue', Wed: 'wdName.Wed', Thu: 'wdName.Thu', Fri: 'wdName.Fri', Sat: 'wdName.Sat', Sun: 'wdName.Sun',
}
/** Inicial del día (L M X J V S D en español; M T W T F S S en inglés), como en la aplicación de escritorio. */
export const weekdayInitial = (d: Weekday): string => tr(initialKeys[d])
export const weekdayName = (d: Weekday): string => tr(nameKeys[d])

const recurrenceKeys: Record<Recurrence, Key> = { Once: 'recurrence.once', Daily: 'recurrence.daily', Weekly: 'recurrence.weekly' }
export const recurrenceLabel = (r: Recurrence): string => tr(recurrenceKeys[r])

const taskStateKeys: Record<TaskState, Key> = { running: 'taskState.running', scheduled: 'taskState.scheduled', paused: 'taskState.paused', done: 'taskState.done' }
export const taskStateLabel = (s: TaskState): string => tr(taskStateKeys[s])

const todayKeys: Record<TodayStatus, Key> = { scheduled: 'todayStatus.scheduled', running: 'todayStatus.running', recorded: 'todayStatus.recorded', skipped: 'todayStatus.skipped' }
export const todayStatusLabel = (s: TodayStatus): string => tr(todayKeys[s])

/** «20:00:00» → «20:00»; los segundos solo se enseñan si los hay. */
export function shortTime(time: string | null | undefined): string {
  if (!time) return '—'
  const [h, m, s] = time.split(':')
  return s && s !== '00' ? `${h}:${m}:${s}` : `${h}:${m}`
}

/** Hora de una marca de pared («2026-09-20T20:00:00» → «20:00»). */
export const wallTime = (wall: string | null | undefined): string => (wall ? shortTime(wall.slice(11, 19)) : '—')

const asDate = (wall: string): Date => new Date(wall.length === 10 ? `${wall}T00:00:00` : wall)
const sameDay = (a: Date, b: Date) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()

/** «hoy a las 20:00», «mañana a las 08:00», «lun 21 sept · 23:30». `now` es el reloj del equipo que graba. */
export function whenLabel(wall: string | null | undefined, now: string | null | undefined): string {
  if (!wall) return '—'
  const d = asDate(wall)
  const ref = now ? asDate(now) : new Date()
  const time = wallTime(wall)
  if (sameDay(d, ref)) return tr('when.today', { time })
  const tomorrow = new Date(ref); tomorrow.setDate(ref.getDate() + 1)
  if (sameDay(d, tomorrow)) return tr('when.tomorrow', { time })
  const sameYear = d.getFullYear() === ref.getFullYear()
  const day = d.toLocaleDateString(locale(), { weekday: 'short', day: 'numeric', month: 'short', ...(sameYear ? {} : { year: 'numeric' }) })
  return `${day.replace(',', '')} · ${time}`
}

/** «20 sept 2026» / «20 Sept 2026». */
export const dateLabel = (date: string): string =>
  asDate(date).toLocaleDateString(locale(), { day: 'numeric', month: 'short', year: 'numeric' })

/** «1 h 30 min», «45 min», «30 s» (las unidades son iguales en los dos idiomas). */
export function durationLabel(seconds: number | null | undefined): string {
  if (!seconds || seconds <= 0) return '—'
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = seconds % 60
  const parts: string[] = []
  if (h) parts.push(`${h} h`)
  if (m) parts.push(`${m} min`)
  if (s && !h) parts.push(`${s} s`)
  return parts.join(' ') || '0 s'
}

/** Cuándo se repite, en palabras: «Todos los días», «L · X · V», «20 sept 2026». */
export function repeatLabel(t: Pick<ScheduledTask, 'recurrence' | 'weekdays' | 'date'>): string {
  if (t.recurrence === 'Daily') return tr('repeat.daily')
  if (t.recurrence === 'Once') return dateLabel(t.date)
  if (t.weekdays.length === 7) return tr('repeat.daily')
  if (t.weekdays.length === 5 && !t.weekdays.includes('Sat') && !t.weekdays.includes('Sun')) return tr('repeat.weekdays')
  if (t.weekdays.length === 2 && t.weekdays.includes('Sat') && t.weekdays.includes('Sun')) return tr('repeat.weekends')
  return WEEKDAYS.filter(d => t.weekdays.includes(d)).map(weekdayInitial).join(' · ')
}

/** Los días de una semanal con su nombre, para leerlos enteros (tooltip y lectores de pantalla). */
export const weekdaysSpoken = (days: Weekday[]): string =>
  WEEKDAYS.filter(d => days.includes(d)).map(weekdayName).join(', ')

/** «20:00 – 21:00» (con «+1 día» si cruza la medianoche). */
export const timeRange = (t: Pick<ScheduledTask, 'startTime' | 'endTime' | 'endsNextDay'>): string =>
  `${shortTime(t.startTime)} – ${shortTime(t.endTime)}${t.endsNextDay ? ` ${tr('range.nextDay')}` : ''}`

/** Duración inicio→fin en segundos; fin < inicio = termina al día siguiente; iguales = 0 (no vale). */
export function spanSeconds(start: string, end: string): number {
  const secs = (t: string) => { const [h, m, s] = t.split(':').map(Number); return (h || 0) * 3600 + (m || 0) * 60 + (s || 0) }
  const a = secs(start)
  const b = secs(end)
  return b > a ? b - a : b < a ? b + 86400 - a : 0
}

/** «quedan 35 min», «queda 1 h 05 min», «quedan 40 s». */
export function remainingLabel(seconds: number): string {
  if (seconds >= 3600) {
    const h = Math.floor(seconds / 3600)
    const m = String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')
    return tr(h === 1 ? 'remaining.hourOne' : 'remaining.hourMany', { h, m })
  }
  if (seconds >= 60) return tr('remaining.minutes', { n: Math.ceil(seconds / 60) })
  return tr('remaining.seconds', { n: Math.max(0, seconds) })
}

// ---------- errores de la API de programación, en el idioma del panel ----------
// La API los manda con un `code` estable y un `error` en el idioma de la APLICACIÓN, que puede no ser el del panel: se
// traduce por código y, si no se conoce, se enseña el texto tal cual.

const problemKeys: Record<string, Key> = {
  'invalid-time': 'problem.invalidTime',
  'end-equals-start': 'problem.endEqualsStart',
  'segment-minutes-invalid': 'problem.segmentMinutes',
  'pick-weekday': 'problem.pickWeekday',
  'pick-date': 'problem.pickDate',
  'invalid-date': 'problem.invalidDate',
  'past-date': 'problem.pastDate',
  'duration-overlaps-next': 'problem.durationOverlaps',
  'duplicate-title': 'problem.duplicateTitle',
  'unknown-channel': 'problem.unknownChannel',
  'not-found': 'problem.notFound',
  'no-active-task': 'problem.noActiveTask',
  'no-scheduler': 'problem.noScheduler',
}

/** A qué campo del formulario pertenece cada problema (para marcarlo donde toca). */
export const problemField: Record<string, 'title' | 'date' | 'time' | 'weekdays' | 'segment' | 'channel'> = {
  'invalid-time': 'time', 'end-equals-start': 'time', 'duration-overlaps-next': 'time', clash: 'time',
  'segment-minutes-invalid': 'segment', 'pick-weekday': 'weekdays', 'pick-date': 'date', 'invalid-date': 'date', 'past-date': 'date',
  'duplicate-title': 'title', 'unknown-channel': 'channel',
}

export function scheduleProblem(err: unknown): { code: string | null; message: string } {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data as { code?: string; error?: string; clashWith?: string } | undefined
    const code = typeof data?.code === 'string' ? data.code : null
    if (code === 'clash') return { code, message: tr('problem.clash', { title: data?.clashWith ?? tr('problem.otherTask') }) }
    if (code && problemKeys[code]) return { code, message: tr(problemKeys[code]) }
    return { code, message: errorMessage(err) }
  }
  return { code: null, message: errorMessage(err) }
}
