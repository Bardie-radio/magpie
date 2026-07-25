# Build from the parent folder that contains both `magpie/` and `kithara/`
# (multi-root / Local Compose sibling layout → ProjectReference):
#
#   docker build -f magpie/Dockerfile -t magpie .
#
# META-OPS-002: Alpine final + bare libav packages (no ffmpeg CLI metapackage).
# Build on Debian SDK so Grpc.Tools protoc (glibc) runs; publish for linux-musl-x64.
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
RUN dotnet restore magpie/src/Magpie/Magpie.csproj -r linux-musl-x64

COPY magpie/src/Magpie/ magpie/src/Magpie/
# Re-restore after source COPY (obj/assets from the prior restore were overwritten).
RUN dotnet publish magpie/src/Magpie/Magpie.csproj \
      -c Release -r linux-musl-x64 --self-contained false \
      -o /app/publish

# Pin alpine3.22: floating `10.0-alpine` is 3.23+ (ffmpeg 8) — AutoGen 6.1 needs .so.58/.60.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.22 AS final
WORKDIR /app

# Demux/decode/resample via FFmpeg.AutoGen — shared libs only (Alpine 3.22 ffmpeg 6.1.x).
# Do not install the `ffmpeg` CLI metapackage.
RUN apk add --no-cache \
      ffmpeg-libavcodec \
      ffmpeg-libavformat \
      ffmpeg-libavutil \
      ffmpeg-libswresample \
    && mkdir -p /data/mtls /audio \
    && chown -R "$APP_UID":"$APP_UID" /data /app /audio

COPY --from=build /app/publish .
RUN chown -R "$APP_UID":"$APP_UID" /app

USER $APP_UID
ENV ASPNETCORE_URLS= \
    MODULE_TLS_DATA_PATH=/data/mtls \
    MODULE_WORK_GRPC_PORT=5001 \
    MAGPIE_FFMPEG_ROOT=/usr/lib

EXPOSE 8080 5001
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Magpie.dll"]
