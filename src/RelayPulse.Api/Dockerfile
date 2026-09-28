# Build context is the repository root (see docker-compose.yml): the build needs
# Directory.Build.props / Directory.Packages.props, Core, and db/seed.sql.
# global.json is deliberately not copied: it pins a local SDK feature band the image may not carry.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/RelayPulse.Core/RelayPulse.Core.csproj src/RelayPulse.Core/
COPY src/RelayPulse.Api/RelayPulse.Api.csproj src/RelayPulse.Api/
RUN dotnet restore src/RelayPulse.Api/RelayPulse.Api.csproj
COPY src/ src/
RUN dotnet publish src/RelayPulse.Api/RelayPulse.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
# The fixture travels with the image so the one-shot `seed` service can load it.
COPY db/seed.sql /app/db/seed.sql
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RelayPulse.Api.dll"]
