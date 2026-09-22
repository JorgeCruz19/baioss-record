import axios, { AxiosError } from 'axios'
import { connectionLabel, getApiBaseUrl, getConnection } from './connection'
import { tr } from '../i18n'

/**
 * Cliente HTTP de la API. La base NO se fija al crear el cliente: se resuelve EN CADA PETICIÓN (ver connection.ts),
 * así que cambiar la IP o el puerto del Record desde el panel se aplica en el acto, sin recargar ni recompilar.
 * Base vacía = mismo origen: el servidor del panel reenvía /api y /ws al Record (ver vite.config.ts).
 */
export const api = axios.create({ timeout: 10_000 })
api.interceptors.request.use(config => {
  config.baseURL = `${getApiBaseUrl()}/api/v1`
  return config
})

/** Base de los WebSocket: la de la API con esquema ws/wss, o este mismo origen. */
const wsBase = () => (getApiBaseUrl() || window.location.origin).replace(/^http/i, 'ws')

/** URL del WebSocket de eventos del Record (mismo host que la API). */
export function wsEventsUrl(): string {
  return `${wsBase()}/ws/events`
}

/** URL del WebSocket de vista previa de un canal: la aplicación empuja JPEG de `width` px a `fps` imágenes por segundo. */
export function wsPreviewUrl(channelId: string, width: number, fps: number): string {
  return `${wsBase()}/ws/preview/${channelId}?w=${width}&fps=${fps}`
}

/** Qué decir cuando no hay respuesta: depende de a dónde se está apuntando. */
function unreachable(): string {
  const remote = getConnection()
  return remote ? tr('error.unreachableRemote', { label: connectionLabel(remote) }) : tr('error.unreachableLocal')
}

/**
 * Texto legible de un fallo: el `error` que devuelve el Record (400/409/422), un ProblemDetails, o que la aplicación
 * no responde. Con la aplicación cerrada el proxy de Vite contesta 5xx sin cuerpo: se trata igual que un fallo de red.
 * Un bloqueo por CORS tampoco trae respuesta: para el navegador es indistinguible de un fallo de red.
 */
export function errorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const e = err as AxiosError<{ error?: string; title?: string; detail?: string } | string>
    const data = typeof e.response?.data === 'object' && e.response.data !== null ? e.response.data : undefined
    if (data?.error) return data.error
    if (data?.detail) return data.detail
    if (data?.title) return data.title
    if (!e.response || e.response.status >= 500) return unreachable()
    return tr('error.status', { status: `${e.response.status} ${e.response.statusText}`.trim() })
  }
  return err instanceof Error ? err.message : String(err)
}
