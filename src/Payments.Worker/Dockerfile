FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY global.json NuGet.Config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src ./src
COPY sql ./sql
RUN dotnet publish src/Payments.Worker/Payments.Worker.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "Payments.Worker.dll"]
