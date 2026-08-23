// Package data embeds the gameplay content files so the server binary is
// self-contained (docs/ARCHITECTURE.md, "Server").
//
// The embed lives HERE, next to the JSON, rather than in internal/defs,
// because go:embed cannot reach into a parent directory — a package can only
// embed files at or below its own directory. Keeping the JSON at
// server/data/*.json also keeps every doc reference to those paths correct.
//
// Ownership (.omp/AGENTS.md): `game` owns the *.json in this directory;
// `netcode` owns this file. Adding a content file needs no change here — the
// patterns below are directory-wide.
package data

import "embed"

//go:embed *.json zones/*.json
var FS embed.FS
