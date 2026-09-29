// Applies the saved theme before first paint to avoid a flash of the wrong theme.
// Loaded as an external script because the CSP forbids inline scripts.
;(function () {
  try {
    var theme = window.localStorage.getItem('agentd.theme')
    if (theme === 'agentd' || theme === 'agentd-dark') {
      document.documentElement.setAttribute('data-theme', theme)
    }
  } catch (e) {
    // Storage unavailable (private mode, blocked): fall back to the system preference.
  }
})()
