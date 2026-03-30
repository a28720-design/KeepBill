FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY KeepBill.csproj ./
RUN dotnet restore KeepBill.csproj

COPY . ./
RUN dotnet publish KeepBill.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/out ./

# Render injects PORT at runtime; default to 10000 for local container runs.
ENV PORT=10000
ENV ASPNETCORE_URLS=http://+:${PORT}

ENTRYPOINT ["dotnet", "KeepBill.dll"]
