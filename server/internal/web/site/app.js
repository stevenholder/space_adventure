// Shared plumbing for the account site. Every mutating call carries
// X-Requested-With — the server refuses without it (CSRF).
function api (path, body) {
  return fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'sa-web' },
    body: JSON.stringify(body || {}),
  }).then(async (r) => {
    if (!r.ok) throw new Error((await r.text()).trim() || r.statusText)
    return r.json()
  })
}

function val (id) { return document.getElementById(id).value }

function wire (buttonId, errId, fn) {
  const err = document.getElementById(errId)
  document.getElementById(buttonId).addEventListener('click', () => {
    err.textContent = ''
    err.classList.remove('ok')
    fn().catch((e) => { err.textContent = e.message })
  })
}
