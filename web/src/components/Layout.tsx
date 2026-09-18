import { useState, type ReactNode } from 'react'
import {
  Box, Button, Drawer, IconButton, List, ListItemButton, ListItemIcon, ListItemText, ToggleButton, ToggleButtonGroup, Tooltip,
  Typography, useMediaQuery,
} from '@mui/material'
import { alpha, useTheme } from '@mui/material/styles'
import VideocamOutlinedIcon from '@mui/icons-material/VideocamOutlined'
import VideoLibraryOutlinedIcon from '@mui/icons-material/VideoLibraryOutlined'
import HistoryRoundedIcon from '@mui/icons-material/HistoryRounded'
import StorageRoundedIcon from '@mui/icons-material/StorageRounded'
import MenuRoundedIcon from '@mui/icons-material/MenuRounded'
import LightModeOutlinedIcon from '@mui/icons-material/LightModeOutlined'
import DarkModeOutlinedIcon from '@mui/icons-material/DarkModeOutlined'
import SettingsBrightnessOutlinedIcon from '@mui/icons-material/SettingsBrightnessOutlined'
import KeyOutlinedIcon from '@mui/icons-material/KeyOutlined'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import { Link, Outlet, useLocation } from 'react-router-dom'
import { useLicense, useStorageStatus } from '../hooks/queries'
import { useLiveEvents } from '../hooks/useLiveEvents'
import { formatBytes } from '../api/format'
import { StorageHealth } from '../api/types'
import { toneColor, toneTint, type Tone } from '../theme'
import { Dot, Meter } from './common'
import { useColorMode, type ModePreference } from './ColorMode'

const SIDEBAR_WIDTH = 248

const NAV: Array<{ to: string; label: string; icon: ReactNode }> = [
  { to: '/canales', label: 'Canales', icon: <VideocamOutlinedIcon fontSize="small" /> },
  { to: '/grabaciones', label: 'Grabaciones', icon: <VideoLibraryOutlinedIcon fontSize="small" /> },
  { to: '/actividad', label: 'Actividad', icon: <HistoryRoundedIcon fontSize="small" /> },
  { to: '/almacenamiento', label: 'Almacenamiento', icon: <StorageRoundedIcon fontSize="small" /> },
]

/** Tono del medidor de disco: acento mientras hay sitio, y de ahí a aviso y a crítico. */
export function healthTone(h: StorageHealth): Tone {
  if (h === StorageHealth.Warning) return 'warning'
  if (h === StorageHealth.Critical || h === StorageHealth.Emergency) return 'critical'
  return h === StorageHealth.Unknown ? 'neutral' : 'accent'
}

function Brand() {
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
      <Box sx={t => ({ display: 'grid', placeItems: 'center', width: 30, height: 30, borderRadius: '9px', bgcolor: toneColor(t, 'accent') })}>
        <Box sx={{ width: 11, height: 11, borderRadius: '50%', bgcolor: '#ffffff' }} />
      </Box>
      <Box sx={{ lineHeight: 1.15 }}>
        <Typography sx={{ fontSize: 14.5, fontWeight: 700, letterSpacing: '-0.01em' }}>Baioss Record</Typography>
        <Typography variant="caption" color="text.secondary">Panel web</Typography>
      </Box>
    </Box>
  )
}

function ThemeSwitch() {
  const { preference, setPreference } = useColorMode()
  return (
    <ToggleButtonGroup
      exclusive
      fullWidth
      size="small"
      value={preference}
      onChange={(_, v: ModePreference | null) => { if (v) setPreference(v) }}
      aria-label="Tema de la interfaz"
    >
      <ToggleButton value="light" aria-label="Tema claro"><Tooltip title="Claro"><LightModeOutlinedIcon sx={{ fontSize: 17 }} /></Tooltip></ToggleButton>
      <ToggleButton value="dark" aria-label="Tema oscuro"><Tooltip title="Oscuro"><DarkModeOutlinedIcon sx={{ fontSize: 17 }} /></Tooltip></ToggleButton>
      <ToggleButton value="system" aria-label="Tema del sistema"><Tooltip title="Como el sistema"><SettingsBrightnessOutlinedIcon sx={{ fontSize: 17 }} /></Tooltip></ToggleButton>
    </ToggleButtonGroup>
  )
}

function Sidebar({ connected, onNavigate }: { connected: boolean; onNavigate?: () => void }) {
  const { pathname } = useLocation()
  const storage = useStorageStatus().data
  const license = useLicense().data
  const measured = storage && storage.health !== StorageHealth.Unknown

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', p: 2 }}>
      <Box sx={{ px: 1, pt: 1, pb: 3 }}><Brand /></Box>

      <List component="nav" aria-label="Secciones" disablePadding sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
        {NAV.map(item => {
          const selected = pathname.startsWith(item.to)
          return (
            <ListItemButton
              key={item.to}
              component={Link}
              to={item.to}
              selected={selected}
              onClick={onNavigate}
              aria-current={selected ? 'page' : undefined}
              sx={{ py: 0.9, px: 1.25 }}
            >
              <ListItemIcon sx={t => ({ minWidth: 32, color: selected ? toneColor(t, 'accent') : 'text.secondary' })}>{item.icon}</ListItemIcon>
              <ListItemText
                primary={item.label}
                slotProps={{ primary: { sx: { fontSize: 14, fontWeight: selected ? 650 : 500, color: selected ? 'text.primary' : 'text.secondary' } } }}
              />
            </ListItemButton>
          )
        })}
      </List>

      <Box sx={{ flexGrow: 1 }} />

      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2, px: 1, pb: 1 }}>
        {measured && (
          <Box component={Link} to="/almacenamiento" onClick={onNavigate} sx={{ display: 'block', color: 'inherit', textDecoration: 'none' }}>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 0.75 }}>
              <Typography variant="caption" color="text.secondary">Disco {storage.worstLabel ?? ''}</Typography>
              <Typography variant="caption" sx={{ fontWeight: 600 }}>{Math.round(storage.usedPercent)} %</Typography>
            </Box>
            <Meter value={storage.usedPercent} tone={healthTone(storage.health)} label={`Ocupación del disco ${storage.worstLabel ?? ''}`} />
            <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 0.75 }}>{formatBytes(storage.freeBytes)} libres</Typography>
          </Box>
        )}

        {license && (
          <Tooltip
            placement="right"
            title={<>{license.summary}<br />Código de equipo: {license.machineCode}<br />Canales con licencia: {license.licensedChannels}</>}
          >
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
              <KeyOutlinedIcon sx={t => ({ fontSize: 16, color: license.canStartRecording ? 'text.secondary' : toneColor(t, 'critical') })} />
              <Typography variant="caption" color="text.secondary" noWrap>
                {license.canStartRecording ? license.summary : 'La licencia no permite grabar'}
              </Typography>
            </Box>
          </Tooltip>
        )}

        <Tooltip
          placement="right"
          title={connected
            ? 'Los cambios llegan al instante desde la aplicación.'
            : 'Sin canal en vivo: los datos se consultan cada segundo.'}
        >
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <Dot tone={connected ? 'good' : 'neutral'} />
            <Typography variant="caption" color="text.secondary">{connected ? 'Conectado en vivo' : 'Reconectando…'}</Typography>
          </Box>
        </Tooltip>

        <ThemeSwitch />
      </Box>
    </Box>
  )
}

/** Aviso de disco casi lleno: qué pasa, por qué importa y dónde resolverlo. */
function StorageEmergencyNotice({ label, usedPercent, showLink }: { label: string; usedPercent: number; showLink: boolean }) {
  return (
    <Box
      role="alert"
      sx={t => ({
        display: 'flex',
        flexWrap: 'wrap',
        alignItems: 'center',
        gap: 1.5,
        mb: 3,
        p: 2,
        borderRadius: 3,
        bgcolor: toneTint(t, 'critical', 0.8),
        border: `1px solid ${alpha(toneColor(t, 'critical'), 0.3)}`,
      })}
    >
      <ErrorOutlineRoundedIcon sx={t => ({ color: toneColor(t, 'critical') })} />
      <Box sx={{ flexGrow: 1, minWidth: 220 }}>
        <Typography sx={{ fontWeight: 650 }}>Queda muy poco espacio en el disco {label}</Typography>
        <Typography variant="body2" color="text.secondary">
          Está al {Math.round(usedPercent)} %. Libera espacio para que las grabaciones no se interrumpan.
        </Typography>
      </Box>
      {showLink && <Button component={Link} to="/almacenamiento" variant="outlined" size="small">Ver almacenamiento</Button>}
    </Box>
  )
}

export default function Layout() {
  const theme = useTheme()
  const isDesktop = useMediaQuery(theme.breakpoints.up('md'), { noSsr: true })
  const [drawerOpen, setDrawerOpen] = useState(false)
  const { pathname } = useLocation()
  const { connected } = useLiveEvents()
  const storage = useStorageStatus().data

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh', bgcolor: 'background.default' }}>
      {isDesktop ? (
        <Box
          component="aside"
          sx={{ position: 'sticky', top: 0, flexShrink: 0, width: SIDEBAR_WIDTH, height: '100vh', borderRight: 1, borderColor: 'divider' }}
        >
          <Sidebar connected={connected} />
        </Box>
      ) : (
        <Drawer open={drawerOpen} onClose={() => setDrawerOpen(false)}>
          <Box sx={{ width: SIDEBAR_WIDTH, height: '100%' }}>
            <Sidebar connected={connected} onNavigate={() => setDrawerOpen(false)} />
          </Box>
        </Drawer>
      )}

      <Box component="main" sx={{ flexGrow: 1, minWidth: 0 }}>
        {!isDesktop && (
          <Box
            sx={{
              position: 'sticky',
              top: 0,
              zIndex: 10,
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              px: 1.5,
              py: 1,
              bgcolor: 'background.default',
              borderBottom: 1,
              borderColor: 'divider',
            }}
          >
            <IconButton aria-label="Abrir el menú" onClick={() => setDrawerOpen(true)}><MenuRoundedIcon /></IconButton>
            <Brand />
          </Box>
        )}
        <Box sx={{ maxWidth: 1180, mx: 'auto', px: { xs: 2, sm: 3, md: 5 }, py: { xs: 3, md: 5 } }}>
          {storage?.isEmergency && (
            <StorageEmergencyNotice
              label={storage.worstLabel ?? ''}
              usedPercent={storage.usedPercent}
              showLink={!pathname.startsWith('/almacenamiento')}
            />
          )}
          <Outlet />
        </Box>
      </Box>
    </Box>
  )
}
