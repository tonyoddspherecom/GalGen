# The GalGen server, for Linux. Build from the repository root:
#   docker build -f UniverseGen.Server/Dockerfile -t galgen-server .
# and run it with a folder holding galgen.json mounted at /config:
#   docker run -p 24700:24700 -p 24701:24701 -v /srv/galgen:/config galgen-server

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY UniverseGen/ UniverseGen/
COPY UniverseGen.Service/ UniverseGen.Service/
COPY UniverseGen.Service.Http/ UniverseGen.Service.Http/
COPY UniverseGen.Server/ UniverseGen.Server/
RUN dotnet publish UniverseGen.Server/UniverseGen.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 24700 24701
ENTRYPOINT ["./galgen-server", "serve", "--config", "/config/galgen.json"]
