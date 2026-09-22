import { useCallback, useEffect, useSyncExternalStore, type ReactNode } from 'react'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import { esES } from '@mui/x-date-pickers/locales'
import 'dayjs/locale/es'
import 'dayjs/locale/en-gb'
import { es } from './es'
import { en } from './en'

/**
 * Idioma del panel: español o inglés, como la aplicación de escritorio. La primera vez sigue al navegador (español si su
 * idioma es cualquier variante de español; si no, inglés); lo que elija el operador se recuerda en este navegador. El
 * cambio se aplica al instante: todo lo que se pinta pasa por `t()`, y las cifras y las fechas por el locale del idioma.
 *
 * Las claves las define `es.ts` (la fuente de verdad); `en.ts` tiene que tener exactamente las mismas, y el compilador
 * lo comprueba. `{nombre}` en un texto es un hueco que rellena `t(clave, { nombre })`.
 */
export type Lang = 'es' | 'en'
export type Key = keyof typeof es
export type Params = Record<string, string | number>
export type T = (key: Key, params?: Params) => string

export const LANGS: readonly Lang[] = ['es', 'en']
/** Locale de Intl por idioma. Inglés BRITÁNICO a propósito: reloj de 24 h y fechas día/mes, como en un control de grabación. */
export const LOCALES: Record<Lang, string> = { es: 'es-ES', en: 'en-GB' }
const DAYJS_LOCALES: Record<Lang, string> = { es: 'es', en: 'en-gb' }
const STORAGE_KEY = 'baioss.lang'
const dictionaries: Record<Lang, Record<Key, string>> = { es, en }

function detect(): Lang {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'es' || saved === 'en') return saved
  } catch { /* sin almacenamiento local: se decide por el navegador */ }
  return /^es\b/i.test(navigator.language ?? '') ? 'es' : 'en'
}

let current: Lang = detect()
const listeners = new Set<() => void>()
const subscribe = (l: () => void) => { listeners.add(l); return () => { listeners.delete(l) } }

/** Idioma vigente, para el código que no es un componente (formateadores). Los componentes usan `useT()`. */
export const getLang = (): Lang => current

export function setLang(lang: Lang): void {
  if (lang === current) return
  current = lang
  try { localStorage.setItem(STORAGE_KEY, lang) } catch { /* vale para esta sesión */ }
  listeners.forEach(l => l())
}

/** Traduce una clave rellenando los huecos `{nombre}`. Sin traducción cae al español y, si tampoco, se ve la clave. */
export function translate(lang: Lang, key: Key, params?: Params): string {
  const text = dictionaries[lang][key] ?? es[key] ?? key
  return params ? text.replace(/\{(\w+)\}/g, (match, name: string) => (name in params ? String(params[name]) : match)) : text
}

/** Como `translate`, con el idioma vigente: para el código que no es un componente. */
export const tr = (key: Key, params?: Params): string => translate(current, key, params)

/** «3 canales», «1 canal»: la cifra con su nombre en singular o en plural. */
export const plural = (t: T, n: number, one: Key, many: Key): string => `${n} ${t(n === 1 ? one : many)}`

export const useLang = (): Lang => useSyncExternalStore(subscribe, getLang, getLang)

/** `t` para los textos del componente y el idioma/locale vigentes. Quien lo usa se vuelve a pintar al cambiar de idioma. */
export function useT(): { t: T; lang: Lang; locale: string } {
  const lang = useLang()
  const t = useCallback<T>((key, params) => translate(lang, key, params), [lang])
  return { t, lang, locale: LOCALES[lang] }
}

/** Idioma de la página y locale de los selectores de fecha y hora de MUI X (calendario, nombres de los meses, botones). */
export function LanguageProvider({ children }: { children: ReactNode }) {
  const lang = useLang()
  useEffect(() => { document.documentElement.lang = lang }, [lang])
  return (
    <LocalizationProvider
      dateAdapter={AdapterDayjs}
      adapterLocale={DAYJS_LOCALES[lang]}
      localeText={lang === 'es' ? esES.components.MuiLocalizationProvider.defaultProps.localeText : undefined}
    >
      {children}
    </LocalizationProvider>
  )
}
