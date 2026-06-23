import { createApp } from 'vue'
import './style.css'
import App from './App.vue'

if (import.meta.env.DEV && 'serviceWorker' in navigator) {
  void navigator.serviceWorker.getRegistrations()
    .then((registrations) => Promise.all(registrations.map((registration) => registration.unregister())))
    .then(() => 'caches' in window ? caches.keys() : [])
    .then((cacheKeys) => Promise.all(cacheKeys.map((cacheKey) => caches.delete(cacheKey))))
    .catch(() => undefined)
}

createApp(App).mount('#app')
