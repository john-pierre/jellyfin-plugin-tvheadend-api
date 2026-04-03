# ── Stage 1: Build the plugin ───────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

# Copy solution + project file first for layer-cached restore
COPY Jellyfin.Plugin.TvHeadendApi.sln ./
COPY Jellyfin.Plugin.TvHeadendApi/Jellyfin.Plugin.TvHeadendApi.csproj Jellyfin.Plugin.TvHeadendApi/

RUN dotnet restore Jellyfin.Plugin.TvHeadendApi.sln

# Copy the rest of the source code
COPY Jellyfin.Plugin.TvHeadendApi/ Jellyfin.Plugin.TvHeadendApi/
COPY manifest.json ./

# Build version – overridable via --build-arg
ARG VERSION=1.0.0.0

RUN dotnet build Jellyfin.Plugin.TvHeadendApi.sln \
      --configuration Release \
      --no-restore \
      -p:AssemblyVersion=${VERSION} \
      -p:FileVersion=${VERSION} \
      -p:InformationalVersion=${VERSION}

# Create the plugin directory with DLL + meta.json
RUN apt-get update && apt-get install -y --no-install-recommends jq && rm -rf /var/lib/apt/lists/* \
    && TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ") \
    && mkdir -p /plugin \
    && cp Jellyfin.Plugin.TvHeadendApi/bin/Release/net8.0/Jellyfin.Plugin.TvHeadendApi.dll /plugin/ \
    && jq --arg ts "$TIMESTAMP" --arg v "$VERSION" \
       '.[0] | del(.versions) | . + {timestamp: $ts, version: $v}' \
       manifest.json > /plugin/meta.json

# ── Stage 2: Jellyfin runtime with plugin installed ─────────────────
FROM jellyfin/jellyfin:10.10.7

ENV TZ=Europe/Berlin

# Keep a copy of the built plugin in the immutable image layer.
# At container start, we sync it into /config/plugins so bind-mounted /config works.
ARG VERSION=1.0.0.0
COPY --from=build /plugin/ /opt/tvheadend-plugin/tvheadend_api_${VERSION}/

# Create persistent directories
RUN mkdir -p /config /cache /media

# Startup script copies plugin into /config/plugins when needed, then starts Jellyfin.
COPY docker/start-jellyfin.sh /usr/local/bin/start-jellyfin.sh
RUN chmod +x /usr/local/bin/start-jellyfin.sh

EXPOSE 8096 8920

# FFmpeg tuning for live TV – reduce probing to speed up channel switches
ENV JELLYFIN_FFmpeg__probesize=1M
ENV JELLYFIN_FFmpeg__analyzeduration=1M

# Override the base image entrypoint so our bootstrap script runs first.
ENTRYPOINT ["/usr/local/bin/start-jellyfin.sh"]
