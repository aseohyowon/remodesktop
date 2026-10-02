# 시그널링 서버 컨테이너 (저장소 최상위에서 빌드)
#   docker build -f signaling/Dockerfile -t remodesktop-signaling .
#   docker run -d -p 8080:8080 -v remodesktop-signal:/app/data remodesktop-signaling
# 운영 환경에서는 앞에 HTTPS 리버스 프록시(Caddy, nginx 등)를 두고 wss://로 접속하세요.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY windows/Protocol/RemoteDesktop.Protocol/ windows/Protocol/RemoteDesktop.Protocol/
COPY signaling/Signaling.Server/ signaling/Signaling.Server/
RUN dotnet publish signaling/Signaling.Server/Signaling.Server.csproj -c Release -o /out -p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Signaling__DataDirectory=/app/data
VOLUME /app/data
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Signaling.Server.dll"]
