/**
 * Client identity token (docs/PROTOCOL.md, "Identity token").
 *
 * An opaque, client-generated bearer string the server uses to look up (or
 * create) a persistent player row. Generated once with
 * crypto.getRandomValues (never Math.random — this is the key to a
 * player's persistent row) and kept in localStorage so a reconnect on the
 * same machine restores the same player.
 *
 * localStorage is unavailable in some contexts (a private window, blocked
 * site data) and can throw on read or write there. This module must never
 * throw: a storage exception falls back to "" (an ephemeral session that is
 * simply not saved) rather than stopping the game from loading. DOM-only —
 * kept out of tsconfig.sim.json's headless include for that reason.
 */

const STORAGE_KEY = 'sa.token'

// 32 lowercase hex chars, matching what this module generates. Anything
// else stored under the key is treated as absent rather than trusted.
const TOKEN_RE = /^[0-9a-f]{32}$/

function readStoredToken(): string | null {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    if (stored !== null && TOKEN_RE.test(stored)) return stored
    return null
  } catch {
    return null
  }
}

function generateToken(): string {
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  let hex = ''
  for (const b of bytes) hex += b.toString(16).padStart(2, '0')
  return hex
}

function storeToken(token: string): void {
  try {
    localStorage.setItem(STORAGE_KEY, token)
  } catch {
    // Storage unavailable or full: the token still works for this
    // session, it just won't be there on the next visit.
  }
}

/**
 * Returns this browser's persistent identity token, generating and storing
 * one on first use. Returns "" if localStorage is unavailable — the caller
 * sends that as the hello token, which the server treats as absent (an
 * ephemeral, unsaved session).
 */
export function getToken(): string {
  try {
    if (typeof localStorage === 'undefined') return ''
    const existing = readStoredToken()
    if (existing !== null) return existing
    const token = generateToken()
    storeToken(token)
    return token
  } catch {
    return ''
  }
}
