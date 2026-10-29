FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY LICENSE NOTICE THIRD-PARTY-NOTICES.md ./
COPY licenses/ ./licenses/
COPY src/ ./src/
RUN dotnet publish src/Vndb.Web/Vndb.Web.csproj -c Release -a $TARGETARCH --self-contained false \
    -p:UseAppHost=false -o /out/web '-bl:/tmp/web-publish-{}.binlog'
RUN dotnet publish src/Vndb.Cli/Vndb.Cli.csproj -c Release -a $TARGETARCH --self-contained false \
    -p:UseAppHost=false -o /out/cli '-bl:/tmp/cli-publish-{}.binlog'
RUN dotnet publish src/Vndb.Admin/Vndb.Admin.csproj -c Release -a $TARGETARCH --self-contained false \
    -p:UseAppHost=false -o /out/admin '-bl:/tmp/admin-publish-{}.binlog'
RUN chmod -R a+rX /out/web /out/cli /out/admin

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS cli
WORKDIR /data
COPY --from=build /out/cli/ /app/
COPY --from=build /src/LICENSE /src/NOTICE /src/THIRD-PARTY-NOTICES.md /app/
COPY --from=build /src/licenses/ /app/licenses/
LABEL org.opencontainers.image.version="0.5.0" org.opencontainers.image.licenses="Apache-2.0"
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/vndb.dll"]
CMD ["--help"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS web
WORKDIR /app
COPY --from=build /out/web/ ./
COPY --from=build /out/admin/ ./admin/
COPY --from=build /out/cli/ ./cli/
COPY --from=build /src/LICENSE /src/NOTICE /src/THIRD-PARTY-NOTICES.md ./
COPY --from=build /src/licenses/ ./licenses/
LABEL org.opencontainers.image.version="0.5.0" org.opencontainers.image.licenses="Apache-2.0"
ENV ASPNETCORE_HTTP_PORTS=8080 Library__DataDirectory=/data DOTNET_NOLOGO=1
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/Vndb.Web.dll"]

FROM web AS final
