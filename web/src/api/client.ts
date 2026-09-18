import axios, { AxiosError } from 'axios'

/** Base de la API. Vacía = mismo origen: el proxy de Vite reenvía /api y /ws al Record (ver vite.config.ts). */
export const apiBaseUrl = ((import.meta.env.VITE_API_BASE_URL as string | undefined) ?? '').replace(/\/+$/, '')

export const api = axios.create({ baseURL: `${apiBaseUrl}/api/v1`, timeout: 10_000 })

/** URL del WebSocket de eventos del Record (mismo host que la API, esquema ws/wss). */
export function wsEventsUrl(): string {
  const base = apiBaseUrl || window.location.origin
  return base.replace(/^http/i, 'ws') + '/ws/events'
}

const UNREACHABLE = 'No se pudo contactar con la aplicación. Ábrela en este equipo; el panel se reconectará solo.'

/**
 * Texto legible de un fallo: el `error` que devuelve el Record (400/409/422), un ProblemDetails, o que la aplicación
 * no responde. Con la aplicación cerrada el proxy de Vite contesta 5xx sin cuerpo: se trata igual que un fallo de red.
 */
export function errorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const e = err as AxiosError<{ error?: string; title?: string; detail?: string } | string>
    const data = typeof e.response?.data === 'object' && e.response.data !== null ? e.response.data : undefined
    if (data?.error) return data.error
    if (data?.detail) return data.detail
    if (data?.title) return data.title
    if (!e.response || e.response.status >= 500) return UNREACHABLE
    return `La aplicación respondió ${e.response.status} ${e.response.statusText}`.trim()
  }
  return err instanceof Error ? err.message : String(err)
}
