[![Build Status](https://dev.azure.com/nmica/NMica/_apis/build/status/NMica.NMica?branchName=master)](https://dev.azure.com/nmica/NMica/_build/latest?definitionId=1&branchName=master)

nMica auto-generates your Dockerfiles for your .NET Core projects with intelligent layering that maximizes Docker layer caching

## Quick start
1. Add `NMica` [NuGet package](https://www.nuget.org/packages/NMica) to your executable projects
2. Build image at solution level `dotnet build mysolution.sln`

Dockerfiles will be generated inside solution directory for each executable project

### Supported project types:

- Executable .NET Core projects targeting v5.0 or v3.1 (*these are the only target frameworks that have officially supported images that are not end of life*)

## Features
- Autogenerate Dockerfile for every compatible project in solution
- Select appropriate build & run docker image based on `TargetFramework` version and SDK
- Support for nuget.config with local source packages 
- Multistage container build
- Intelligent layering for .NET projects (see below)

## Intelligent Dockerfile generation

NMica significantly improves on the default recommended Dockerfile template used for .NET applications by creating final run image in layers that significantly improves caching and minimizes image size. The default Dockerfile copies the entire output of `dotnet publish` as a single layer into the final run image. Many items in that output folder will stay static between builds, such as nuget dependency DLLs, but since they are copied as a single operation this creates a single distinct layer in that image. Usually your application code will change much more frequently then the dependencies, so building up your app in layers allow much higher reuse of docker layer caching. NMica instead creates the following layers ordered based on likelihood of change between builds, from least common to most common:

- `package` - stable nuget dependencies 
- `earlypackage` - pre-release nuget dependencies
- `project` - any project references that the app depends on
- `app` - the target project itself (and any files that don't fall in the above categories)

**On a project with many NuGet dependencies, this can reduce the size of the new layers that are generated for new image associated with image from double digit megabytes to a few KB due to cache hits**

See this [blog post](https://stakhov.pro/building-efficient-net-docker-images/) for more details
