import { defineConfig, type Plugin } from 'vite'
import type { IncomingMessage, ServerResponse } from 'node:http'
import { fileURLToPath } from 'node:url'
import fs from 'node:fs'
import path from 'node:path'

// art/ is a sibling of client/ and is a frozen contract we only consume.
// Serve it under /art/ in dev and preview so the client can fetch
// /art/manifest.json and /art/<file> same-origin. In production (scale-out
// milestone) the static host serves /art from the same repo.
const artDir = path.resolve(
  fileURLToPath(new URL('.', import.meta.url)),
  '../art',
)

// Where the /ws proxy points. `make up` (infra) runs the Go server on :8080;
// override with SA_WS_PROXY if the server moves.
const wsTarget = process.env.SA_WS_PROXY ?? 'ws://127.0.0.1:8080'

const MIME: Record<string, string> = {
  '.json': 'application/json',
  '.glb': 'model/gltf-binary',
  '.gltf': 'model/gltf+json',
  '.bin': 'application/octet-stream',
  '.png': 'image/png',
  '.webp': 'image/webp',
}

function artMiddleware(): Plugin {
  const handler = (req: IncomingMessage, res: ServerResponse, next: () => void): void => {
    const url = (req.url ?? '').split('?')[0]
    if (!url.startsWith('/art/')) {
      next()
      return
    }
    const file = path.normalize(path.join(artDir, url.slice('/art/'.length)))
    if (!file.startsWith(artDir + path.sep)) {
      res.statusCode = 403
      res.end()
      return
    }
    fs.readFile(file, (err, buf) => {
      if (err) {
        res.statusCode = 404
        res.setHeader('Content-Type', 'text/plain')
        res.end('not found')
        return
      }
      res.statusCode = 200
      res.setHeader('Content-Type', MIME[path.extname(file).toLowerCase()] ?? 'application/octet-stream')
      res.setHeader('Cache-Control', 'no-cache')
      res.end(buf)
    })
  }
  return {
    name: 'serve-art',
    configureServer(server) {
      server.middlewares.use(handler)
    },
    configurePreviewServer(server) {
      server.middlewares.use(handler)
    },
  }
}

const wsProxy = { '/ws': { target: wsTarget, ws: true } }

export default defineConfig({
  server: {
    port: 5173,
    proxy: wsProxy,
  },
  preview: {
    port: 5173,
    proxy: wsProxy,
  },
  plugins: [artMiddleware()],
  build: {
    target: 'es2022',
    outDir: 'dist',
  },
})
