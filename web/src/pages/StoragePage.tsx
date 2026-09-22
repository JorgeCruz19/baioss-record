import { useEffect, useState, type ReactNode } from 'react'
import { Box, Button, Card, Divider, InputAdornment, MenuItem, Select, Skeleton, Switch, TextField, Typography } from '@mui/material'
import CheckCircleOutlineRoundedIcon from '@mui/icons-material/CheckCircleOutlineRounded'
import WarningAmberRoundedIcon from '@mui/icons-material/WarningAmberRounded'
import ErrorOutlineRoundedIcon from '@mui/icons-material/ErrorOutlineRounded'
import HelpOutlineRoundedIcon from '@mui/icons-material/HelpOutlineRounded'
import StorageRoundedIcon from '@mui/icons-material/StorageRounded'
import { useFirstLoad, useSaveStorageSettings, useStorageSettings, useStorageStatus } from '../hooks/queries'
import { ConnectionError, EmptyState, Meter, PageHeader, StatusTag } from '../components/common'
import { healthTone } from '../components/Layout'
import { formatBytes, healthLabel } from '../api/format'
import { RetentionAction, StorageHealth, type StorageSettings, type VolumeStatus } from '../api/types'
import { useSnack } from '../components/Snack'
import { errorMessage } from '../api/client'
import { useT } from '../i18n'

function healthIcon(h: StorageHealth): ReactNode {
  if (h === StorageHealth.Ok) return <CheckCircleOutlineRoundedIcon />
  if (h === StorageHealth.Warning) return <WarningAmberRoundedIcon />
  if (h === StorageHealth.Critical || h === StorageHealth.Emergency) return <ErrorOutlineRoundedIcon />
  return <HelpOutlineRoundedIcon />
}

function VolumeCard({ v }: { v: VolumeStatus }) {
  const { t } = useT()
  const tone = healthTone(v.health)
  return (
    <Card sx={{ p: 2.5 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
        <Typography variant="h3" sx={{ flexGrow: 1 }}>{t('storage.disk', { label: v.label })}</Typography>
        <StatusTag tone={tone === 'accent' ? 'good' : tone} icon={healthIcon(v.health)} label={healthLabel(v.health)} />
      </Box>
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, mb: 1.25 }}>
        <Typography component="div" sx={{ fontSize: 30, fontWeight: 650, lineHeight: 1, letterSpacing: '-0.02em' }}>{Math.round(v.usedPercent)} %</Typography>
        <Typography variant="body2" color="text.secondary">{t('storage.used')}</Typography>
      </Box>
      <Meter value={v.usedPercent} tone={tone} height={8} label={t('storage.diskUsage', { label: v.label })} />
      <Typography variant="body2" color="text.secondary" sx={{ mt: 1.25 }}>
        {t('storage.freeOf', { free: formatBytes(v.freeBytes), total: formatBytes(v.totalBytes) })}
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
  const { t } = useT()
  const status = useStorageStatus()
  const firstLoad = useFirstLoad(status)
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
        notify(t('storage.saved'), 'success')
      },
      onError: e => notify(t('storage.saveFailed', { error: errorMessage(e) }), 'error'),
    })
  }
  const onDiscard = () => { setDirty(false); if (settings.data) setForm(settings.data) }

  const volumes = status.data?.volumes ?? []

  return (
    <Box sx={{ pb: dirty ? 10 : 0 }}>
      <PageHeader title={t('nav.storage')} subtitle={t('storage.subtitle')} />

      {firstLoad.loading ? (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, minmax(0, 1fr))' }, gap: 2.5 }}>
          <Card sx={{ p: 2.5 }}><Skeleton width="40%" height={22} /><Skeleton width="30%" height={40} /><Skeleton height={12} /></Card>
        </Box>
      ) : !status.data ? (
        <ConnectionError error={firstLoad.error} onRetry={() => void status.refetch()} />
      ) : volumes.length === 0 ? (
        <Card>
          <EmptyState icon={<StorageRoundedIcon />} title={t('storage.emptyTitle')} description={t('storage.emptyBody')} />
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
              title={t('storage.retention')}
              hint={t('storage.retentionHint')}
              control={<Switch checked={form.retentionEnabled} onChange={e => set('retentionEnabled', e.target.checked)} slotProps={{ input: { 'aria-label': t('storage.retention') } }} />}
            />
            <Divider />
            <SettingRow
              title={t('storage.keepFor')}
              hint={t('storage.keepForHint')}
              control={<NumberField value={form.retentionDays} unit={t('unit.days')} label={t('storage.keepForAria')} onChange={n => set('retentionDays', n)} />}
            />
            <Divider />
            <SettingRow
              title={t('storage.minFree')}
              hint={t('storage.minFreeHint')}
              control={
                <Box sx={{ display: 'flex', gap: 1 }}>
                  <NumberField value={form.minFreeGB} unit="GB" label={t('storage.minFreeGbAria')} onChange={n => set('minFreeGB', n)} width={116} />
                  <NumberField value={form.minFreePercent} unit="%" label={t('storage.minFreePctAria')} onChange={n => set('minFreePercent', n)} width={100} />
                </Box>
              }
            />
            <Divider />
            <SettingRow
              title={t('storage.interval')}
              hint={t('storage.intervalHint')}
              control={<NumberField value={form.intervalMinutes} unit={t('unit.min')} label={t('storage.intervalAria')} onChange={n => set('intervalMinutes', n)} />}
            />
            <Divider />
            <SettingRow
              title={t('storage.action')}
              hint={t(form.action === RetentionAction.Archive ? 'storage.actionArchiveHint' : 'storage.actionDeleteHint')}
              control={
                <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
                  <Select size="small" value={form.action} onChange={e => set('action', Number(e.target.value) as StorageSettings['action'])} sx={{ width: 132 }} inputProps={{ 'aria-label': t('storage.actionAria') }}>
                    <MenuItem value={RetentionAction.Delete}>{t('action.deleteVerb')}</MenuItem>
                    <MenuItem value={RetentionAction.Archive}>{t('action.archive')}</MenuItem>
                  </Select>
                  {form.action === RetentionAction.Archive && (
                    <TextField
                      size="small"
                      placeholder="D:\Archivo"
                      value={form.archivePath ?? ''}
                      onChange={e => set('archivePath', e.target.value || null)}
                      sx={{ width: 260 }}
                      slotProps={{ htmlInput: { 'aria-label': t('storage.archivePathAria') } }}
                    />
                  )}
                </Box>
              }
            />
          </Card>

          <Card sx={{ px: 2.5, py: 1 }}>
            <SettingRow
              title={t('storage.thresholds')}
              hint={t('storage.thresholdsHint')}
              control={
                <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1 }}>
                  <NumberField value={form.warnPercent} unit="%" caption={t('storage.warn')} label={t('storage.warnAria')} onChange={n => set('warnPercent', n)} width={100} />
                  <NumberField value={form.criticalPercent} unit="%" caption={t('storage.critical')} label={t('storage.criticalAria')} onChange={n => set('criticalPercent', n)} width={100} />
                  <NumberField value={form.emergencyPercent} unit="%" caption={t('storage.emergency')} label={t('storage.emergencyAria')} onChange={n => set('emergencyPercent', n)} width={100} />
                </Box>
              }
            />
            <Divider />
            <SettingRow
              title={t('storage.autoCleanup')}
              hint={t('storage.autoCleanupHint')}
              control={<Switch checked={form.autoCleanupOnEmergency} onChange={e => set('autoCleanupOnEmergency', e.target.checked)} slotProps={{ input: { 'aria-label': t('storage.autoCleanupAria') } }} />}
            />
            <Divider />
            <SettingRow
              title={t('storage.blockNew')}
              hint={t('storage.blockNewHint')}
              control={<Switch checked={form.stopNewRecordingsOnEmergency} onChange={e => set('stopNewRecordingsOnEmergency', e.target.checked)} slotProps={{ input: { 'aria-label': t('storage.blockNew') } }} />}
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
            aria-label={t('storage.unsaved')}
            sx={th => ({
              display: 'flex',
              flexWrap: 'wrap',
              alignItems: 'center',
              gap: 1.5,
              pl: 2.5,
              pr: 1.25,
              py: 1.25,
              pointerEvents: 'auto',
              boxShadow: th.palette.mode === 'dark' ? '0 12px 32px rgba(0,0,0,0.6)' : '0 12px 32px rgba(11,11,11,0.14)',
            })}
          >
            <Typography variant="body2" sx={{ fontWeight: 600, mr: 1 }}>{t('storage.unsavedTitle')}</Typography>
            <Button onClick={onDiscard} disabled={save.isPending}>{t('action.discard')}</Button>
            <Button variant="contained" onClick={onSave} disabled={save.isPending}>{t(save.isPending ? 'action.saving' : 'action.saveChanges')}</Button>
          </Card>
        </Box>
      )}
    </Box>
  )
}
