import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  base: '/explorer/',
  plugins: [react()],
  server: {
    proxy: {
      '/dbs': {
        target: 'http://localhost:8081',
        changeOrigin: true,
      },
      '/api': {
        target: 'http://localhost:8081',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: '../../src/Host/wwwroot/explorer',
    emptyOutDir: true,
  },
})
