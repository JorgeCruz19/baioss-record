import { useState, type ReactNode } from 'react'
import {
  Box, Button, Drawer, IconButton, List, ListItemButton, ListItemIcon, ListItemText, ToggleButton, ToggleButtonGroup, Tooltip,
  Typography, useMediaQuery,
} from '@mui/material'
import { alpha, useTheme } from '@mui/material/styles'
import VideocamOutlinedIcon from '@mui/icons-material/VideocamOutlined'
import VideoLibraryOutlinedIcon from '@mui/icons-material/VideoLibraryOutlined'
import EventRepeatRoundedIcon from '@mui/icons-material/EventRepeatRounded'
import HistoryRoundedIcon from '@mui/icons-material/HistoryRounded'
import StorageRoundedIcon from '@mui/icons-material/StorageRounded'
import MenuRoundedIcon from '@mui/icons-material/MenuRounded'
import LightModeOutlinedIcon from '@mui/icons-material/LightModeOutlined'
import DarkModeOutlinedIcon from '@mui/icons-material/DarkModeOutlined'
import SettingsBrightnessOutlinedIcon from '@mui/icons-material/SettingsBrightnessOutlined'
import KeyOutlinedIcon from '@mui/icons-material/KeyOutlined'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import SettingsEthernetRoundedIcon from '@mui/icons-material/SettingsEthernetRounded'
import { Link, Outlet, useLocation } from 'react-router-dom'
import { useLicense, useStorageStatus } from '../hooks/queries'
import { useLiveEvents } from '../hooks/useLiveEvents'
import { formatBytes } from '../api/format'
import { StorageHealth } from '../api/types'
import { toneColor, toneTint, type Tone } from '../theme'
import { Dot, Meter } from './common'
import { useColorMode, type ModePreference } from './ColorMode'
import { useConnectionDialog } from './ConnectionDialog'
import { connectionLabel, useConnection } from '../api/connection'
import { LANGS, setLang, useT, type Key, type Lang } from '../i18n'

const SIDEBAR_WIDTH = 248

// Las rutas se quedan en español (son direcciones, no textos); lo que se ve es la etiqueta traducida.
const NAV: Array<{ to: string; label: Key; icon: ReactNode }> = [
  { to: '/canales', label: 'nav.channels', icon: <VideocamOutlinedIcon fontSize="small" /> },
  { to: '/programacion', label: 'nav.schedule', icon: <EventRepeatRoundedIcon fontSize="small" /> },
  { to: '/grabaciones', label: 'nav.recordings', icon: <VideoLibraryOutlinedIcon fontSize="small" /> },
  { to: '/actividad', label: 'nav.activity', icon: <HistoryRoundedIcon fontSize="small" /> },
  { to: '/almacenamiento', label: 'nav.storage', icon: <StorageRoundedIcon fontSize="small" /> },
]

/** Tono del medidor de disco: acento mientras hay sitio, y de ahí a aviso y a crítico. */
export function healthTone(h: StorageHealth): Tone {
  if (h === StorageHealth.Warning) return 'warning'
  if (h === StorageHealth.Critical || h === StorageHealth.Emergency) return 'critical'
  return h === StorageHealth.Unknown ? 'neutral' : 'accent'
}

function Brand() {
  const { t } = useT()
  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25 }}>
      <Box sx={th => ({ display: 'grid', placeItems: 'center', width: 30, height: 30, borderRadius: '9px', bgcolor: toneColor(th, 'accent') })}>
        <Box sx={{ width: 11, height: 11, borderRadius: '50%', bgcolor: '#ffffff' }} />
      </Box>
      <Box sx={{ lineHeight: 1.15 }}>
        <Typography sx={{ fontSize: 14.5, fontWeight: 700, letterSpacing: '-0.01em' }}>Baioss Record</Typography>
        <Typography variant="caption" color="text.secondary">{t('app.subtitle')}</Typography>
      </Box>
    </Box>
  )
}

function ThemeSwitch() {
  const { t } = useT()
  const { preference, setPreference } = useColorMode()
  return (
    <ToggleButtonGroup
      exclusive
      fullWidth
      size="small"
      value={preference}
      onChange={(_, v: ModePreference | null) => { if (v) setPreference(v) }}
      aria-label={t('theme.label')}
    >
      <ToggleButton value="light" aria-label={t('theme.light')}><Tooltip title={t('theme.light')}><LightModeOutlinedIcon sx={{ fontSize: 17 }} /></Tooltip></ToggleButton>
      <ToggleButton value="dark" aria-label={t('theme.dark')}><Tooltip title={t('theme.dark')}><DarkModeOutlinedIcon sx={{ fontSize: 17 }} /></Tooltip></ToggleButton>
      <ToggleButton value="system" aria-label={t('theme.system')}><Tooltip title={t('theme.system')}><SettingsBrightnessOutlinedIcon sx={{ fontSize: 17 }} /></Tooltip></ToggleButton>
    </ToggleButtonGroup>
  )
}

/** Español / inglés, como en la aplicación de escritorio. Cada idioma se nombra en su propio idioma. */
function LanguageSwitch() {
  const { t, lang } = useT()
  const names: Record<Lang, Key> = { es: 'lang.es', en: 'lang.en' }
  return (
    <ToggleButtonGroup
      exclusive
      fullWidth
      size="small"
      value={lang}
      onChange={(_, v: Lang | null) => { if (v) setLang(v) }}
      aria-label={t('lang.label')}
    >
      {LANGS.map(l => (
        <ToggleButton key={l} value={l} aria-label={t(names[l])} sx={{ fontWeight: 650, letterSpacing: '0.04em' }}>
          <Tooltip title={t(names[l])}><span>{l.toUpperCase()}</span></Tooltip>
        </ToggleButton>
      ))}
    </ToggleButtonGroup>
  )
}

function Sidebar({ connected, onNavigate }: { connected: boolean; onNavigate?: () => void }) {
  const { t } = useT()
  const { pathname } = useLocation()
  const storage = useStorageStatus().data
  const license = useLicense().data
  const measured = storage && storage.health !== StorageHealth.Unknown
  const { connection } = useConnection()
  const { open: openConnection } = useConnectionDialog()
  const label = connectionLabel(connection)

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', p: 2 }}>
      <Box sx={{ px: 1, pt: 1, pb: 3 }}><Brand /></Box>

      <List component="nav" aria-label={t('nav.sections')} disablePadding sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
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
              <ListItemIcon sx={th => ({ minWidth: 32, color: selected ? toneColor(th, 'accent') : 'text.secondary' })}>{item.icon}</ListItemIcon>
              <ListItemText
                primary={t(item.label)}
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
              <Typography variant="caption" color="text.secondary">{t('storage.disk', { label: storage.worstLabel ?? '' })}</Typography>
              <Typography variant="caption" sx={{ fontWeight: 600 }}>{Math.round(storage.usedPercent)} %</Typography>
            </Box>
            <Meter value={storage.usedPercent} tone={healthTone(storage.health)} label={t('storage.diskUsage', { label: storage.worstLabel ?? '' })} />
            <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 0.75 }}>{t('storage.free', { bytes: formatBytes(storage.freeBytes) })}</Typography>
          </Box>
        )}

        {license && (
          <Tooltip
            placement="right"
            title={<>{license.summary}<br />{t('license.machineCode', { code: license.machineCode })}<br />{t('license.channels', { n: license.licensedChannels })}</>}
          >
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
              <KeyOutlinedIcon sx={th => ({ fontSize: 16, color: license.canStartRecording ? 'text.secondary' : toneColor(th, 'critical') })} />
              <Typography variant="caption" color="text.secondary" noWrap>
                {license.canStartRecording ? license.summary : t('license.cannotRecord')}
              </Typography>
            </Box>
          </Tooltip>
        )}

        {/* A qué Record está conectado el panel, y el acceso para cambiar su IP y puerto (se aplica en el acto). */}
        <Tooltip
          placement="right"
          title={<>
            {t('conn.tooltipRecord', { label })}
            <br />{t(connected ? 'conn.live' : 'conn.polling')}
            <br />{t('conn.tooltipChange')}
          </>}
        >
          <Box
            component="button"
            type="button"
            onClick={() => { openConnection(); onNavigate?.() }}
            aria-label={t('conn.aria', { label })}
            sx={{
              all: 'unset', boxSizing: 'border-box', cursor: 'pointer', width: '100%',
              display: 'flex', alignItems: 'center', gap: 1, borderRadius: 1.5, py: 0.5, px: 0.5, mx: -0.5,
              '&:hover': { bgcolor: 'action.hover' },
              '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main' },
            }}
          >
            <Dot tone={connected ? 'good' : 'neutral'} />
            <Box sx={{ minWidth: 0, flexGrow: 1 }}>
              <Typography variant="caption" color="text.secondary" component="div" noWrap>
                {t(connected ? 'conn.statusLive' : 'conn.statusReconnecting')}
              </Typography>
              {/* Solo la dirección: «192.168.100.200:50050» tiene que caber entera en la barra lateral. */}
              <Typography variant="caption" component="div" noWrap sx={{ fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>{label}</Typography>
            </Box>
            <SettingsEthernetRoundedIcon sx={{ fontSize: 16, color: 'text.secondary' }} />
          </Box>
        </Tooltip>

        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
          <ThemeSwitch />
          <LanguageSwitch />
        </Box>
      </Box>
    </Box>
  )
}

/** Aviso de disco casi lleno: qué pasa, por qué importa y dónde resolverlo. */
function StorageEmergencyNotice({ label, usedPercent, showLink }: { label: string; usedPercent: number; showLink: boolean }) {
  const { t } = useT()
  return (
    <Box
      role="alert"
      sx={th => ({
        display: 'flex',
        flexWrap: 'wrap',
        alignItems: 'center',
        gap: 1.5,
        mb: 3,
        p: 2,
        borderRadius: 3,
        bgcolor: toneTint(th, 'critical', 0.8),
        border: `1px solid ${alpha(toneColor(th, 'critical'), 0.3)}`,
      })}
    >
      <ErrorOutlineRoundedIcon sx={th => ({ color: toneColor(th, 'critical') })} />
      <Box sx={{ flexGrow: 1, minWidth: 220 }}>
        <Typography sx={{ fontWeight: 650 }}>{t('storage.emergencyTitle', { label })}</Typography>
        <Typography variant="body2" color="text.secondary">{t('storage.emergencyBody', { percent: Math.round(usedPercent) })}</Typography>
      </Box>
      {showLink && <Button component={Link} to="/almacenamiento" variant="outlined" size="small">{t('storage.viewStorage')}</Button>}
    </Box>
  )
}

export default function Layout() {
  const { t } = useT()
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
            <IconButton aria-label={t('nav.openMenu')} onClick={() => setDrawerOpen(true)}><MenuRoundedIcon /></IconButton>
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
