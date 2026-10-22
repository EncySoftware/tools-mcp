# The hosted tools-mcp (`tools-mcp serve`): the dotnet tool exactly as published on nuget.org, on the ASP.NET Core
# runtime. Nothing is compiled on the server: its memory belongs to the backend.
#   docker build --pull --build-arg VERSION=0.2.0 -t tools-mcp:0.2.0 https://github.com/EncySoftware/tools-mcp.git#v0.2.0
FROM alpine:3.20 AS package
ARG VERSION
RUN test -n "$VERSION" \
 && wget -qO /p.nupkg "https://api.nuget.org/v3-flatcontainer/encysoftware.toolsmcp/${VERSION}/encysoftware.toolsmcp.${VERSION}.nupkg" \
 && mkdir /x && unzip -q /p.nupkg 'tools/net8.0/any/*' -d /x \
 && mv /x/tools/net8.0/any /app

# The tool is built for net8.0 and rolls forward (RollForward=Major): it runs on a runtime that is still supported.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=package /app /app
# The links' files live on the volume, so a link outlives an update within its hour.
ENV TOOLS_MCP_DATA=/data ASPNETCORE_HTTP_PORTS=8080 DOTNET_CLI_TELEMETRY_OPTOUT=1
RUN mkdir -p /data && chown app /data
VOLUME /data
USER app
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/tools-mcp.dll"]
CMD ["serve"]
