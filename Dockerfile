FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# сначала только проектные файлы: слой restore кэшируется, пока не меняются зависимости
COPY global.json Directory.Build.props ./
COPY src/InterviewBot.Core/InterviewBot.Core.csproj src/InterviewBot.Core/
COPY src/InterviewBot.Infrastructure/InterviewBot.Infrastructure.csproj src/InterviewBot.Infrastructure/
COPY src/InterviewBot.Host/InterviewBot.Host.csproj src/InterviewBot.Host/
RUN dotnet restore src/InterviewBot.Host/InterviewBot.Host.csproj

COPY src/ src/
RUN dotnet publish src/InterviewBot.Host/InterviewBot.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# программа по умолчанию; docker-compose монтирует ./seed поверх, чтобы правки не требовали пересборки
COPY seed/ seed/
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "InterviewBot.Host.dll"]
