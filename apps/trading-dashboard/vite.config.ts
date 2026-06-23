import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { VitePWA } from 'vite-plugin-pwa'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue(),
    VitePWA({
      registerType: 'autoUpdate',
      manifest: {
        name: 'RoniAT Trading Dashboard',
        short_name: 'RoniAT',
        start_url: '/',
        display: 'standalone',
        background_color: '#f5f7f8',
        theme_color: '#0f766e',
      },
      devOptions: {
        enabled: false,
      },
    }),
  ],
  server: {
    host: '0.0.0.0',
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5066',
      '/health': 'http://localhost:5066',
      '/hubs': {
        target: 'http://localhost:5066',
        ws: true,
      },
    },
  },
})
