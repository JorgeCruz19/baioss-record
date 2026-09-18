import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'
import { Box, Snackbar } from '@mui/material'
import CheckCircleRoundedIcon from '@mui/icons-material/CheckCircleRounded'
import ErrorRoundedIcon from '@mui/icons-material/ErrorRounded'
import InfoRoundedIcon from '@mui/icons-material/InfoRounded'
import WarningRoundedIcon from '@mui/icons-material/WarningRounded'
import { toneColor, type Tone } from '../theme'

type Level = 'success' | 'info' | 'warning' | 'error'
interface SnackApi { notify: (message: string, level?: Level) => void }

const Ctx = createContext<SnackApi>({ notify: () => undefined })

const visual: Record<Level, { tone: Tone; icon: ReactNode }> = {
  success: { tone: 'good', icon: <CheckCircleRoundedIcon fontSize="small" /> },
  info: { tone: 'accent', icon: <InfoRoundedIcon fontSize="small" /> },
  warning: { tone: 'warning', icon: <WarningRoundedIcon fontSize="small" /> },
  error: { tone: 'critical', icon: <ErrorRoundedIcon fontSize="small" /> },
}

/** Un único aviso global y discreto: `useSnack().notify('texto', 'error')` desde cualquier pantalla. */
export function SnackProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<{ message: string; level: Level; key: number } | null>(null)
  const notify = useCallback((message: string, level: Level = 'info') => setState({ message, level, key: Date.now() }), [])
  const value = useMemo(() => ({ notify }), [notify])
  const v = visual[state?.level ?? 'info']

  return (
    <Ctx.Provider value={value}>
      {children}
      <Snackbar
        key={state?.key}
        open={state !== null}
        autoHideDuration={state?.level === 'error' ? 7000 : 4000}
        onClose={(_, reason) => { if (reason !== 'clickaway') setState(null) }}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        <Box
          role={state?.level === 'error' ? 'alert' : 'status'}
          sx={t => ({
            display: 'flex',
            alignItems: 'center',
            gap: 1.25,
            maxWidth: 560,
            px: 2,
            py: 1.25,
            borderRadius: 3,
            fontSize: 13.5,
            fontWeight: 500,
            // Aviso en «tinta inversa»: oscuro sobre tema claro y claro sobre tema oscuro.
            bgcolor: t.palette.mode === 'dark' ? '#f1f0ec' : '#1a1a19',
            color: t.palette.mode === 'dark' ? '#0b0b0b' : '#ffffff',
            boxShadow: '0 12px 32px rgba(0, 0, 0, 0.28)',
            '& svg': { color: toneColor(t, v.tone), flexShrink: 0 },
          })}
        >
          {v.icon}
          <span>{state?.message}</span>
        </Box>
      </Snackbar>
    </Ctx.Provider>
  )
}

export const useSnack = () => useContext(Ctx)
