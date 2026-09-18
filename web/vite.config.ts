import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// La API del Record escucha SOLO en loopback (http://127.0.0.1:5005) y no envía cabeceras CORS. En desarrollo
// (`npm run dev`) y en `npm run preview`, las rutas /api y /ws se reenvían a esa dirección, de modo que el
// navegador ve un único origen y no hace falta tocar la API. BAIOSS_API permite apuntar a otra dirección.
const target = process.env.BAIOSS_API ?? 'http://127.0.0.1:5005'
const proxy = {
  '/api': { target, changeOrigin: true },
  '/ws': { target, ws: true, changeOrigin: true },
}

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, proxy },
  preview: { port: 4173, proxy },
})
