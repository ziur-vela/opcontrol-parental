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
COPY src/ src/
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
RUN mkdir -p /var/lib/opcontrol-parental/keys && chown -R appuser:appuser /var/lib/opcontrol-parental
USER appuser
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpControl.Parental.Server.dll"]
