/**
 * Asset loading by manifest id (art/manifest.json, served at /art/).
 *
 * The manifest is a contract, not an inventory (art/README): a `.glb` that
 * does not exist yet is normal, never an error — callers fall back to
 * placeholder geometry so the client never blocks on art.
 */
import type { Object3D } from 'three'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'

export interface AssetEntry {
  id: string
  file: string
  tris: number
}

export class AssetLib {
  private entries = new Map<string, AssetEntry>()
  private loader = new GLTFLoader()
  private gltfCache = new Map<string, Object3D>()

  /** Fetch the manifest. A missing manifest means "no art yet" — all
   *  placeholder, not a failure. */
  async loadManifest(): Promise<void> {
    try {
      const res = await fetch('/art/manifest.json')
      if (!res.ok) return
      const json = (await res.json()) as { version: number; assets: AssetEntry[] }
      for (const a of json.assets) this.entries.set(a.id, a)
    } catch {
      // offline / no art served — placeholder path takes over
    }
  }

  entry(id: string): AssetEntry | undefined {
    return this.entries.get(id)
  }

  /**
   * Load the `.glb` for `id`; resolves to the gltf scene, or null when the
   * id is unknown, the file is missing, or the load fails. Never rejects.
   */
  loadGltf(id: string): Promise<Object3D | null> {
    const cached = this.gltfCache.get(id)
    if (cached) return Promise.resolve(cached)
    const e = this.entries.get(id)
    if (!e) return Promise.resolve(null)
    return new Promise((resolve) => {
      this.loader.load(
        `/art/${e.file}`,
        (gltf) => {
          this.gltfCache.set(id, gltf.scene)
          resolve(gltf.scene)
        },
        undefined,
        () => resolve(null), // missing file: normal in M1
      )
    })
  }

  /** First mesh geometry under `node`, or null. */
  static firstGeometry(node: Object3D | null): Object3D | null {
    if (!node) return null
    let found: Object3D | null = null
    node.traverse((o) => {
      if (found === null && (o as { isMesh?: boolean }).isMesh) found = o
    })
    return found
  }
}
