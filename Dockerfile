FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Путь к csproj теперь внутри папки CheckHome/
COPY ["CheckHome/CheckHome.csproj", "CheckHome/"]
RUN dotnet restore "CheckHome/CheckHome.csproj"

# Копируем всё из папки CheckHome
COPY ["CheckHome/.", "CheckHome/"]

RUN dotnet publish "CheckHome/CheckHome.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CheckHome.dll"]