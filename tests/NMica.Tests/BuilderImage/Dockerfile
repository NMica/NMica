# Utility image that allows cross-compilation on supported combinations. 
# Test apps are compiled and run in docker to ensure no caches are hit such as build binaries or nuget global cache
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS net60
FROM mcr.microsoft.com/dotnet/aspnet:5.0 AS net50
FROM mcr.microsoft.com/dotnet/aspnet:3.1 AS net31
FROM mcr.microsoft.com/dotnet/aspnet:2.1 AS net21
FROM mcr.microsoft.com/dotnet/framework/sdk:4.8 AS msbuild
SHELL ["cmd", "/S", "/C"]
ADD https://aka.ms/vs/17/release/vs_BuildTools.exe C:/TEMP/vs_buildtools.exe
RUN C:\TEMP\vs_buildtools.exe --quiet --wait --norestart --nocache \
                    --installPath C:\BuildTools \
                    --add Microsoft.VisualStudio.Workload.AzureBuildTools \
                    --remove Microsoft.VisualStudio.Component.Windows10SDK.10240 \
                    --remove Microsoft.VisualStudio.Component.Windows10SDK.10586 \
                    --remove Microsoft.VisualStudio.Component.Windows10SDK.14393 \
                    --remove Microsoft.VisualStudio.Component.Windows81SDK \
                        || IF "%ERRORLEVEL%"=="3010" EXIT 0
FROM mcr.microsoft.com/dotnet/framework/sdk:4.8
COPY --from=msbuild ["/BuildTools","/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/"]
COPY --from=net21 ["/Program Files/dotnet/shared","/Program Files/dotnet/shared"]
COPY --from=net31 ["/Program Files/dotnet/shared","/Program Files/dotnet/shared"]
COPY --from=net50 ["/Program Files/dotnet/shared","/Program Files/dotnet/shared"]
COPY --from=net60 ["/Program Files/dotnet","/Program Files/dotnet"]
ENV DOTNET_USE_POLLING_FILE_WATCHER=true \
    NUGET_XMLDOC_MODE=skip
#USER ContainerAdministrator
#RUN setx /M PATH "%PATH%;C:\Program Files\powershell"
#USER ContainerUser
RUN dotnet help
ENTRYPOINT ["powershell", "-NoLogo","-ExecutionPolicy","Bypass"]
 