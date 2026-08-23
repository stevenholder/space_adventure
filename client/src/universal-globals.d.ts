// Globals that exist in BOTH runtimes the headless code has to run in — the
// browser and Node — but which TypeScript only ships typings for in lib.dom.
//
// tsconfig.sim.json deliberately omits "DOM" so that touching a browser-only
// API in src/sim or the headless src/net files is a build error rather than a
// crash inside a QA harness (docs/ARCHITECTURE.md, "Client"). Without these
// two declarations that config also rejects TextEncoder/TextDecoder, which are
// WHATWG Encoding and available everywhere — banning them would push the
// codecs into hand-rolled UTF-8, which is worse code for no safety.
//
// Keep this file to APIs that genuinely exist in both runtimes. Anything
// browser-only (window, document, performance.now, requestAnimationFrame)
// must stay unavailable — that prohibition is the whole point.

declare class TextEncoder {
  encode(input?: string): Uint8Array
}

declare class TextDecoder {
  constructor(label?: string, options?: { fatal?: boolean; ignoreBOM?: boolean })
  decode(input?: ArrayBufferView | ArrayBuffer): string
}
