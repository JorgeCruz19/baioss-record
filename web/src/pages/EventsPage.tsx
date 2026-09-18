import { Fragment, useMemo, useState, type ReactNode } from 'react'
import {
  Box, ButtonBase, Card, Collapse, InputAdornment, MenuItem, Select, TextField, ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import SearchRoundedIcon from '@mui/icons-material/SearchRounded'
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord'
import StopRoundedIcon from '@mui/icons-material/StopRounded'
import MovieOutlinedIcon from '@mui/icons-material/MovieOutlined'
import SdStorageOutlinedIcon from '@mui/icons-material/SdStorageOutlined'
import SensorsRoundedIcon from '@mui/icons-material/SensorsRounded'
import GraphicEqRoundedIcon from '@mui/icons-material/GraphicEqRounded'
import ScheduleRoundedIcon from '@mui/icons-material/ScheduleRounded'
import AutoDeleteOutlinedIcon from '@mui/icons-material/AutoDeleteOutlined'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import WarningAmberRoundedIcon from '@mui/icons-material/WarningAmberRounded'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import ExpandMoreRoundedIcon from '@mui/icons-material/ExpandMoreRounded'
import HistoryRoundedIcon from '@mui/icons-material/HistoryRounded'
import { useChannels, useEvents } from '../hooks/queries'
import { ChannelBadge, ConnectionError, EmptyState, PageHeader, RowsSkeleton } from '../components/common'
import {
  categoryLabel, categoryLabels, dayKey, dayLabel, describeEvent, formatClock, formatDate, formatRelative, formatTime, payloadRows, severityLabel,
} from '../api/format'
import type { EventEntry, Severity } from '../api/types'
import { toneColor, toneTint, type Tone } from '../theme'

type Level = '' | 'Warning' | 'Error'

const periods: Array<{ days: number; label: string }> = [
  { days: 1, label: 'Últimas 24 horas' },
  { days: 7, label: 'Últimos 7 días' },
  { days: 30, label: 'Últimos 30 días' },
]

function severityTone(s: Severity): Tone {
  return s === 'Critical' || s === 'Error' ? 'critical' : s === 'Warning' ? 'warning' : 'neutral'
}

/** Icono por TEMA del evento; el color lo pone la severidad (y siempre va con su texto al lado). */
function eventIcon(e: EventEntry): ReactNode {
  const c = e.category
  if (c === 'RecordingStarted' || c === 'RecordingResumed' || c === 'RecordingRecovered') return <FiberManualRecordIcon />
  if (c === 'RecordingStopped' || c === 'RecordingPaused') return <StopRoundedIcon />
  if (c === 'SegmentCompleted' || c === 'RecordingFileUnverified') return <MovieOutlinedIcon />
  if (c.startsWith('Storage')) return <SdStorageOutlinedIcon />
  if (c.startsWith('Signal')) return <SensorsRoundedIcon />
  if (c.startsWith('Audio')) return <GraphicEqRoundedIcon />
  if (c.startsWith('Scheduled')) return <ScheduleRoundedIcon />
  if (c === 'RecordingPurged' || c === 'RetentionSkipped') return <AutoDeleteOutlinedIcon />
  if (e.severity === 'Critical' || e.severity === 'Error') return <ErrorOutlineRoundedIcon />
  if (e.severity === 'Warning') return <WarningAmberRoundedIcon />
  return <InfoOutlinedIcon />
}

function EventRow({ e, channelKey, open, onToggle }: { e: EventEntry; channelKey: (id: string) => string | undefined; open: boolean; onToggle: () => void }) {
  const tone = severityTone(e.severity)
  const description = describeEvent(e)
  const key = e.channelId ? channelKey(e.channelId) : undefined
  const details = useMemo(() => (open ? payloadRows(e, channelKey) : []), [open, e, channelKey])

  return (
    <Box sx={{ borderBottom: 1, borderColor: 'divider', '&:last-of-type': { borderBottom: 0 } }}>
      <ButtonBase
        onClick={onToggle}
        aria-expanded={open}
        sx={t => ({
          display: 'flex',
          alignItems: 'center',
          gap: 1.75,
          width: '100%',
          px: 2.5,
          py: 1.5,
          textAlign: 'left',
          transition: 'background-color .15s',
          '&:hover': { bgcolor: t.palette.action.hover },
          '&.Mui-focusVisible': { bgcolor: t.palette.action.selected },
        })}
      >
        <Box
          aria-hidden
          sx={t => ({
            display: 'grid',
            placeItems: 'center',
            flexShrink: 0,
            width: 34,
            height: 34,
            borderRadius: '50%',
            bgcolor: tone === 'neutral' ? t.palette.action.hover : toneTint(t, tone),
            '& svg': { fontSize: 17, color: tone === 'neutral' ? t.palette.text.secondary : toneColor(t, tone) },
          })}
        >
          {eventIcon(e)}
        </Box>

        <Box sx={{ flexGrow: 1, minWidth: 0 }}>
          <Typography sx={{ fontSize: 14, fontWeight: 600 }} noWrap>
            {categoryLabel(e.category)}
            {tone !== 'neutral' && (
              <Typography component="span" variant="caption" color="text.secondary" sx={{ ml: 1, fontWeight: 500 }}>
                {severityLabel[e.severity]}
              </Typography>
            )}
          </Typography>
          {(description || e.operator) && (
            <Typography variant="body2" color="text.secondary" noWrap>
              {[e.operator, description].filter(Boolean).join(' · ')}
            </Typography>
          )}
        </Box>

        {key && <ChannelBadge letter={key} size={24} />}
        {/* Las entradas ya van agrupadas por día: aquí basta la hora (y, si es de hace poco, cuánto hace). */}
        <Tooltip title={formatDate(e.timestamp)}>
          <Typography variant="caption" color="text.secondary" sx={{ flexShrink: 0, width: 96, textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
            {Date.now() - new Date(e.timestamp).getTime() < 3_600_000 ? formatRelative(e.timestamp) : formatTime(e.timestamp)}
          </Typography>
        </Tooltip>
        <ExpandMoreRoundedIcon sx={{ fontSize: 18, color: 'text.disabled', transition: 'transform .2s', transform: open ? 'rotate(180deg)' : 'none' }} />
      </ButtonBase>

      <Collapse in={open} unmountOnExit>
        <Box sx={{ pl: { xs: 2.5, sm: 8.5 }, pr: 2.5, pb: 2 }}>
          <Box sx={{ display: 'grid', gridTemplateColumns: 'max-content minmax(0, 1fr)', columnGap: 2, rowGap: 0.5 }}>
            <Typography variant="caption" color="text.secondary">Hora</Typography>
            <Typography variant="caption" sx={{ fontVariantNumeric: 'tabular-nums' }}>{formatClock(e.timestamp)}</Typography>
            {details.map(row => (
              <Fragment key={row.label + row.value}>
                <Typography variant="caption" color="text.secondary">{row.label}</Typography>
                <Typography variant="caption" sx={{ wordBreak: 'break-word' }}>{row.value}</Typography>
              </Fragment>
            ))}
            {details.length === 0 && (
              <>
                <Typography variant="caption" color="text.secondary">Detalle</Typography>
                <Typography variant="caption" sx={{ wordBreak: 'break-word' }}>{e.message}</Typography>
              </>
            )}
          </Box>
        </Box>
      </Collapse>
    </Box>
  )
}

export default function EventsPage() {
  const [days, setDays] = useState(7)
  const [channel, setChannel] = useState('')
  const [level, setLevel] = useState<Level>('')
  const [category, setCategory] = useState('')
  const [search, setSearch] = useState('')
  const [openId, setOpenId] = useState<number | null>(null)

  const channels = useChannels()
  const events = useEvents({ days, channel, severity: level, category, take: 1000 })
  const keyMap = useMemo(() => new Map((channels.data ?? []).map(c => [c.channelId, c.key])), [channels.data])
  const channelKey = useMemo(() => (id: string) => keyMap.get(id), [keyMap])

  // La búsqueda es local (sobre lo ya cargado): título, frase, operador y texto original.
  const rows = useMemo(() => {
    const all = events.data ?? []
    const q = search.trim().toLowerCase()
    if (!q) return all
    return all.filter(e => [categoryLabel(e.category), e.category, describeEvent(e), e.operator ?? '', e.message].join(' ').toLowerCase().includes(q))
  }, [events.data, search])

  // Agrupadas por día (la API ya las entrega de la más reciente a la más antigua).
  const groups = useMemo(() => {
    const out: Array<{ key: string; label: string; items: EventEntry[] }> = []
    for (const e of rows) {
      const k = dayKey(e.timestamp)
      const last = out[out.length - 1]
      if (last && last.key === k) last.items.push(e)
      else out.push({ key: k, label: dayLabel(e.timestamp), items: [e] })
    }
    return out
  }, [rows])

  const filtering = Boolean(channel || level || category || search.trim())

  return (
    <Box>
      <PageHeader
        title="Actividad"
        subtitle={events.data ? `${rows.length} ${rows.length === 1 ? 'entrada' : 'entradas'} · quién grabó qué, cuándo y por qué se cortó` : 'El registro de lo que ocurre en el equipo'}
      >
        <ToggleButtonGroup exclusive size="small" value={level} onChange={(_, v: Level | null) => { if (v !== null) setLevel(v) }} aria-label="Importancia">
          <ToggleButton value="">Todo</ToggleButton>
          <ToggleButton value="Warning">Avisos</ToggleButton>
          <ToggleButton value="Error">Errores</ToggleButton>
        </ToggleButtonGroup>
      </PageHeader>

      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, mb: 2.5 }}>
        <TextField
          size="small"
          placeholder="Buscar en la actividad"
          value={search}
          onChange={e => setSearch(e.target.value)}
          sx={{ flexGrow: 1, minWidth: 220 }}
          slotProps={{
            htmlInput: { 'aria-label': 'Buscar en la actividad' },
            input: { startAdornment: <InputAdornment position="start"><SearchRoundedIcon fontSize="small" /></InputAdornment> },
          }}
        />
        <Select size="small" displayEmpty value={category} onChange={e => setCategory(e.target.value)} sx={{ minWidth: 210 }} inputProps={{ 'aria-label': 'Tipo de evento' }}>
          <MenuItem value="">Todos los eventos</MenuItem>
          {Object.entries(categoryLabels).map(([value, label]) => <MenuItem key={value} value={value}>{label}</MenuItem>)}
        </Select>
        <Select size="small" displayEmpty value={channel} onChange={e => setChannel(e.target.value)} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': 'Canal' }}>
          <MenuItem value="">Todos los canales</MenuItem>
          {(channels.data ?? []).map(c => <MenuItem key={c.channelId} value={c.channelId}>Canal {c.key}</MenuItem>)}
        </Select>
        <Select size="small" value={days} onChange={e => setDays(Number(e.target.value))} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': 'Periodo' }}>
          {periods.map(p => <MenuItem key={p.days} value={p.days}>{p.label}</MenuItem>)}
        </Select>
      </Box>

      {events.isPending ? (
        <Card><RowsSkeleton rows={7} /></Card>
      ) : !events.data ? (
        <ConnectionError error={events.error} onRetry={() => void events.refetch()} />
      ) : rows.length === 0 ? (
        <Card>
          <EmptyState
            icon={<HistoryRoundedIcon />}
            title={filtering ? 'Nada coincide con estos filtros' : 'Sin actividad en este periodo'}
            description={filtering
              ? 'Prueba a ampliar el periodo o a quitar algún filtro.'
              : 'Aquí aparecerán los inicios y paradas de grabación, los avisos de disco y de señal, y cualquier fallo.'}
          />
        </Card>
      ) : (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2.5 }}>
          {groups.map(g => (
            <Box key={g.key} component="section" aria-label={g.label}>
              <Typography variant="caption" color="text.secondary" component="h2" sx={{ mb: 1, ml: 0.5, fontWeight: 600 }}>{g.label}</Typography>
              <Card>
                {g.items.map(e => (
                  <EventRow key={e.id} e={e} channelKey={channelKey} open={openId === e.id} onToggle={() => setOpenId(openId === e.id ? null : e.id)} />
                ))}
              </Card>
            </Box>
          ))}
        </Box>
      )}
    </Box>
  )
}
