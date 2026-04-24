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

## Install

Add to each executable project you want containerized:

```sh
dotnet add package NMica
```

## Targets

NMica exposes two MSBuild targets. Pick whichever fits your pipeline.

### `PublishContainer` — one-command containerized publish

NMica intercepts the SDK's built-in `PublishContainer` target and produces a multi-layer image. Three output modes, matching the SDK's:

```sh
# 1. Local Docker daemon (default output sink)
dotnet publish /t:PublishContainer \
  -p:ContainerImageName=myapp \
  -p:ContainerImageTags=latest
# → image appears in `docker images` as myapp:latest, ready to `docker run`

# 2. OCI archive tarball (daemonless, self-contained)
dotnet publish /t:PublishContainer \
  -p:ContainerImageName=myapp \
  -p:ContainerImageTags=latest \
  -p:ContainerArchiveOutputPath=./myapp.tar
# → writes an oci-layout tarball to ./myapp.tar; load it with
# `docker load -i`, `podman load -i`, or push with `skopeo copy`

# 3. Remote registry push (currently via local Docker; full daemonless push is Phase 2)
dotnet publish /t:PublishContainer \
  -p:ContainerImageName=myapp \
  -p:ContainerRegistry=myregistry.example.com \
  -p:ContainerImageTags=latest
```

What NMica does on your behalf:

1. Runs `PublishLayer` internally to partition `$(PublishDir)` into `package/`, `earlypackage/`, `project/`, `app/` subdirectories.
2. **For archive output**: pulls the base image's manifest + blobs over HTTPS from the source registry, assembles a new OCI manifest with one layer per populated subdirectory, and writes a self-contained oci-layout tarball. Pure daemonless — no Docker required.
3. **For daemon / remote output**: emits a small multi-COPY `Dockerfile.nmica` next to the publish output and shells to `docker build`. Requires Docker on the build host.

> The daemon-less path is currently OCI-archive-only; direct daemon-less registry push is the next milestone (Phase 2). Until then, remote pushes go through the local Docker daemon.

### `PublishLayer` — low-level layer staging

For users who prefer their own Dockerfile, NMica's `PublishLayer` target reorganizes `$(PublishDir)` into the four change-frequency buckets. You can then COPY each into its own layer from a Dockerfile you control.

```sh
# Split everything into all four layers under ./out
dotnet msbuild /t:PublishLayer \
  -p:DockerLayer=All \
  -p:PublishDir=out/ \
  MyApp.csproj
# → out/package/*.dll, out/earlypackage/*.dll, out/project/*.dll, out/app/*.dll

# Stage only specific layers (combinable comma-separated)
dotnet msbuild /t:PublishLayer \
  -p:DockerLayer=Package,Project \
  -p:PublishDir=out/ \
  MyApp.csproj
```

Then in your Dockerfile:

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

Each `COPY` is its own Docker layer — same caching benefit as the `PublishContainer` path, but the Dockerfile is yours to customize.

## How it layers

Least → most frequently changed:

- `package` — stable NuGet dependency DLLs (changes on dependency upgrades)
- `earlypackage` — pre-release NuGet dependency DLLs (changes on nightly bumps)
- `project` — referenced project DLLs (changes when sibling libs rebuild)
- `app` — your code and anything else (changes every build)

Deeper write-up: https://stakhov.pro/building-efficient-net-docker-images/

## Configuration

| Property | Default | Effect |
|---|---|---|
| `NMicaOverridePublishContainer` | `true` | When set, `PublishContainer` produces a multi-layer image. Turn off to use the SDK's default single-layer behaviour. |
| `GenerateDockerfile` | `false` | Opt in to have a `Dockerfile` regenerated next to the `.csproj` on every build (for users who prefer their own classic `docker build -f Dockerfile` flow; the `PublishLayer` target above is the cleaner alternative). |
| `DockerLayer` | _(all)_ | Passed to `PublishLayer` to pick specific layers. Values: `App`, `Package`, `EarlyPackage`, `Project`, `All` (combinable with commas). |

## Requirements

- Executable project targeting .NET 6 or later
- Built from a solution file
- **For `PublishContainer` with local-daemon or remote-registry output**: Docker available on the build host (Phase 1 fallback path)
- **For `PublishContainer` with `ContainerArchiveOutputPath`**: no daemon required — pure HTTPS pull of the base image + local tarball write

## Roadmap

NMica's `PublishContainer` override is structured to mirror `Microsoft.NET.Build.Containers` (the SDK's internal container machinery) closely. The intent is to upstream this as a feature PR to the SDK once it's complete, at which point NMica itself becomes unnecessary for this use case.

- **Phase 1 (shipped)**: daemonless OCI archive output; Docker-build fallback for daemon and remote push
- **Phase 2 (next)**: direct daemonless registry push via Distribution API (with auth: `config.json` + credential helpers); `docker save` tarball format + `docker load` for local daemon without `docker build`
- **Phase 3**: multi-arch manifests; cross-repo blob mount optimization
