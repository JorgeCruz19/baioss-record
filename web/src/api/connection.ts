import { useSyncExternalStore } from 'react'
import { tr, type Key } from '../i18n'

/**
 * Dónde está la API del Record, elegido EN EL NAVEGADOR y aplicado en el acto: sin `.env`, sin recompilar y sin
 * reiniciar nada. Se recuerda en este navegador (localStorage).
 *
 *  - Sin conexión guardada → «este mismo servidor»: las peticiones van al origen que sirve el panel, que las reenvía
 *    al Record (el proxy de Vite, o el propio Record si algún día sirve el panel). Es lo de siempre.
 *  - Con conexión guardada → el navegador habla DIRECTO con `http://host:puerto`. Para eso el Record tiene que
 *    escuchar en la red y permitir este origen (🛠 Configuración → Panel web y API): si no, el navegador lo bloquea.
 *
 * `VITE_API_BASE_URL` (compilación) queda solo como valor de partida para quien empaquete el panel apuntando a un
 * Record fijo; lo que se elija aquí manda sobre él.
 */
export interface Connection { host: string; port: number }

export const DEFAULT_PORT = 5005
const KEY = 'baioss.connection'
const BUILD_DEFAULT = ((import.meta.env.VITE_API_BASE_URL as string | undefined) ?? '').replace(/\/+$/, '')

function load(): Connection | null {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return null
    const c = JSON.parse(raw) as Partial<Connection>
    return typeof c.host === 'string' && c.host && Number.isInteger(c.port) && c.port! > 0 && c.port! <= 65535
      ? { host: c.host, port: c.port! }
      : null
  } catch { return null }
}

let current: Connection | null = load()
const listeners = new Set<() => void>()

export const getConnection = (): Connection | null => current

/** Guarda (o, con `null`, vuelve a «este mismo servidor») y avisa a quien esté suscrito: consultas y WebSockets se rehacen. */
export function setConnection(next: Connection | null): void {
  current = next
  try {
    if (next) localStorage.setItem(KEY, JSON.stringify(next))
    else localStorage.removeItem(KEY)
  } catch { /* sin almacenamiento local: vale para esta sesión */ }
  listeners.forEach(l => l())
}

const subscribe = (l: () => void) => { listeners.add(l); return () => { listeners.delete(l) } }

/** `http://host:puerto` de una conexión, o vacío para «este mismo servidor». */
export const baseUrlOf = (c: Connection | null): string => (c ? `http://${c.host}:${c.port}` : '')

/** Base de la API AHORA MISMO (se consulta en cada petición: un cambio se aplica sin recargar). */
export const getApiBaseUrl = (): string => (current ? baseUrlOf(current) : BUILD_DEFAULT)

/** Cómo nombrar la conexión al operador. */
export const connectionLabel = (c: Connection | null): string =>
  c ? `${c.host}:${c.port}` : BUILD_DEFAULT ? BUILD_DEFAULT.replace(/^https?:\/\//i, '') : tr('conn.thisServer')

/** Conexión vigente + base de la API, reactivas. */
export function useConnection(): { connection: Connection | null; baseUrl: string } {
  const connection = useSyncExternalStore(subscribe, getConnection, getConnection)
  return { connection, baseUrl: connection ? baseUrlOf(connection) : BUILD_DEFAULT }
}

/**
 * Interpreta lo que escriba el operador: «192.168.1.10», «grabador-01», «192.168.1.10:5005» o una URL pegada
 * («http://192.168.1.10:5005/lo-que-sea»). El puerto del campo aparte se usa si el texto no trae uno. Un fallo se
 * devuelve como CLAVE de texto: quien lo enseña lo traduce.
 */
export function parseAddress(text: string, portText: string): Connection | { error: Key } {
  let host = text.trim().replace(/^[a-z]+:\/\//i, '').replace(/\/.*$/, '')
  let port = Number(portText.trim() || DEFAULT_PORT)
  const withPort = /^(.*):(\d{1,5})$/.exec(host)
  if (withPort) { host = withPort[1]; port = Number(withPort[2]) }
  if (!host) return { error: 'conn.errHost' }
  if (!/^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$/i.test(host)) return { error: 'conn.errHostChars' }
  if (!Number.isInteger(port) || port < 1 || port > 65535) return { error: 'conn.errPort' }
  return { host, port }
}
