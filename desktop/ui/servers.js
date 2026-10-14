// The servers page (docs/architect/desktop.md §1): your saved agentd servers; opening one points the window at it.
// The app opens the last server at start, unless you came back here from the menu (Servers…, ?list).
const { invoke } = window.__TAURI__.core

const $ = (id) => document.getElementById(id)
let servers = []

function show(error) {
  $('error').hidden = !error
  $('error').textContent = error ?? ''
}

function render() {
  const list = $('servers')
  list.replaceChildren()
  $('empty').hidden = servers.length > 0
  servers.forEach((s, i) => {
    const li = document.createElement('li')
    const open = document.createElement('button')
    open.className = 'server'
    open.type = 'button'
    const name = document.createElement('strong')
    name.textContent = s.name
    const url = document.createElement('span')
    url.className = 'muted'
    url.textContent = s.url
    open.append(name, url)
    open.addEventListener('click', () => void openServer(s))

    const remove = document.createElement('button')
    remove.type = 'button'
    remove.className = 'ghost'
    remove.textContent = 'Remove'
    remove.setAttribute('aria-label', `Remove ${s.name}`)
    remove.addEventListener('click', () => void save(servers.filter((_, j) => j !== i)))
    li.append(open, remove)
    list.append(li)
  })
}

async function save(list) {
  try {
    servers = (await invoke('servers_save', { list })).servers
    show(null)
    render()
    return true
  } catch (err) {
    show(String(err))
    return false
  }
}

async function openServer(s) {
  $('status').textContent = `Opening ${s.name}…`
  try {
    await invoke('server_open', { url: s.url })
  } catch (err) {
    $('status').textContent = ''
    show(String(err))
  }
}

$('add').addEventListener('submit', async (e) => {
  e.preventDefault()
  const form = e.currentTarget
  const data = new FormData(form)
  if (await save([...servers, { name: String(data.get('name')), url: String(data.get('url')) }])) form.reset()
})

try {
  const saved = await invoke('servers_list')
  servers = saved.servers
  render()
  const last = servers.find((s) => s.url === saved.last)
  if (last && !new URLSearchParams(location.search).has('list')) await openServer(last)
} catch (err) {
  show(String(err))
}
