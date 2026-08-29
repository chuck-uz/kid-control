# Build context must be the REPO ROOT (needs Directory.*.props + referenced projects).
#   docker build -f src/KidControl.Backend/Dockerfile -t kidcontrol-backend .
# The build has no .git (see .dockerignore), so MinVer can't derive a version — pass it in.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BACKEND_VERSION=2.0.0
WORKDIR /src

# Restore is a separate, cached layer: it only re-runs when a csproj/props changes, not on
# every code edit. Copy just the project files the backend needs, restore, THEN copy source.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/KidControl.Backend/KidControl.Backend.csproj       src/KidControl.Backend/
COPY src/KidControl.Fleet.Contracts/KidControl.Fleet.Contracts.csproj src/KidControl.Fleet.Contracts/
COPY src/KidControl.Domain/KidControl.Domain.csproj         src/KidControl.Domain/
RUN dotnet restore src/KidControl.Backend/KidControl.Backend.csproj

COPY . .
RUN dotnet publish src/KidControl.Backend/KidControl.Backend.csproj -c Release -o /app \
      --no-restore -p:MinVerVersionOverride=${BACKEND_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "KidControl.Backend.dll"]
