FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /source
COPY global.json Notes.slnx ./
COPY src/Notes.Api/Notes.Api.csproj src/Notes.Api/
RUN dotnet restore src/Notes.Api/Notes.Api.csproj --source "$NUGET_SOURCE" --nologo
COPY src/ src/
RUN dotnet publish src/Notes.Api/Notes.Api.csproj -c Release --no-restore \
    -o /app/publish /p:UseAppHost=false --nologo

FROM build AS tests
COPY tests/ tests/
RUN dotnet restore tests/Notes.Api.Tests/Notes.Api.Tests.csproj --source "$NUGET_SOURCE" --nologo \
    && dotnet build tests/Notes.Api.Tests/Notes.Api.Tests.csproj -c Release --no-restore --nologo
ENTRYPOINT ["dotnet", "test", "tests/Notes.Api.Tests/Notes.Api.Tests.csproj", "-c", "Release", "--no-build", "--logger", "console;verbosity=normal"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Notes.Api.dll"]
