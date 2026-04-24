# CLAUDE.md

Orientation for AI assistants working in this repo. For human-readable project docs, see
`README.md`.

## What NMica is (30-second version)

An MSBuild task that makes `dotnet publish /t:PublishContainer` produce a multi-layer
container image instead of the single-layer one the .NET SDK ships by default. The
implementation is almost entirely the SDK's own container machinery (linked in as a
submodule — see below); our actual contribution is one loop in
`src/NMica/Tasks/CreateNewImageOverride.cs` that iterates four layer subdirectories and
calls `Layer.FromDirectory` + `ImageBuilder.AddLayer` once per bucket instead of once on
`$(PublishDir)`.

If you only read one file to understand the feature, read that one.

## Directory map

| Path | Status | Notes |
|---|---|---|
| `src/NMica/Tasks/` | **edit** | Our MSBuild tasks. ~500 LoC total. |
| `src/NMica/build/` | **edit** | `NMica.props` + `NMica.targets` — NuGet-consumer-side MSBuild wiring (UsingTask override + the `BeforeTargets="_PublishSingleContainer"` layer-prep hook). |
| `src/NMica/Vendor/` | **edit** | Two shim files (`CliUtilsShim.cs`, `MSBuildLogger.cs`) that let linked SDK sources compile without their unpublished helper libs. Keep tiny. |
| `tests/NMica.Tests/` | **edit** | TUnit tests. Real Docker integration — tests run inside / against real containers. |
| `external/dotnet-sdk/` | **⚠️ SUBMODULE — read only** | See next section. |
| `build/` | edit rarely | Nuke — deployment orchestration only (Pack / Release / etc.), not needed for feature work. |

## The submodule — `external/dotnet-sdk/`

**This directory is a sparse + shallow git submodule of `dotnet/sdk` pinned at a specific
tag.** Only `src/Containers/Microsoft.NET.Build.Containers/` is checked out (~3.7 MB of
the SDK repo's 1.5 GB).

Files under this tree are compiled *into* `NMica.dll` via `<Compile Include>` globs in
`src/NMica/NMica.csproj`. They are **not** NMica's code — they're upstream Microsoft code
that we vendor verbatim, by reference, so we stay trivially in sync with upstream and
NMica can be deleted when the feature eventually lands upstream.

### Rules

1. **Never modify files under `external/dotnet-sdk/`.** Submodule changes don't persist
   across `submodule update` and will confuse the next contributor. If you need to patch
   something, add a "shadow" file in `src/NMica/Vendor/` (MSBuild last-wins semantics) or
   open an upstream PR against `dotnet/sdk`.
2. **Do not go exploring this tree to "understand the project."** Nothing in it is
   NMica-specific. If you find yourself reading files there, the only legitimate reason
   is: *you're already looking at a specific type name referenced from our code and want
   to see its implementation*. Even then, one file deep, not a tour.
3. The linked files' `using` directives assume global usings from the SDK's
   `Directory.Build.props` (`System.Runtime.InteropServices`, `System.Text`,
   `System.Xml.Linq`). These are mirrored in `src/NMica/NMica.csproj` — do not add or
   remove without a reason.

### When the submodule is worth touching

- Bumping the pinned version (`git -C external/dotnet-sdk checkout vX.Y.Z`)
- Re-examining a specific SDK type because our shim stopped compiling against it after a
  version bump

That's it.

## Target framework

- `NMica.dll`: **`net10.0`** (not older). The linked SDK sources use `Convert.ToHexStringLower`,
  which is .NET 9+; we picked net10 because SDK 10 is the minimum we actually support
  anyway.
- User projects consuming NMica: any TFM with a published `mcr.microsoft.com/dotnet/runtime`
  image (net6+).
- Test project: `net10.0` with `<RollForward>LatestMajor</RollForward>`.

## Build & test

```sh
# Everything
dotnet build NMica.sln
dotnet test --project tests/NMica.Tests/NMica.Tests.csproj   # needs Docker daemon

# Force fresh NMica package (NuGet caches the 1.0.0-test nupkg aggressively)
rm -rf ~/.nuget/packages/nmica artifacts src/NMica/bin src/NMica/obj
```

The test suite is ~15 tests, ~27s wall clock, real Docker integration (pulls base images,
runs `docker build`, inspects tarballs). A clean rebuild from a fresh checkout takes ~1
minute including docker image warm-up.

## Idioms that aren't obvious from the code

- **TUnit, not xUnit.** `[Test]`, `[Arguments(...)]`, `[MethodDataSource(...)]`,
  `[Before(HookType.Test)]`. Data sources for mutable types return `IEnumerable<Func<T>>`
  (TUnit0046 enforces a fresh instance per invocation).
- **Tests run in parallel by default** across and within classes. Each test gets its own
  temp dir via `BaseTests.SetupTestDir`.
- **Tasks inherit `Microsoft.Build.Utilities.Task` directly.** No `ContextAwareTask` /
  `AssemblyLoadContext` isolation — there's no bundled dependency (no `Newtonsoft.Json`,
  etc.) to protect from MSBuild's version.
- **Every `PackageReference` uses `ExcludeAssets="runtime"`.** We compile against the SDK's
  deps; at runtime the SDK's own loaded copies serve. The NMica NuGet ships exactly one
  file: `tasks/net10.0/NMica.dll`.
- **Our shadow task lives in the `Microsoft.NET.Build.Containers.Tasks` namespace
  intentionally** — so MSBuild's last-UsingTask-wins rule routes the SDK's
  `_PublishSingleContainer` call into our type instead of theirs.
- **PublishLayer classifies via MSBuild item metadata** (`%(NuGetPackageVersion)` on
  `@(ResolvedFileToPublish)`), not by parsing `project.assets.json`. Don't regress to JSON
  parsing.
- **Layering is disabled automatically for `PublishAot=true` / `PublishTrimmed=true`.**
  Those modes defeat the caching invariant; `NMica.targets` computes
  `_NMicaIncompatibleReason` and falls back to the SDK's single-layer path with a
  warning. Don't add a code path that "works around" this — the fallback IS the correct
  behavior.

## Common anti-patterns to avoid

- Reimplementing tar/gzip/registry/auth code in `src/NMica/`. It's already there, linked
  from the submodule. Use the linked types.
- Adding `Newtonsoft.Json` (or any other runtime-visible dependency) back to
  `NMica.csproj`. The package is designed to ship zero bundled DLLs.
- Editing `external/dotnet-sdk/`. See the rules above.
- Running tests without Docker. They will fail fast but in a confusing way.
- Assuming `netstandard2.0` is still the "right" TFM for an MSBuild task. It's not —
  .NET Framework MSBuild support was dropped earlier; `net10.0` is correct.
