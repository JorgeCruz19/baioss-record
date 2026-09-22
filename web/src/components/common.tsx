import type { ReactNode } from 'react'
import { Box, Button, Card, Skeleton, Typography } from '@mui/material'
import { alpha, keyframes } from '@mui/material/styles'
import CloudOffOutlinedIcon from '@mui/icons-material/CloudOffOutlined'
import RefreshRoundedIcon from '@mui/icons-material/RefreshRounded'
import { toneColor, toneTint, type Tone } from '../theme'
import { errorMessage } from '../api/client'
import { useConnectionDialog } from './ConnectionDialog'
import { useT } from '../i18n'

// Piezas pequeñas de interfaz que comparten todas las pantallas. Regla de color: el estado se comunica con un
// punto o icono de color + una etiqueta en tinta normal (nunca con el color solo, ni tiñendo el texto).

const pulse = keyframes`
  0%   { box-shadow: 0 0 0 0 var(--dot-glow); }
  70%  { box-shadow: 0 0 0 6px transparent; }
  100% { box-shadow: 0 0 0 0 transparent; }
`

/** Punto de estado. `pulsing` queda reservado para «grabando». */
export function Dot({ tone = 'neutral', pulsing = false, size = 8 }: { tone?: Tone; pulsing?: boolean; size?: number }) {
  return (
    <Box
      component="span"
      aria-hidden
      sx={t => ({
        display: 'inline-block',
        flexShrink: 0,
        width: size,
        height: size,
        borderRadius: '50%',
        bgcolor: toneColor(t, tone),
        '--dot-glow': alpha(toneColor(t, tone), 0.55),
        animation: pulsing ? `${pulse} 1.6s ease-out infinite` : 'none',
      })}
    />
  )
}

/** Etiqueta de estado: fondo tintado + punto o icono de color + texto en tinta normal. */
export function StatusTag({ tone = 'neutral', label, icon, pulsing = false }: { tone?: Tone; label: string; icon?: ReactNode; pulsing?: boolean }) {
  return (
    <Box
      component="span"
      sx={t => ({
        display: 'inline-flex',
        alignItems: 'center',
        gap: 0.75,
        height: 26,
        px: 1.25,
        borderRadius: 999,
        bgcolor: tone === 'neutral' ? t.palette.action.hover : toneTint(t, tone),
        color: 'text.primary',
        fontSize: 12.5,
        fontWeight: 600,
        lineHeight: 1,
        whiteSpace: 'nowrap',
        // Una etiqueta larga (el nombre de un preset) se recorta con puntos suspensivos en vez de desbordar la tarjeta.
        minWidth: 0,
        maxWidth: '100%',
        '& svg': { fontSize: 15, flexShrink: 0, color: toneColor(t, tone) },
      })}
    >
      {icon ?? <Dot tone={tone} pulsing={pulsing} />}
      <Box component="span" sx={{ overflow: 'hidden', textOverflow: 'ellipsis', lineHeight: 1.3 }}>{label}</Box>
    </Box>
  )
}

/**
 * Medidor de una proporción frente a un límite. El relleno lleva la severidad (acento → aviso → crítico) y la
 * pista es el MISMO tono muy aclarado, para que el estado se lea en toda la barra. Siempre va junto a su valor escrito.
 */
export function Meter({ value, tone = 'accent', height = 6, label }: { value: number; tone?: Tone; height?: number; label: string }) {
  const v = Math.max(0, Math.min(100, Number.isFinite(value) ? value : 0))
  return (
    <Box
      role="meter"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(v)}
      sx={t => ({ position: 'relative', flexGrow: 1, minWidth: 0, height, borderRadius: height, overflow: 'hidden', bgcolor: toneTint(t, tone, 1.3) })}
    >
      <Box
        sx={t => ({
          position: 'absolute',
          inset: 0,
          width: `${v}%`,
          borderRadius: height,
          bgcolor: toneColor(t, tone),
          transition: 'width .4s ease, background-color .3s ease',
        })}
      />
    </Box>
  )
}

/** Cifra con su etiqueta (en minúsculas de frase, sin dos puntos). `hint` explica la cifra al pasar el ratón. */
export function Metric({ label, value, hint }: { label: string; value: ReactNode; hint?: string }) {
  return (
    <Box sx={{ minWidth: 0 }} title={hint}>
      <Typography variant="caption" color="text.secondary" component="div" noWrap>{label}</Typography>
      <Typography component="div" noWrap sx={{ mt: 0.25, fontSize: 15, fontWeight: 600 }}>{value}</Typography>
    </Box>
  )
}

/** Letra del canal en su insignia. */
export function ChannelBadge({ letter, size = 40 }: { letter: string; size?: number }) {
  const large = size >= 36
  return (
    <Box
      aria-hidden
      sx={t => ({
        display: 'grid',
        placeItems: 'center',
        flexShrink: 0,
        width: size,
        height: size,
        borderRadius: large ? '12px' : '7px',
        bgcolor: toneTint(t, 'accent'),
        color: t.palette.mode === 'dark' ? '#9ec5f4' : '#1c5cab',
        fontWeight: 700,
        fontSize: large ? 17 : 12,
      })}
    >
      {letter}
    </Box>
  )
}

/** Cabecera de pantalla: título, una línea de contexto y, a la derecha, los filtros o acciones. */
export function PageHeader({ title, subtitle, children }: { title: string; subtitle?: ReactNode; children?: ReactNode }) {
  return (
    <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'flex-end', gap: 2, mb: 3 }}>
      <Box sx={{ flexGrow: 1, minWidth: 220 }}>
        <Typography variant="h1">{title}</Typography>
        {subtitle && <Typography color="text.secondary" sx={{ mt: 0.5 }}>{subtitle}</Typography>}
      </Box>
      {children && <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 1.5 }}>{children}</Box>}
    </Box>
  )
}

/** Estado vacío: qué es esto, por qué está vacío y qué hacer. */
export function EmptyState({ icon, title, description, action }: { icon: ReactNode; title: string; description?: ReactNode; action?: ReactNode }) {
  return (
    <Box sx={{ textAlign: 'center', py: 8, px: 3 }}>
      <Box
        sx={t => ({
          display: 'grid',
          placeItems: 'center',
          width: 52,
          height: 52,
          mx: 'auto',
          mb: 2,
          borderRadius: '50%',
          bgcolor: t.palette.action.hover,
          color: 'text.secondary',
        })}
      >
        {icon}
      </Box>
      <Typography variant="h3">{title}</Typography>
      {description && <Typography color="text.secondary" sx={{ mt: 0.75, mx: 'auto', maxWidth: 440 }}>{description}</Typography>}
      {action && <Box sx={{ mt: 2.5 }}>{action}</Box>}
    </Box>
  )
}

/** No se pudo leer de la aplicación: lo que pasó, y un botón para reintentar (además se reintenta solo). */
export function ConnectionError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const { t } = useT()
  const { open: openConnection } = useConnectionDialog()
  return (
    <Card>
      <EmptyState
        icon={<CloudOffOutlinedIcon />}
        title={t('error.title')}
        description={errorMessage(error)}
        action={
          <Box sx={{ display: 'flex', flexWrap: 'wrap', justifyContent: 'center', gap: 1 }}>
            <Button variant="outlined" startIcon={<RefreshRoundedIcon />} onClick={onRetry}>{t('action.retry')}</Button>
            {/* Puede que el Record esté en otra IP o puerto: se cambia aquí mismo, sin archivos ni recompilar. */}
            <Button onClick={openConnection}>{t('action.changeConnection')}</Button>
          </Box>
        }
      />
    </Card>
  )
}

/** Filas de carga para listas y tablas (solo en la primera carga, nunca al refrescar). */
export function RowsSkeleton({ rows = 5 }: { rows?: number }) {
  return (
    <Box sx={{ p: 2.5, display: 'flex', flexDirection: 'column', gap: 2 }}>
      {Array.from({ length: rows }, (_, i) => (
        <Box key={i} sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
          <Skeleton variant="circular" width={32} height={32} />
          <Box sx={{ flexGrow: 1 }}>
            <Skeleton width={`${40 + ((i * 17) % 30)}%`} height={18} />
            <Skeleton width={`${25 + ((i * 11) % 25)}%`} height={14} />
          </Box>
          <Skeleton width={64} height={16} />
        </Box>
      ))}
    </Box>
  )
}
