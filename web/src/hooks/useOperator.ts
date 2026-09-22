import { useEffect, useState } from 'react'

const KEY = 'baioss.operator'

/**
 * El nombre de quien maneja el panel («Tu nombre»). Queda en la auditoría del Record como quien inició una grabación, le
 * puso nombre o tocó la programación. Se recuerda en este navegador y lo comparten todas las pantallas que lo piden.
 */
export function useOperator(): [string, (value: string) => void] {
  const [operator, setOperator] = useState(() => {
    try { return localStorage.getItem(KEY) ?? '' } catch { return '' }
  })
  useEffect(() => {
    try { localStorage.setItem(KEY, operator) } catch { /* sin almacenamiento local: vale para esta sesión */ }
  }, [operator])
  return [operator, setOperator]
}
