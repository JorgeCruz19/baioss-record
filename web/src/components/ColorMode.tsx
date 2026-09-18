import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { CssBaseline, ThemeProvider, useMediaQuery } from '@mui/material'
import { buildTheme, type Mode } from '../theme'

export type ModePreference = Mode | 'system'

interface ColorModeApi {
  /** Lo que eligió el usuario: claro, oscuro o seguir al sistema. */
  preference: ModePreference
  /** El modo que se está pintando ahora mismo. */
  mode: Mode
  setPreference: (p: ModePreference) => void
}

const STORAGE_KEY = 'baioss.theme'
const Ctx = createContext<ColorModeApi>({ preference: 'system', mode: 'light', setPreference: () => undefined })

function readPreference(): ModePreference {
  try {
    const v = localStorage.getItem(STORAGE_KEY)
    return v === 'light' || v === 'dark' || v === 'system' ? v : 'system'
  } catch { return 'system' }
}

/** Tema claro/oscuro: por defecto sigue al sistema; la elección del usuario se recuerda en este navegador. */
export function ColorModeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreferenceState] = useState<ModePreference>(readPreference)
  const systemDark = useMediaQuery('(prefers-color-scheme: dark)', { noSsr: true })
  const mode: Mode = preference === 'system' ? (systemDark ? 'dark' : 'light') : preference
  const theme = useMemo(() => buildTheme(mode), [mode])

  useEffect(() => {
    document.documentElement.style.colorScheme = mode
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme.palette.background.default)
  }, [mode, theme])

  const api = useMemo<ColorModeApi>(() => ({
    preference,
    mode,
    setPreference: p => {
      setPreferenceState(p)
      try { localStorage.setItem(STORAGE_KEY, p) } catch { /* sin almacenamiento local: solo dura la sesión */ }
    },
  }), [preference, mode])

  return (
    <Ctx.Provider value={api}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        {children}
      </ThemeProvider>
    </Ctx.Provider>
  )
}

export const useColorMode = () => useContext(Ctx)
