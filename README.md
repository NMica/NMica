# NMica

**Multi-layer, cache-optimized container images for .NET — out of the box with `dotnet publish /t:PublishContainer`.**

The .NET SDK's `PublishContainer` target is convenient but produces a single Docker layer containing your entire `dotnet publish` output — code + every NuGet DLL + every project reference. One line of code change invalidates the whole layer: tens to hundreds of MB of unchanged dependency DLLs get re-hashed, re-pushed to your registry, re-pulled by CI and prod, and re-stored on every host.

Install NMica and `PublishContainer` now produces an image with **four** layers, ordered by how often each piece actually changes. Iterative rebuilds push only the one layer whose content moved.

## Default vs NMica

Hypothetical project: `MyApp.dll` (~200 KB), references `classlib.dll` (~80 KB), depends on ~40 NuGet packages (~60 MB). Numbers are approximate; your mileage will vary, but the ratio is typical.

| | SDK default | With NMica |
|---|---|---|
| **Image layers (app-specific)** | 1 | 4 |
| Layer 1 | `publish/*` — 60 MB | `package/*.dll` — 58 MB (stable NuGet) |
| Layer 2 | | `earlypackage/*.dll` — 2 MB (pre-release NuGet) |
| Layer 3 | | `project/*.dll` — 80 KB (referenced projects) |
| Layer 4 | | `app/*.dll` — 200 KB (your code) |
| **Delta pushed on code-only change** | ~60 MB | ~200 KB |
| **Delta pushed on dep upgrade** | ~60 MB | ~58 MB (only package layer rebuilds) |
| **Disk for 10 rebuilds of same app** | ~600 MB (10 × 60 MB) | ~62 MB (60 MB shared + 10 × ~200 KB) |

Pulls, pushes, and registry/host storage all dedupe by layer digest. Fewer, more-granular layers means fewer bytes over the wire and fewer bytes on disk.

## How it layers

Least → most frequently changed:

- `package` — stable NuGet dependency DLLs (changes on dependency upgrades)
- `earlypackage` — pre-release NuGet dependency DLLs (changes on nightly bumps)
- `project` — referenced project DLLs (changes when sibling libs rebuild)
- `app` — your code and anything else (changes every build)

## Install

Add to each executable project you want containerized:

```sh
dotnet add package NMica
```

## Targets

NMica exposes two MSBuild targets. Pick whichever fits your pipeline.

### `PublishContainer` — one-command containerized publish

NMica intercepts the SDK's built-in `PublishContainer` target and produces a multi-layer image via the SDK's own OCI machinery — all three sinks the SDK supports work unchanged:

```sh
# 1. Local container daemon (Docker or Podman; default sink)
dotnet publish /t:PublishContainer \
  -p:ContainerRepository=myapp \
  -p:ContainerImageTags=latest
# → image appears in `docker images` / `podman images` as myapp:latest

# 2. OCI archive tarball (fully daemonless, self-contained)
dotnet publish /t:PublishContainer \
  -p:ContainerRepository=myapp \
  -p:ContainerImageTags=latest \
  -p:ContainerArchiveOutputPath=./myapp.tar
# → writes an oci-layout tarball; load with `docker load -i`, `podman load -i`,
#   or push with `skopeo copy oci-archive:myapp.tar docker://<registry>/myapp`

# 3. Remote registry push (fully daemonless — Distribution API over HTTPS)
dotnet publish /t:PublishContainer \
  -p:ContainerRepository=myapp \
  -p:ContainerRegistry=myregistry.example.com \
  -p:ContainerImageTags=latest
# → pushes blobs + manifest directly; authenticates via ~/.docker/config.json
#   and installed credential helpers (ECR, GCR, ACR, etc.)
```

Under the hood: NMica hooks `BeforeTargets="_PublishSingleContainer"` to partition `$(PublishDir)` into `package/`, `earlypackage/`, `project/`, `app/` subdirectories (via MSBuild item metadata — no `project.assets.json` parsing). Then the SDK's own `CreateNewImage` task is redirected (via `<UsingTask>` override) to a NMica shadow that iterates those subdirectories and calls `Layer.FromDirectory` + `ImageBuilder.AddLayer` once per bucket instead of once on the whole `$(PublishDir)`. The rest of the pipeline — base-image pull, manifest/config assembly, push/archive/load — runs unmodified SDK code linked in as a git submodule. See [Build (contributors)](#build-contributors).

### `PublishLayer` + `GenerateDockerfile` — classic Dockerfile flow

If you'd rather ship a `Dockerfile` in your repo and run `docker build` yourself, NMica has you covered with two pieces that work together:

- **`GenerateDockerfile`** writes a ready-to-use multi-stage Dockerfile next to your `.csproj`. Off by default; opt in by setting `<GenerateDockerfile>true</GenerateDockerfile>` in your project (or pass `-p:GenerateDockerfile=true` to a build).
- **`PublishLayer`** is the runtime-side target the Dockerfile invokes inside its build stage — it partitions `$(PublishDir)` into the four buckets so each gets its own `COPY`.

#### Let NMica generate the Dockerfile for you

```xml
<!-- in MyApp.csproj -->
<PropertyGroup>
  <GenerateDockerfile>true</GenerateDockerfile>
</PropertyGroup>
```
which will generate something like this:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet msbuild /t:PublishLayer /p:DockerLayer=All /p:PublishDir=/out MyApp.csproj

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out/package/ ./
COPY --from=build /out/earlypackage/ ./
COPY --from=build /out/project/ ./
COPY --from=build /out/app/ ./
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

- 

Classification is driven entirely by MSBuild item metadata that the SDK's restore pipeline already computes (`%(NuGetPackageVersion)` on `@(ResolvedFileToPublish)`) — no `project.assets.json` parsing involved.

Deeper write-up: https://stakhov.pro/building-efficient-net-docker-images/

## Configuration

| Property | Default | Effect |
|---|---|---|
| `NMicaOverridePublishContainer` | `true` | When set, `PublishContainer` produces a multi-layer image. Turn off to use the SDK's default single-layer behaviour. |
| `GenerateDockerfile` | `false` | Opt in to have a multi-stage `Dockerfile` regenerated next to the `.csproj` on every build. Pairs with `PublishLayer` — the generated Dockerfile invokes `PublishLayer` in its build stage. Leave off if you use `PublishContainer` or ship your own Dockerfile. |
| `DockerLayer` | _(all)_ | Passed to `PublishLayer` to pick specific layers. Values: `App`, `Package`, `EarlyPackage`, `Project`, `All` (combinable with commas). |

## AOT and trimming

NMica's layering relies on dependency DLLs being separate files with stable per-package content. That invariant breaks under:

- `PublishAot=true` — the output is a single native binary; there are no dependency DLLs to layer.
- `PublishTrimmed=true` — the trimmer rewrites dependency DLL content based on which methods the app's code actually reaches, so the "package" layer changes on every app build and caching does nothing.

In both cases NMica detects the mode, emits a build warning explaining the situation, and falls back to the SDK's default single-layer `PublishContainer` behavior. No configuration needed — it just does the right thing. Set `<NMicaOverridePublishContainer>false</NMicaOverridePublishContainer>` to silence the warning if you don't want to hear about it.

## Requirements

- **.NET 10 SDK or newer

## Build (contributors)

```sh
git clone --recurse-submodules --shallow-submodules <this-repo-url>
cd <repo>
dotnet build
dotnet test
```

NMica source-links parts of [`dotnet/sdk`](https://github.com/dotnet/sdk) as a submodule at `external/dotnet-sdk/`; `--shallow-submodules` keeps the clone fast (skipping the SDK's ~1.5 GB history). If you already cloned without it, `git submodule update --init --depth 1` retrofits.

## Roadmap

NMica's `PublishContainer` override is structured to mirror `Microsoft.NET.Build.Containers` (the SDK's internal container machinery) as closely as possible — the Containers sources are linked directly from a pinned `dotnet/sdk` submodule rather than reimplemented. The intent is to upstream this as a feature PR to the SDK once it's stable, at which point NMica becomes unnecessary for this use case.

- **Phase 1** (superseded): hand-rolled OCI archive writer + docker-build fallback
- **Phase 2** (shipped): submodule-link the SDK's `Microsoft.NET.Build.Containers` sources directly. All three sinks (local daemon, OCI archive, remote registry push) go through the SDK's proven machinery with full credential-helper + auth support; our only contribution is the "iterate layer buckets" change in the `CreateNewImage` shadow task
- **Phase 3** (next): upstream PR against `dotnet/sdk` exposing the multi-layer capability as a first-class `PublishContainer` feature, making NMica unnecessary
