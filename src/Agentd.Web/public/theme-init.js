// Applies the saved theme before first paint to avoid a flash of the wrong theme.
// Loaded as an external script because the CSP forbids inline scripts.
// daisyUI reads data-theme; Nuxt UI reads the `dark` class (kept in step with the system while the choice is "system").
;(function () {
  var theme = null
  try {
    theme = window.localStorage.getItem('agentd.theme')
  } catch (e) {
    // Storage unavailable (private mode, blocked): fall back to the system preference.
  }
  var root = document.documentElement
  if (theme === 'agentd' || theme === 'agentd-dark') {
    root.setAttribute('data-theme', theme)
  }
  var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null
  function sync() {
    var choice = root.getAttribute('data-theme')
    root.classList.toggle('dark', choice === 'agentd-dark' || (!choice && !!media && media.matches))
  }
  sync()
  if (media && media.addEventListener) media.addEventListener('change', sync)
})()
