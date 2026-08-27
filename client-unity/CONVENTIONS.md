# Unity client conventions

Phase 3.5 rebuilds the client in Unity (C#) as a packaged **native desktop**
build. Browser delivery is dropped. Scope is renderer, input and assets — the
Go server, the wire protocol, the sim rule tables and the twelve `.mjs`
harnesses are untouched.

These rules exist to keep the thing that has been working — one file, ~150
lines, one verify command — from being destroyed by an engine whose native
unit of work is a binary-ish scene file.

## 0. Why this repo lives on the Windows filesystem

`C:\dev\space_adventure`, reachable from WSL at `/mnt/c/dev/space_adventure`.

Not a preference. **Unity refuses to open a project on a WSL path.** It fatals
inside `EnsureSuitableFileSystem` during `Application::InitializeProject`, and
its crash handler exits 0, so from a shell it looks like a successful run that
did nothing. `\\wsl.localhost\...` is rejected, not merely slow.

The feared cost of relocating did not materialise. Measured on this repo:

| | ext4 | `/mnt/c` |
|---|---|---|
| `dotnet build --no-incremental` | 4.42 s | 3.98 s |
| `go test ./internal/sim` | 1.53 s | 1.74 s |
| `git status` | ~0.0 s | 2.05 s |

Compilation is unaffected; only git's metadata scan pays. Two consequences to
know about:

- **`core.fileMode` is false.** Unity is a Windows process and writes files
  as `755` through drvfs, which would otherwise show as a mode change on every
  file it touches.
- **The filesystem is case-insensitive.** There are no case-only filename
  collisions in the tree today, and adding one would silently lose a file.

## 1. Code-first. No agent authors a scene.

**No agent may create or edit a `.unity` or `.prefab`, or a `.asset` under
`Assets/`.**

The `Assets/` scope is the point. `ProjectSettings/*.asset` are Unity's own
project settings: it writes them on first open and they **must** be committed
or the project has no graphics, physics or input configuration. A `.asset`
under `Assets/` is a different animal — a ScriptableObject someone authored in
the Editor — and that is the thing this rule exists to prevent.

`.meta` files are the exception that proves the rule: Unity generates one for
every file and folder, and they **must** be committed or the GUIDs that link
assets together are regenerated on every clone. They are committed and never
hand-edited. That cannot be enforced mechanically, so it is a review rule.

Unity's native storage is GUID-keyed YAML. Its diffs are unreviewable, its
merges conflict on unrelated edits, and "verify" means opening the Editor —
which no agent here can do. That is a direct collision with the dispatch model
in `.omp/AGENTS.md`.

So: **one near-empty boot scene**, committed once by a human, containing a
single GameObject that runs `Game/Boot.cs`. Every other object, component,
material and camera is created from C# at runtime. A task that seems to need
scene authoring is a main-thread task, and usually it is a sign the work
should be code instead.

`make unity-gate` enforces this (`C47`) over `Assets/` only: exactly one
`.unity` is allowed, `Assets/Scenes/Boot.unity`, and no `.prefab` or `.asset`
at all.
A convention nobody checks lasts about two weeks.

## 2. Three assemblies, and `Sim` may not see the engine

| asmdef | references | may reference `UnityEngine` |
|---|---|---|
| `Sim` | nothing | **no** |
| `Net` | `Sim` | **no** |
| `Game` | `Sim`, `Net`, `UnityEngine` | yes |

`Sim` holds the movement and terrain rules that must agree with the Go server
to 1e-10 m. It must compile and run **headless**, with no Editor and no engine,
because that is the only way the conformance diff can run in CI (`C40`, `C44`).
`Net` is the wire codec: also pure, also headless, so codec parity (`C41`) can
run the same way.

Everything that touches a `GameObject`, a `Transform`, a `Camera` or an
`Input` action lives in `Game`.

## 3. `Sim` carries its own math types

`Sim` defines its own `Vec3`, `Quat` and math helpers. It does **not** use
`UnityEngine.Vector3`.

This is not purism. `Vector3.Normalize` and `Quaternion.Slerp` are not
specified to the bit, they have changed between engine versions, and the
conformance bar is 1e-10 m. The types mirror `client/src/sim/types.ts`
field-for-field so the port stays a transliteration rather than a rewrite, and
so a disagreement is traceable to one line in one file.

`Game` converts at the boundary — `sim.Vec3` in, `UnityEngine.Vector3` out —
and that conversion is the only place the two ever meet.

## 4. Two build systems, one source of truth

The C# lives in `Assets/{Sim,Net,Game}/`. Unity compiles it via the asmdefs.
The headless build compiles the *same files* via `headless/*.csproj`, which
glob them — nothing is copied, nothing is generated.

```
make unity-test        # builds Sim + Net headless and runs the self-checks
dotnet run --project client-unity/headless/SimDump -- --selftest
```

`Sim` and `Net` target `netstandard2.1` so the Unity compiler accepts them
unchanged. Only the headless runners target a modern TFM. The solution is
`headless/SpaceAdventure.Client.slnx` — .NET 10 writes the newer XML solution
format, not `.sln`.

## 5. Verification is a runnable command, not an Editor

Every task states one command that passes or fails. For `Sim` and `Net` that
is `dotnet build` or `dotnet run`. For `Game` — which cannot be verified
without an Editor — the task is main-thread and the verification is the live
stack plus the `.mjs` harnesses, which speak the wire protocol directly and do
not care what renders it.

## 6. No test framework

Checks are `assert`-style console runners that exit non-zero, matching the
`test/*.mjs` harnesses. No NuGet test packages, no fixtures, no per-function
suites. A check that cannot fail is not a check — reintroduce the bug and
watch it go red before you believe it.

## 7. What is not committed

`Library/`, `Temp/`, `Logs/`, `Obj/`, `Build/`, `UserSettings/`, and the
`*.csproj` / `*.sln` that Unity generates at the project root. The `headless/`
projects and their `.slnx` are hand-written and **are** committed; Unity's
generated ones are not. `.meta` files are committed (see rule 1).
