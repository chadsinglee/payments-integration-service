FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY global.json NuGet.Config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src ./src
COPY sql ./sql
RUN dotnet publish src/Acme.Stub/Acme.Stub.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=10 CMD wget -qO- http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Acme.Stub.dll"]
