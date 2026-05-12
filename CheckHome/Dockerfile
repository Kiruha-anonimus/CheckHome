FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["CheckHome.csproj", "."]
RUN dotnet restore "CheckHome.csproj"
COPY . .
RUN dotnet build "CheckHome.csproj" -c Release -o /app/build
RUN dotnet publish "CheckHome.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CheckHome.dll"]