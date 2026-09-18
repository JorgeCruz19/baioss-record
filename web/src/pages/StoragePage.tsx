import { useEffect, useState, type ReactNode } from 'react'
import { Box, Button, Card, Divider, InputAdornment, MenuItem, Select, Skeleton, Switch, TextField, Typography } from '@mui/material'
import CheckCircleOutlineRoundedIcon from '@mui/icons-material/CheckCircleOutlineRounded'
import WarningAmberRoundedIcon from '@mui/icons-material/WarningAmberRounded'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import HelpOutlineRoundedIcon from '@mui/icons-material/HelpOutlineRounded'
import StorageRoundedIcon from '@mui/icons-material/StorageRounded'
import { useSaveStorageSettings, useStorageSettings, useStorageStatus } from '../hooks/queries'
import { ConnectionError, EmptyState, Meter, PageHeader, StatusTag } from '../components/common'
import { healthTone } from '../components/Layout'
import { formatBytes, healthLabel } from '../api/format'
import { RetentionAction, StorageHealth, type StorageSettings, type VolumeStatus } from '../api/types'
import { useSnack } from '../components/Snack'
import { errorMessage } from '../api/client'

function healthIcon(h: StorageHealth): ReactNode {
  if (h === StorageHealth.Ok) return <CheckCircleOutlineRoundedIcon />
  if (h === StorageHealth.Warning) return <WarningAmberRoundedIcon />
  if (h === StorageHealth.Critical || h === StorageHealth.Emergency) return <ErrorOutlineRoundedIcon />
  return <HelpOutlineRoundedIcon />
}

function VolumeCard({ v }: { v: VolumeStatus }) {
  const tone = healthTone(v.health)
  return (
    <Card sx={{ p: 2.5 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
        <Typography variant="h3" sx={{ flexGrow: 1 }}>Disco {v.label}</Typography>
        <StatusTag tone={tone === 'accent' ? 'good' : tone} icon={healthIcon(v.health)} label={healthLabel[v.health] ?? 'Sin medir'} />
      </Box>
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, mb: 1.25 }}>
        <Typography component="div" sx={{ fontSize: 30, fontWeight: 650, lineHeight: 1, letterSpacing: '-0.02em' }}>{Math.round(v.usedPercent)} %</Typography>
        <Typography variant="body2" color="text.secondary">ocupado</Typography>
      </Box>
      <Meter value={v.usedPercent} tone={tone} height={8} label={`Ocupación del disco ${v.label}`} />
      <Typography variant="body2" color="text.secondary" sx={{ mt: 1.25 }}>
        {formatBytes(v.freeBytes)} libres de {formatBytes(v.totalBytes)}
      </Typography>
    </Card>
  )
}

/** Fila de ajuste: qué hace (título + explicación) a la izquierda y el control a la derecha. */
function SettingRow({ title, hint, control }: { title: string; hint?: string; control: ReactNode }) {
  return (
    <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 2, py: 1.75 }}>
      <Box sx={{ flexGrow: 1, flexBasis: 280, minWidth: 0 }}>
        <Typography sx={{ fontSize: 14, fontWeight: 600 }}>{title}</Typography>
        {hint && <Typography variant="body2" color="text.secondary">{hint}</Typography>}
      </Box>
      <Box sx={{ flexShrink: 0 }}>{control}</Box>
    </Box>
  )
}

/** Campo numérico con su unidad; `caption` añade una etiqueta visible encima cuando hay varios campos juntos. */
function NumberField({ value, unit, label, caption, onChange, width = 132 }: {
  value: number; unit: string; label: string; caption?: string; onChange: (n: number) => void; width?: number
}) {
  const field = (
    <TextField
      size="small"
      type="number"
      value={Number.isFinite(value) ? value : 0}
      onChange={e => onChange(e.target.value === '' ? 0 : Number(e.target.value))}
      sx={{ width }}
      slotProps={{
        htmlInput: { min: 0, 'aria-label': label, style: { textAlign: 'right' } },
        input: { endAdornment: <InputAdornment position="end">{unit}</InputAdornment> },
      }}
    />
  )
  if (!caption) return field
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" component="div" sx={{ mb: 0.5 }}>{caption}</Typography>
      {field}
    </Box>
  )
}

export default function StoragePage() {
  const status = useStorageStatus()
  const settings = useStorageSettings()
  const save = useSaveStorageSettings()
  const { notify } = useSnack()
  const [form, setForm] = useState<StorageSettings | null>(null)
  const [dirty, setDirty] = useState(false)

  // El formulario sigue a los ajustes vigentes mientras el operador no haya tocado nada.
  useEffect(() => { if (settings.data && !dirty) setForm(settings.data) }, [settings.data, dirty])

  const set = <K extends keyof StorageSettings>(key: K, value: StorageSettings[K]) => {
    setForm(f => (f ? { ...f, [key]: value } : f))
    setDirty(true)
  }

  const onSave = () => {
    if (!form) return
    save.mutate(form, {
      onSuccess: saved => {
        setForm(saved)
        setDirty(false)
        notify('Ajustes guardados. La aplicación los aplica sin reiniciar.', 'success')
      },
      onError: e => notify(`No se pudieron guardar los ajustes. ${errorMessage(e)}`, 'error'),
    })
  }
  const onDiscard = () => { setDirty(false); if (settings.data) setForm(settings.data) }

  const volumes = status.data?.volumes ?? []

  return (
    <Box sx={{ pb: dirty ? 10 : 0 }}>
      <PageHeader title="Almacenamiento" subtitle="El espacio de los discos donde se graba y qué hacer cuando escasea" />

      {status.isPending ? (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))' }, gap: 2.5 }}>
          <Card sx={{ p: 2.5 }}><Skeleton width="40%" height={22} /><Skeleton width="30%" height={40} /><Skeleton height={12} /></Card>
        </Box>
      ) : !status.data ? (
        <ConnectionError error={status.error} onRetry={() => void status.refetch()} />
      ) : volumes.length === 0 ? (
        <Card>
          <EmptyState icon={<StorageRoundedIcon />} title="Aún no hay discos medidos" description="La aplicación mide los discos de destino cada pocos segundos. Aparecerán aquí enseguida." />
        </Card>
      ) : (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))' }, gap: 2.5 }}>
          {volumes.map(v => <VolumeCard key={v.label} v={v} />)}
        </Box>
      )}

      {form && (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2.5, mt: 4 }}>
          <Card sx={{ px: 2.5, py: 1 }}>
            <SettingRow
              title="Limpieza automática"
              hint="Borra o archiva sola las grabaciones más antiguas. Las marcadas como importantes o protegidas no se tocan nunca."
              control={<Switch checked={form.retentionEnabled} onChange={e => set('retentionEnabled', e.target.checked)} slotProps={{ input: { 'aria-label': 'Limpieza automática' } }} />}
            />
            <Divider />
            <SettingRow
              title="Conservar las grabaciones durante"
              hint="Lo más antiguo se limpia. Con 0 no se limpia por antigüedad."
              control={<NumberField value={form.retentionDays} unit="días" label="Días de conservación" onChange={n => set('retentionDays', n)} />}
            />
            <Divider />
            <SettingRow
              title="Mantener siempre libre al menos"
              hint="Se limpia lo más antiguo hasta recuperar este espacio. Si pones las dos cifras, manda la mayor. Con 0 no se aplica."
              control={
                <Box sx={{ display: 'flex', gap: 1 }}>
                  <NumberField value={form.minFreeGB} unit="GB" label="Espacio libre mínimo en GB" onChange={n => set('minFreeGB', n)} width={116} />
                  <NumberField value={form.minFreePercent} unit="%" label="Espacio libre mínimo en porcentaje" onChange={n => set('minFreePercent', n)} width={100} />
                </Box>
              }
            />
            <Divider />
            <SettingRow
              title="Revisar cada"
              hint="Con 0 se revisa cada 6 horas."
              control={<NumberField value={form.intervalMinutes} unit="min" label="Minutos entre revisiones" onChange={n => set('intervalMinutes', n)} />}
            />
            <Divider />
            <SettingRow
              title="Qué hacer con lo antiguo"
              hint={form.action === RetentionAction.Archive ? 'Se mueve a la carpeta que indiques, en vez de borrarse.' : 'Se borra del disco.'}
              control={
                <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
                  <Select size="small" value={form.action} onChange={e => set('action', Number(e.target.value) as StorageSettings['action'])} sx={{ width: 132 }} inputProps={{ 'aria-label': 'Acción de limpieza' }}>
                    <MenuItem value={RetentionAction.Delete}>Borrar</MenuItem>
                    <MenuItem value={RetentionAction.Archive}>Archivar</MenuItem>
                  </Select>
                  {form.action === RetentionAction.Archive && (
                    <TextField
                      size="small"
                      placeholder="D:\Archivo"
                      value={form.archivePath ?? ''}
                      onChange={e => set('archivePath', e.target.value || null)}
                      sx={{ width: 260 }}
                      slotProps={{ htmlInput: { 'aria-label': 'Carpeta de archivo' } }}
                    />
                  )}
                </Box>
              }
            />
          </Card>

          <Card sx={{ px: 2.5, py: 1 }}>
            <SettingRow
              title="Avisar según lo lleno que esté el disco"
              hint="Tres niveles de ocupación. Con 0, ese nivel no se usa."
              control={
                <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
                  <NumberField value={form.warnPercent} unit="%" caption="Aviso" label="Porcentaje de aviso" onChange={n => set('warnPercent', n)} width={100} />
                  <NumberField value={form.criticalPercent} unit="%" caption="Crítico" label="Porcentaje crítico" onChange={n => set('criticalPercent', n)} width={100} />
                  <NumberField value={form.emergencyPercent} unit="%" caption="Casi lleno" label="Porcentaje de disco casi lleno" onChange={n => set('emergencyPercent', n)} width={100} />
                </Box>
              }
            />
            <Divider />
            <SettingRow
              title="Liberar espacio automáticamente si el disco está casi lleno"
              hint="Al llegar al último nivel se limpian las grabaciones más antiguas que no estén protegidas."
              control={<Switch checked={form.autoCleanupOnEmergency} onChange={e => set('autoCleanupOnEmergency', e.target.checked)} slotProps={{ input: { 'aria-label': 'Liberar espacio cuando el disco esté casi lleno' } }} />}
            />
            <Divider />
            <SettingRow
              title="No empezar grabaciones con el disco casi lleno"
              hint="Las que ya estén en marcha continúan; solo se impide iniciar nuevas."
              control={<Switch checked={form.stopNewRecordingsOnEmergency} onChange={e => set('stopNewRecordingsOnEmergency', e.target.checked)} slotProps={{ input: { 'aria-label': 'No empezar grabaciones con el disco casi lleno' } }} />}
            />
          </Card>
        </Box>
      )}

      {settings.isError && !form && <Box sx={{ mt: 4 }}><ConnectionError error={settings.error} onRetry={() => void settings.refetch()} /></Box>}

      {/* Barra de guardado: aparece solo cuando hay cambios, para que no se pierdan sin querer. */}
      {dirty && (
        <Box sx={{ position: 'fixed', left: 0, right: 0, bottom: 20, zIndex: 20, display: 'flex', justifyContent: 'center', px: 2, pointerEvents: 'none' }}>
          <Card
            role="region"
            aria-label="Cambios sin guardar"
            sx={t => ({
              display: 'flex',
              flexWrap: 'wrap',
              alignItems: 'center',
              gap: 1.5,
              pl: 2.5,
              pr: 1.25,
              py: 1.25,
              pointerEvents: 'auto',
              boxShadow: t.palette.mode === 'dark' ? '0 12px 32px rgba(0,0,0,0.6)' : '0 12px 32px rgba(11,11,11,0.14)',
            })}
          >
            <Typography variant="body2" sx={{ fontWeight: 600, mr: 1 }}>Tienes cambios sin guardar</Typography>
            <Button onClick={onDiscard} disabled={save.isPending}>Descartar</Button>
            <Button variant="contained" onClick={onSave} disabled={save.isPending}>{save.isPending ? 'Guardando…' : 'Guardar cambios'}</Button>
          </Card>
        </Box>
      )}
    </Box>
  )
}
