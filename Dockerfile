# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Termin.Calendar/Termin.Calendar.csproj src/Termin.Calendar/
COPY src/Termin.CalDav/Termin.CalDav.csproj src/Termin.CalDav/
COPY src/Termin.Storage/Termin.Storage.csproj src/Termin.Storage/
COPY src/Termin.Server/Termin.Server.csproj src/Termin.Server/
RUN dotnet restore src/Termin.Server/Termin.Server.csproj
COPY src/ src/
RUN dotnet publish src/Termin.Server/Termin.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
LABEL org.opencontainers.image.title="termin" \
      org.opencontainers.image.description="Remote CalDAV MCP server for Claude" \
      org.opencontainers.image.source="https://github.com/LukasMerzOrg/termin-mcp" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown app:app /data
ENV TERMIN_DATA_DIR=/data \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
VOLUME /data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/termin.dll"]
