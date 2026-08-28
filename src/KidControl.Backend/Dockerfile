# Build context must be the REPO ROOT (needs Directory.*.props + referenced projects).
#   docker build -f src/KidControl.Backend/Dockerfile -t kidcontrol-backend .
# The build has no .git (see .dockerignore), so MinVer can't derive a version — pass it in.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BACKEND_VERSION=2.0.0
WORKDIR /src
COPY . .
RUN dotnet restore src/KidControl.Backend/KidControl.Backend.csproj \
 && dotnet publish src/KidControl.Backend/KidControl.Backend.csproj -c Release -o /app --no-restore \
      -p:MinVerVersionOverride=${BACKEND_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "KidControl.Backend.dll"]
