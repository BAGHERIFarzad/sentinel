import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { '/api': { target: process.env.SENTINEL_API || 'http://localhost:5080', changeOrigin: true } }
  }
})
