NMica simplifies building and operating .NET applications in container environments (Docker, Kubernetes).

## Intelligent Dockerfile generation

Adding NMica to projects in your solution will automatically generate Dockerfile per executable project on build (at solution level). The generated docker image uses the official Microsoft container SDK to compile code, and multistage docker image to create a final run image based either on core or aspnet profile (depending on project type). 

NMica significantly improves on the default recommended Dockerfile template used for .NET applications by creating final run image in layers that significantly improves caching and minimizes image size. The default Dockerfile copies the entire output of `dotnet publish` as a single layer into the final run image. Many items in that output folder will stay static between builds, such as nuget dependency DLLs, but since they are copied as a single operation this creates a single distinct layer in that image. Usually your application code will change much more frequently then the dependencies, so building up your app in layers allow much higher reuse of docker layer caching. NMica instead creates the following layers ordered based on likelihood of change between builds, from least common to most common:

- package - stable nuget dependencies 
- earlypackage - pre-release nuget dependencies
- project - any project references that the app depends on
- app - the target project itself (and any files that don't fall in the above categories)

**On a project with many NuGet dependencies, this can reduce the size of the new layers that are generated for new image associated with image from double digit megabytes to a few KB due to cache hits**

