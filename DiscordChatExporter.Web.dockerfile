# -- Build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

ARG TARGETARCH
ARG VERSION=0.0.0

WORKDIR /tmp/app

COPY favicon.ico .
COPY favicon.png .
COPY NuGet.config .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY DiscordChatExporter.Core DiscordChatExporter.Core
COPY DiscordChatExporter.Web DiscordChatExporter.Web

RUN dotnet publish DiscordChatExporter.Web \
    -p:Version=$VERSION \
    -p:CSharpier_Bypass=true \
    --configuration Release \
    --self-contained \
    --use-current-runtime \
    --arch $TARGETARCH \
    --output DiscordChatExporter.Web/bin/publish/

# -- Run
FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS run

LABEL org.opencontainers.image.title="DiscordChatExporter.Web"
LABEL org.opencontainers.image.description="Browser UI for DiscordChatExporter: one-click server export and selective message deletion."
LABEL org.opencontainers.image.source="https://github.com/Tyrrrz/DiscordChatExporter"
LABEL org.opencontainers.image.licenses="MIT"

# ICU for locale-aware formatting, tzdata for timezones
RUN apk add --no-cache icu-libs icu-data-full tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV LC_ALL=en_US.UTF-8
ENV LANG=en_US.UTF-8

# Non-root user so files written into the mounted data volume are owned by the host user
RUN addgroup -S -g 1000 dce && adduser -S -H -G dce -u 1000 dce

ENV DCE_DATA_DIR=/data
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
# When exposing beyond loopback, set DCE_ALLOWED_HOSTS and DCE_ACCESS_KEY explicitly.
EXPOSE 5080
VOLUME /data

COPY --from=build /tmp/app/DiscordChatExporter.Web/bin/publish /opt/app
USER dce
WORKDIR /data
ENTRYPOINT ["/opt/app/DiscordChatExporter.Web"]
