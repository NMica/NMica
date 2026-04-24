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

### `PublishLayer` — low-level layer staging

For users who prefer to author their own Dockerfile, NMica's `PublishLayer` target reorganizes `$(PublishDir)` into the four buckets. You COPY each as its own `RUN` layer.

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

Each `COPY` becomes its own Docker layer — same caching benefit as the `PublishContainer` path, but the Dockerfile is yours to customize.

## How it layers

Least → most frequently changed:

- `package` — stable NuGet dependency DLLs (changes on dependency upgrades)
- `earlypackage` — pre-release NuGet dependency DLLs (changes on nightly bumps)
- `project` — referenced project DLLs (changes when sibling libs rebuild)
- `app` — your code and anything else (changes every build)

Classification is driven entirely by MSBuild item metadata that the SDK's restore pipeline already computes (`%(NuGetPackageVersion)` on `@(ResolvedFileToPublish)`) — no `project.assets.json` parsing involved.

Deeper write-up: https://stakhov.pro/building-efficient-net-docker-images/

## Configuration

| Property | Default | Effect |
|---|---|---|
| `NMicaOverridePublishContainer` | `true` | When set, `PublishContainer` produces a multi-layer image. Turn off to use the SDK's default single-layer behaviour. |
| `GenerateDockerfile` | `false` | Opt in to have a `Dockerfile` regenerated next to the `.csproj` on every build (for users who prefer the classic `docker build -f Dockerfile` flow; the `PublishLayer` target above is the cleaner alternative). |
| `DockerLayer` | _(all)_ | Passed to `PublishLayer` to pick specific layers. Values: `App`, `Package`, `EarlyPackage`, `Project`, `All` (combinable with commas). |

## Requirements

- **.NET 10 SDK or newer on the build host.** NMica's MSBuild task targets `net10.0`. Earlier LTS cycles are out of support or about to be; SDK 10 is the stated floor.
- Your application project's `TargetFramework` can still be anything with a published `mcr.microsoft.com/dotnet/runtime` image (net6+, practically).
- Built from a solution file (not a standalone csproj).
- **For `PublishContainer` with local-daemon output**: Docker or Podman available on the build host (the SDK pipes a tarball to `docker load` / `podman load`).
- **For `PublishContainer` with `ContainerArchiveOutputPath` or `ContainerRegistry`**: no daemon required — pure HTTPS pull of the base image + local tarball write or direct Distribution API push.

## Build (contributors)

NMica does not maintain its own copy of the OCI-image-writing machinery — the bulk of the container logic is **linked** from a sparse git submodule of [`dotnet/sdk`](https://github.com/dotnet/sdk) at `external/dotnet-sdk/`. The MSBuild task is the thin bit we actually own (~200 LoC of shims + the one-line "multi-layer" change); the ~5 KLoC that implements tar/gzip/registry/auth all sits in the SDK repo, pinned to a specific tag.

The `dotnet/sdk` repo is large (~1.5 GB with full history). The `.gitmodules` entry marks the submodule `shallow = true` so normal `submodule update` operations stay cheap.

### First clone

```sh
# Preferred: clone the main repo AND every submodule shallow in one pass
git clone --recurse-submodules --shallow-submodules <this-repo-url>

# Or, if you already cloned without --recurse-submodules:
git submodule update --init --depth 1
```

`--shallow-submodules` is the key flag — it passes `--depth 1` to each submodule's init. Without it, submodules default to a full clone and pull the SDK's entire history even though only `src/Containers/` is checked out (~3.7 MB) on disk.

### If you forgot the flag and already have a deep checkout

Strip the history retroactively:

```sh
git -C external/dotnet-sdk fetch --depth 1 origin $(git -C external/dotnet-sdk rev-parse HEAD)
git -C external/dotnet-sdk reflog expire --expire=now --all
git -C external/dotnet-sdk gc --prune=now
```

### When *not* to use shallow

Shallow submodules can't easily track a branch's HEAD (fetching newer commits fails if the shallow graft doesn't include the base). NMica pins to a specific tag rather than tracking `main`, so shallow is fine for us. If you want to dig through upstream SDK history for any reason, make the submodule deep on demand:

```sh
git -C external/dotnet-sdk fetch --unshallow
```

### Bumping the pinned SDK version

```sh
cd external/dotnet-sdk
git fetch --depth 1 origin tag v<new-tag>
git checkout v<new-tag>
cd ../..
git add external/dotnet-sdk
git commit -m "Bump dotnet/sdk submodule to v<new-tag>"
```

Reference on shallow submodule mechanics: <https://stackoverflow.com/questions/2144406/how-to-make-shallow-git-submodules>.

### Project layout

| Path | What it is |
|---|---|
| `external/dotnet-sdk/` | Sparse+shallow submodule of `dotnet/sdk`; only `src/Containers/Microsoft.NET.Build.Containers/` is checked out. |
| `src/NMica/NMica.csproj` | Glob-links every `.cs` under the SDK Containers dir (minus the Tasks layer and IVT attributes). All SDK deps (`Microsoft.Extensions.Logging.Abstractions`, `NuGet.Packaging`, `Valleysoft.DockerCredsProvider`) are `ExcludeAssets="runtime"` — they're ambient in the MSBuild host process by the time our task runs. |
| `src/NMica/Vendor/CliUtilsShim.cs` | ~70 LoC namespace shim for `Microsoft.DotNet.Cli.Utils` (an unpublished SDK-internal library). Lets the SDK's `DockerCli.cs` and `RegistrySettings.cs` compile verbatim. |
| `src/NMica/Vendor/MSBuildLogger.cs` | ~80 LoC adapter from `Microsoft.Extensions.Logging.ILogger` to `TaskLoggingHelper`. |
| `src/NMica/Tasks/CreateNewImageOverride.cs` | Near-verbatim copy of the SDK's `CreateNewImage` task. **The one interesting difference**: iterate `package/earlypackage/project/app` subdirectories and call `Layer.FromDirectory` + `imageBuilder.AddLayer` per bucket instead of once on `$(PublishDir)`. |
| `src/NMica/Tasks/{PublishLayer,GenerateDockerfile,CleanPublish,Layers}.cs` | Our own MSBuild tasks — ~350 LoC total. |
| `src/NMica/build/NMica.{props,targets}` | NuGet-consumer-side MSBuild integration. Registers the `UsingTask` override + the `BeforeTargets="_PublishSingleContainer"` layer-prep hook. |

The NuGet package itself ships one file: `tasks/net10.0/NMica.dll`. Every dependency is either in the BCL or pulled from MSBuild's ambient load context at runtime.

## Roadmap

NMica's `PublishContainer` override is structured to mirror `Microsoft.NET.Build.Containers` (the SDK's internal container machinery) as closely as possible — the Containers sources are linked directly from a pinned `dotnet/sdk` submodule rather than reimplemented. The intent is to upstream this as a feature PR to the SDK once it's stable, at which point NMica becomes unnecessary for this use case.

- **Phase 1** (superseded): hand-rolled OCI archive writer + docker-build fallback
- **Phase 2** (shipped): submodule-link the SDK's `Microsoft.NET.Build.Containers` sources directly. All three sinks (local daemon, OCI archive, remote registry push) go through the SDK's proven machinery with full credential-helper + auth support; our only contribution is the "iterate layer buckets" change in the `CreateNewImage` shadow task
- **Phase 3** (next): upstream PR against `dotnet/sdk` exposing the multi-layer capability as a first-class `PublishContainer` feature, making NMica unnecessary
