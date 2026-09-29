# Client conventions (Godot 4, C#)

The client is a Godot 4 (.NET) desktop build. The Go server, the wire protocol
and the sim rule tables are untouched by anything under this directory; the
`.mjs` harnesses in `test/` speak the wire protocol directly and measure the C#
sim and codec through `simdump/`.

These rules exist to keep the thing that has been working — one file, ~150
lines, one verify command — intact inside an engine whose native unit of work
is a scene. Godot's scene format is text, which makes the rule cheaper to keep
than it was under Unity, not unnecessary.

## 0. Where the repo lives

Anywhere. Godot runs natively on Linux, reads WSL paths, holds no project lock
(`dotnet build` works with the editor open), needs no licence, and exports a
player from the command line. Nothing here requires Windows.

## 1. Code-first. No agent authors a scene.

**No agent may create or edit a `.tscn`, `.tres`, `.res`, `.scn`, `.gd`,
`.material` or `.mesh`, or put a `.glb`/`.gltf` anywhere under `godot/`.**

Exactly one scene exists, `godot/Boot.tscn`, four lines of text committed
once: a single Node carrying `Game/Boot.cs`. Every other node, mesh, material,
camera and light is created from C# at runtime. A task that seems to need
scene authoring is a main-thread task, and usually it is a sign the work
should be code instead.

Why, now that the format is text: a `.tscn` still cannot be *verified* without
the editor, and the dispatch model in `.omp/AGENTS.md` is one `.cs` file with
one command that passes or fails. Models stay in `art/` and are read from
there — a `.glb` under `godot/` would be imported as a scene, with a sidecar
per file, which is exactly the asset debt this rule prevents.

`make godot-gate` enforces this (`C91`): one `.tscn`, no resources, no models
under the project, and no `using Godot` in the shared assemblies.
A convention nobody checks lasts about two weeks.

## 2. Four assemblies, and three may not see the engine

| csproj | references | may reference `Godot` |
|---|---|---|
| `shared/Sim` | nothing | **no** |
| `shared/Net` | `Sim`, Newtonsoft | **no** |
| `shared/Core` | `Sim`, `Net` | **no** |
| `godot/SpaceAdventure` | all three, `GodotSharp` | yes |

`Sim` holds the movement and terrain rules that must agree with the Go server
to 1e-10 m. `Net` is the wire codec and transport. `Core` is prediction, the
render timeline and the other pure arithmetic the game needs. All three build
and run **headless**, with no engine, because that is the only way the
conformance diff (`C40`) and the codec parity (`C41`) can run in CI. They
reference no `GodotSharp`, so a `using Godot;` in any of them is a build
error, not a review note.

Everything that touches a `Node`, a `Transform3D`, a `Camera3D` or `Input`
lives in `godot/Game/`.

## 3. `Sim` carries its own math types, and `Frame.cs` is the only bridge

`Sim` defines its own `Vec3`, `Quat` and math helpers. It does **not** use
`Godot.Vector3`. `Vector3.Normalized` and `Quaternion.Slerp` are not specified
to the bit and the conformance bar is 1e-10 m.

The Sim is right-handed, Y-up, local +Z forward. So is Godot. The conversion
in `godot/Game/Frame.cs` is therefore the identity, and that file is the
**only** place `Sim.Vec3`/`Quat` meet `Godot.Vector3`/`Basis`. The one flip
that remains is the model flip: every `.glb` in `art/` faces −Z (glTF's
convention), so `AssetRegistry` parents each loaded model under one node
carrying `Frame.ModelFlip`, and nowhere else. `Basis.LookingAt` is for the
camera only; entities take the Sim basis verbatim.

## 4. One build system, one source of truth

`dotnet build SpaceAdventure.Client.slnx` compiles all four assemblies,
including the engine-bound one: `Godot.NET.Sdk` pulls `GodotSharp` from
NuGet, so no editor is needed to typecheck `Game/`. The editor and the
exporter invoke the same csproj through the same MSBuild. Nothing is copied,
nothing is generated.

```
make godot-test        # builds everything and runs the self-checks
make godot-dev         # runs from source, headless, against the local server
make godot-build       # exports a player to client/build/<preset>/
```

`Sim`, `Net` and `Core` target `netstandard2.1`; the game targets `net8.0`
(Godot's default). The runtime rolls forward to whatever .NET is installed.

## 5. Verification is a runnable command, not an editor

Every task states one command that passes or fails. For any file under
`client/`, including `Game/`, `dotnet build SpaceAdventure.Client.slnx` is
that command for "does it compile". Behaviour is verified by `make godot-dev`
or `make godot-run` against the live stack plus the `.mjs` harnesses, which
speak the wire protocol directly and do not care what renders it.

Godot .NET reports an unhandled C# exception and keeps running — the same lie
Unity's batchmode told. `Boot.cs` catches in `_Process` and quits non-zero
under `-quitAfter`, and `godot-cli` greps the output for `ERROR:` and
exception traces. Trust the wrapper's exit status, not Godot's.

## 6. No test framework

Checks are `assert`-style console runners that exit non-zero, matching the
`test/*.mjs` harnesses. No NuGet test packages, no fixtures, no per-function
suites. A check that cannot fail is not a check — reintroduce the bug and
watch it go red before you believe it.

## 7. What is not committed

`godot/.godot/` (the import cache and the built assembly), `*.uid`, `bin/`,
`obj/` and `client/build/`.

Committed: `project.godot`, `export_presets.cfg`, every `.csproj`, both
solutions (`SpaceAdventure.Client.slnx` builds everything;
`godot/SpaceAdventure.sln` holds only the game csproj and exists because the
exporter refuses to run without a solution named after the assembly beside
`project.godot`), `Boot.tscn`, `.godot-version`, and the `.import` sidecar of
the one vendored font (the `.meta` analogue: two files, generated once, never
edited).
