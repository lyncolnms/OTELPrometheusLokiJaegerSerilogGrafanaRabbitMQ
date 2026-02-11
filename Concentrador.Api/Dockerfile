# =========================
# STAGE 1 – build
# =========================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# copie csproj e restaure
COPY ./Concentrador.Api/Concentrador.Api.csproj ./Concentrador.Api/
RUN dotnet restore ./Concentrador.Api/Concentrador.Api.csproj

# copie o restante e publique
COPY ./Concentrador.Api ./Concentrador.Api
WORKDIR /src/Concentrador.Api
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# =========================
# STAGE 2 – runtime
# =========================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# portas (ajuste se usar outra no Program.cs)
EXPOSE 8080

# variáveis padrão (podem ser sobrescritas no docker-compose)
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Concentrador.Api.dll"]
