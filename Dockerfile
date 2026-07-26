# Build from this repo (or compose context ../magpie):
#
#   docker build -t magpie .
#
# Restores Bardie.Logos.* / Bardie.Module.Source from nuget.org.
#
# META-OPS-002: Alpine final + bare libav packages (no ffmpeg CLI metapackage).
# Build on Debian SDK so Grpc.Tools protoc (glibc) runs; publish for linux-musl-x64.
#
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Magpie/Magpie.csproj src/Magpie/
RUN dotnet restore src/Magpie/Magpie.csproj -r linux-musl-x64

COPY src/Magpie/ src/Magpie/
# Re-restore after source COPY (obj/assets from the prior restore were overwritten).
RUN dotnet publish src/Magpie/Magpie.csproj \
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
