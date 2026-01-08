import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vitejs.dev/config/
const devPort = 5180
const apiTarget = 'http://127.0.0.1:5179'

export default defineConfig({
  plugins: [vue()],
  server: {
    host: true,
    port: devPort,
    strictPort: true,
    cors: true,
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, '')
      }
    }
  },
  preview: {
    host: true,
    port: devPort,
    strictPort: true,
    cors: true,
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, '')
      }
    }
  }
})


