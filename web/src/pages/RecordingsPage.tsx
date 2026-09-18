import { useMemo, useState, type ReactNode } from 'react'
import {
  Box, Button, Card, ListItemIcon, ListItemText, Menu, MenuItem, Select, Table, TableBody, TableCell, TableContainer, TableHead,
  TableRow, Typography,
} from '@mui/material'
import PersonOutlineRoundedIcon from '@mui/icons-material/PersonOutlineRounded'
import ScheduleRoundedIcon from '@mui/icons-material/ScheduleRounded'
import CodeRoundedIcon from '@mui/icons-material/CodeRounded'
import HelpOutlineRoundedIcon from '@mui/icons-material/HelpOutlineRounded'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import ShieldRoundedIcon from '@mui/icons-material/ShieldRounded'
import StarRoundedIcon from '@mui/icons-material/StarRounded'
import ExpandMoreRoundedIcon from '@mui/icons-material/ExpandMoreRounded'
import CheckRoundedIcon from '@mui/icons-material/CheckRounded'
import WarningAmberRoundedIcon from '@mui/icons-material/WarningAmberRounded'
import VideoLibraryOutlinedIcon from '@mui/icons-material/VideoLibraryOutlined'
import { Link } from 'react-router-dom'
import { useChannels, useRecordings, useSetProtection } from '../hooks/queries'
import { ChannelBadge, ConnectionError, EmptyState, PageHeader, RowsSkeleton, StatusTag } from '../components/common'
import {
  abnormalStop, formatBytes, formatClock, formatDay, formatDuration, protectionHint, protectionLabel, stopReasonLabel, triggerLabel,
} from '../api/format'
import type { ProtectionLevel, RecordingSummary, Trigger } from '../api/types'
import { useSnack } from '../components/Snack'
import { errorMessage } from '../api/client'
import { toneColor, type Tone } from '../theme'

const levels: ProtectionLevel[] = ['None', 'Important', 'Protected']
const periods: Array<{ days: number; label: string }> = [
  { days: 1, label: 'Últimas 24 horas' },
  { days: 7, label: 'Últimos 7 días' },
  { days: 30, label: 'Últimos 30 días' },
  { days: 90, label: 'Últimos 90 días' },
  { days: 365, label: 'Último año' },
]

const triggerIcon: Record<Trigger, ReactNode> = {
  Manual: <PersonOutlineRoundedIcon />,
  Scheduled: <ScheduleRoundedIcon />,
  Api: <CodeRoundedIcon />,
  Unknown: <HelpOutlineRoundedIcon />,
}

const protectionVisual: Record<ProtectionLevel, { icon: ReactNode; tone: Tone }> = {
  None: { icon: <ShieldOutlinedIcon />, tone: 'neutral' },
  Important: { icon: <StarRoundedIcon />, tone: 'warning' },
  Protected: { icon: <ShieldRoundedIcon />, tone: 'accent' },
}

/** Botón con el nivel actual; al pulsarlo se elige otro, con lo que implica cada uno. */
function ProtectionControl({ value, disabled, onChange }: { value: ProtectionLevel; disabled: boolean; onChange: (l: ProtectionLevel) => void }) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)
  const v = protectionVisual[value]
  return (
    <>
      <Button
        size="small"
        disabled={disabled}
        onClick={e => setAnchor(e.currentTarget)}
        endIcon={<ExpandMoreRoundedIcon />}
        aria-haspopup="menu"
        aria-label={`Protección: ${protectionLabel[value]}. Cambiar`}
        sx={t => ({
          color: 'text.primary',
          fontWeight: 550,
          '& .MuiButton-startIcon svg': { fontSize: 17, color: toneColor(t, v.tone) },
          '& .MuiButton-endIcon svg': { fontSize: 16, color: 'text.disabled' },
        })}
        startIcon={v.icon}
      >
        {protectionLabel[value]}
      </Button>
      <Menu anchorEl={anchor} open={anchor !== null} onClose={() => setAnchor(null)}>
        {levels.map(l => (
          <MenuItem
            key={l}
            selected={l === value}
            onClick={() => { setAnchor(null); if (l !== value) onChange(l) }}
            sx={{ alignItems: 'flex-start', py: 1, maxWidth: 300 }}
          >
            <ListItemIcon sx={t => ({ mt: 0.25, minWidth: 30, '& svg': { fontSize: 18, color: toneColor(t, protectionVisual[l].tone) } })}>
              {protectionVisual[l].icon}
            </ListItemIcon>
            <ListItemText
              primary={protectionLabel[l]}
              secondary={protectionHint[l]}
              slotProps={{ primary: { sx: { fontSize: 13.5, fontWeight: 600 } }, secondary: { sx: { fontSize: 12, whiteSpace: 'normal' } } }}
            />
            {l === value && <CheckRoundedIcon sx={{ ml: 1, mt: 0.25, fontSize: 16, color: 'text.secondary' }} />}
          </MenuItem>
        ))}
      </Menu>
    </>
  )
}

export default function RecordingsPage() {
  const [channel, setChannel] = useState('')
  const [days, setDays] = useState(30)
  const channels = useChannels()
  const recordings = useRecordings({ channel, days })
  const setProtection = useSetProtection()
  const { notify } = useSnack()

  const keyOf = useMemo(() => new Map((channels.data ?? []).map(c => [c.channelId, c.key])), [channels.data])
  const rows = recordings.data ?? []
  const totalBytes = rows.reduce((sum, r) => sum + r.totalBytes, 0)
  const period = periods.find(p => p.days === days)?.label.toLowerCase() ?? ''

  const change = (r: RecordingSummary, level: ProtectionLevel) => setProtection.mutate({ id: r.id, level }, {
    onSuccess: () => notify(level === 'None' ? 'Protección retirada.' : `Grabación marcada como ${protectionLabel[level].toLowerCase()}.`, 'success'),
    onError: e => notify(`No se pudo cambiar la protección. ${errorMessage(e)}`, 'error'),
  })

  return (
    <Box>
      <PageHeader
        title="Grabaciones"
        subtitle={recordings.data
          ? `${rows.length} ${rows.length === 1 ? 'grabación' : 'grabaciones'} · ${formatBytes(totalBytes)} · ${period}`
          : 'El historial de lo grabado en este equipo'}
      >
        <Select size="small" displayEmpty value={channel} onChange={e => setChannel(e.target.value)} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': 'Canal' }}>
          <MenuItem value="">Todos los canales</MenuItem>
          {(channels.data ?? []).map(c => <MenuItem key={c.channelId} value={c.channelId}>Canal {c.key}{c.inputName ? ` · ${c.inputName}` : ''}</MenuItem>)}
        </Select>
        <Select size="small" value={days} onChange={e => setDays(Number(e.target.value))} sx={{ minWidth: 170 }} inputProps={{ 'aria-label': 'Periodo' }}>
          {periods.map(p => <MenuItem key={p.days} value={p.days}>{p.label}</MenuItem>)}
        </Select>
      </PageHeader>

      {recordings.isPending ? (
        <Card><RowsSkeleton rows={6} /></Card>
      ) : !recordings.data ? (
        <ConnectionError error={recordings.error} onRetry={() => void recordings.refetch()} />
      ) : rows.length === 0 ? (
        <Card>
          <EmptyState
            icon={<VideoLibraryOutlinedIcon />}
            title="No hay grabaciones en este periodo"
            description="Cuando grabes un canal, la grabación aparecerá aquí con su duración, tamaño y quién la inició."
            action={<Button component={Link} to="/canales" variant="outlined">Ir a Canales</Button>}
          />
        </Card>
      ) : (
        <Card>
          <TableContainer>
            <Table>
              <TableHead>
                <TableRow>
                  <TableCell>Grabación</TableCell>
                  <TableCell>Canal</TableCell>
                  <TableCell align="right">Duración</TableCell>
                  <TableCell align="right">Tamaño</TableCell>
                  <TableCell>Origen</TableCell>
                  <TableCell>Terminó</TableCell>
                  <TableCell>Protección</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map(r => {
                  const key = keyOf.get(r.channelId)
                  return (
                    <TableRow key={r.id} hover>
                      <TableCell>
                        <Typography sx={{ fontSize: 13.5, fontWeight: 600 }} noWrap>{formatDay(r.startedAt)}</Typography>
                        <Typography variant="caption" color="text.secondary" noWrap component="div" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                          {formatClock(r.startedAt)} – {r.endedAt ? formatClock(r.endedAt) : 'ahora'}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                          <ChannelBadge letter={key ?? '?'} size={24} />
                          {!key && <Typography variant="caption" color="text.secondary">{r.channelId.slice(0, 8)}</Typography>}
                        </Box>
                      </TableCell>
                      <TableCell align="right" sx={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>{formatDuration(r.durationSeconds)}</TableCell>
                      <TableCell align="right" sx={{ whiteSpace: 'nowrap' }}>
                        <Box sx={{ fontVariantNumeric: 'tabular-nums' }}>{formatBytes(r.totalBytes)}</Box>
                        <Typography variant="caption" color="text.secondary">{r.files} {r.files === 1 ? 'archivo' : 'archivos'}</Typography>
                      </TableCell>
                      <TableCell>
                        <StatusTag label={triggerLabel[r.trigger] ?? r.trigger} icon={triggerIcon[r.trigger] ?? triggerIcon.Unknown} />
                        {r.operator && <Typography variant="caption" color="text.secondary" component="div" noWrap sx={{ mt: 0.5, maxWidth: 160 }}>{r.operator}</Typography>}
                      </TableCell>
                      <TableCell>
                        {!r.endedAt
                          ? <StatusTag tone="critical" pulsing label="Grabando" />
                          : abnormalStop.has(r.stopReason)
                            ? <StatusTag tone="warning" icon={<WarningAmberRoundedIcon />} label={stopReasonLabel[r.stopReason]} />
                            : <Typography variant="body2" color="text.secondary">{stopReasonLabel[r.stopReason] ?? r.stopReason}</Typography>}
                      </TableCell>
                      <TableCell sx={{ whiteSpace: 'nowrap' }}>
                        <ProtectionControl value={r.protection} disabled={setProtection.isPending} onChange={l => change(r, l)} />
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </TableContainer>
        </Card>
      )}
    </Box>
  )
}
