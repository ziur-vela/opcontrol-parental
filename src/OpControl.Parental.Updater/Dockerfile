# Copyright (c) 2026 Ziur / OpControl IT Solutions
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG VERSION=0.0.0
WORKDIR /src
COPY Directory.Build.props ./
COPY src/OpControl.Parental.Updater/OpControl.Parental.Updater.csproj src/OpControl.Parental.Updater/
RUN dotnet restore src/OpControl.Parental.Updater/OpControl.Parental.Updater.csproj
COPY src/OpControl.Parental.Updater/ src/OpControl.Parental.Updater/
RUN dotnet publish src/OpControl.Parental.Updater/OpControl.Parental.Updater.csproj -c Release -o /app/publish \
    /p:UseAppHost=false /p:Version=${VERSION:-0.0.0}

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS runtime
ARG COMPOSE_VERSION=v2.29.7
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && mkdir -p /usr/local/lib/docker/cli-plugins \
    && curl -fsSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-x86_64" \
       -o /usr/local/lib/docker/cli-plugins/docker-compose \
    && chmod +x /usr/local/lib/docker/cli-plugins/docker-compose \
    && rm -rf /var/lib/apt/lists/*
COPY --from=docker:27-cli /usr/local/bin/docker /usr/local/bin/docker
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data/backups
EXPOSE 8098
ENV ASPNETCORE_URLS=http://+:8098
ENTRYPOINT ["dotnet", "OpControl.Parental.Updater.dll"]
