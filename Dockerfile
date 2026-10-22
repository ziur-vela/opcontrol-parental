# Copyright (c) 2026 Ziur / OpControl IT Solutions
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG VERSION=0.0.0
ARG INFORMATIONAL=0.0.0-dev
ARG COMMIT=unknown
ARG BUILD_DATE=unknown
WORKDIR /src
COPY Directory.Build.props ./
COPY src/OpControl.Parental.Contracts/OpControl.Parental.Contracts.csproj src/OpControl.Parental.Contracts/
COPY src/OpControl.Parental.Server/OpControl.Parental.Server.csproj src/OpControl.Parental.Server/
RUN dotnet restore src/OpControl.Parental.Server/OpControl.Parental.Server.csproj
COPY LICENSE THIRD-PARTY-NOTICES.md ./
COPY src/ src/
COPY release-assets/ /app/release-assets/
RUN dotnet publish src/OpControl.Parental.Server/OpControl.Parental.Server.csproj -c Release -o /app/publish \
    /p:UseAppHost=false /p:Version=$VERSION /p:InformationalVersion=$INFORMATIONAL \
    /p:SourceRevisionId=$COMMIT /p:BuildDate=$BUILD_DATE

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 10001 appuser
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/release-assets /app/release-assets
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
