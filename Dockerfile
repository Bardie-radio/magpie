# Build from the parent folder that contains both `magpie/` and `kithara/`
# (multi-root / Local Compose sibling layout → ProjectReference):
#
#   docker build -f magpie/Dockerfile -t magpie .
#
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY kithara/Directory.Build.props kithara/Directory.Packages.props kithara/
COPY kithara/libs/Bardie.Contracts kithara/libs/Bardie.Contracts/
COPY kithara/libs/Bardie.Module.Channel kithara/libs/Bardie.Module.Channel/
COPY kithara/libs/Bardie.Module.Hosting kithara/libs/Bardie.Module.Hosting/
COPY kithara/libs/Bardie.Module.Source kithara/libs/Bardie.Module.Source/

COPY magpie/Directory.Build.props magpie/Directory.Packages.props magpie/
COPY magpie/src/Magpie/Magpie.csproj magpie/src/Magpie/
RUN dotnet restore magpie/src/Magpie/Magpie.csproj

COPY magpie/src/Magpie/ magpie/src/Magpie/
RUN dotnet publish magpie/src/Magpie/Magpie.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl for healthcheck; ffmpeg shared libs for FFmpeg.AutoGen (in-process, not CLI).
# Match FFmpeg.AutoGen 6.1.x — aspnet:10.0 / Ubuntu 24.04 ships ffmpeg 6.1 (libavcodec.so.60).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/mtls /audio \
    && chown -R "$APP_UID":"$APP_UID" /data /app /audio

COPY --from=build /app/publish .
RUN chown -R "$APP_UID":"$APP_UID" /app

USER $APP_UID
ENV ASPNETCORE_URLS= \
    MODULE_TLS_DATA_PATH=/data/mtls \
    MODULE_WORK_GRPC_PORT=5001 \
    MAGPIE_FFMPEG_ROOT=/usr/lib/x86_64-linux-gnu

EXPOSE 8080 5001
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Magpie.dll"]
