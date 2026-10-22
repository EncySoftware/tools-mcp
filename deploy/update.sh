#!/bin/sh
# On the tools server, as root: build the image for a released version and replace the tools-mcp container.
#   sh update.sh 0.2.0
# First time only (see README, "Hosted server"): /opt/tools-mcp/tools-mcp.env from tools-mcp.env.example, and the
# data folder owned by UID 1654, the image's app user.
set -eu
V="${1:?usage: update.sh <version>, e.g. 0.2.0}"
# --pull: the base images are fetched again, so every release gets the runtime's latest security patches instead of
# whatever was pulled the first time.
docker build --pull --build-arg VERSION="$V" -t "tools-mcp:$V" "https://github.com/EncySoftware/tools-mcp.git#v$V"
# The data folder holds the links' files for their hour: the container's user only.
chmod 700 /opt/tools-mcp/data
docker rm -f tools-mcp 2>/dev/null || true
# The server's memory is the backend's and Postgres's: if tools-mcp ever runs away, the kernel stops this container,
# not them. .NET sizes its heap to the limit. A line a call adds up, so the logs are capped too.
docker run -d --name tools-mcp --restart unless-stopped \
  --memory 512m --memory-swap 512m --pids-limit 128 \
  --log-driver json-file --log-opt max-size=10m --log-opt max-file=3 \
  -p 127.0.0.1:8097:8080 \
  --env-file /opt/tools-mcp/tools-mcp.env \
  -v /opt/tools-mcp/data:/data \
  "tools-mcp:$V"
sleep 5
docker logs --tail 20 tools-mcp
