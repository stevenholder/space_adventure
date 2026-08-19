/**
 * Client configuration — URL query parameters are the only config surface.
 *
 *   ?mock      offline mode: no server. A local fake authority (mock/mock.ts)
 *              drives the same prediction/replay path with simulated latency.
 *   ?ws=URL    WebSocket endpoint. Default is same-origin /ws — nginx proxies
 *              /ws to the Go server in production and Vite does the same in
 *              dev (SA_WS_PROXY env, default ws://127.0.0.1:8080).
 *   ?name=...  player name (PROTOCOL hello). Default: Player-<4 hex>.
 *   ?corrupt   dev override: every 5 s the local position/velocity is forced
 *              to a bad value so server-authority correction is observable
 *              (ROADMAP criterion 3). Works in both live and mock mode.
 */
export interface ClientConfig {
  mock: boolean
  wsUrl: string
  name: string
  corrupt: boolean
}

export function parseConfig(search: string): ClientConfig {
  const p = new URLSearchParams(search)
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:'
  const name =
    p.get('name') ?? `Player-${Math.floor(Math.random() * 0x10000).toString(16).padStart(4, '0')}`
  return {
    mock: p.has('mock'),
    wsUrl: p.get('ws') ?? `${proto}//${location.host}/ws`,
    // PROTOCOL: name is length-prefixed UTF-8; keep it short.
    name: name.slice(0, 32),
    corrupt: p.has('corrupt'),
  }
}
