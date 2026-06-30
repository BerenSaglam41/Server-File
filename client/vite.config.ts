import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig(({ mode }) => {
  // API_TARGET env var ile hedef sunucu değiştirilebilir.
  // Mac'te geliştirme: .env.local dosyasına API_TARGET=http://192.168.64.5:5090 yaz
  // ya da: API_TARGET=http://192.168.64.5:5090 npm run dev -- --host
  const env = loadEnv(mode, process.cwd(), '')
  const apiTarget = env.API_TARGET ?? 'http://localhost:5090'

  return {
    plugins: [react()],
    server: {
      port: 5173,
      host: true,
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
        },
      },
    },
  }
})
