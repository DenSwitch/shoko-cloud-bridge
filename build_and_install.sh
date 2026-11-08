#!/usr/bin/env bash
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Auto-detect container name (official doc uses shoko_server, fallback to shoko-server)
if [ -z "${SHOKO_CONTAINER:-}" ]; then
  if sudo docker ps --format '{{.Names}}' | grep -wq "shoko_server"; then
    CONTAINER="shoko_server"
  elif sudo docker ps --format '{{.Names}}' | grep -wq "shoko-server"; then
    CONTAINER="shoko-server"
  else
    CONTAINER="shoko_server"
  fi
else
  CONTAINER="$SHOKO_CONTAINER"
fi

PORT="${SHOKO_PORT:-8111}"

echo "[1/3] Compiling ShokoCloudBridge.dll inside container '$CONTAINER'..."
sudo docker exec "$CONTAINER" mkdir -p /tmp/cb_build /home/shoko/.shoko/Shoko.CLI/plugins
sudo docker cp "$DIR/compiler_bin/." "$CONTAINER:/tmp/cb_build/compiler_bin/"
sudo docker cp "$DIR/ShokoCloudBridge.cs" "$CONTAINER:/tmp/cb_build/ShokoCloudBridge.cs"

sudo docker exec "$CONTAINER" dotnet \
  /tmp/cb_build/compiler_bin/Compiler.dll \
  /tmp/cb_build/ShokoCloudBridge.cs \
  /home/shoko/.shoko/Shoko.CLI/plugins/ShokoCloudBridge.dll

sudo docker exec "$CONTAINER" rm -rf /tmp/cb_build

echo "[2/3] Restarting '$CONTAINER' to load plugin..."
sudo docker restart "$CONTAINER"

echo "[3/3] Waiting for Shoko Server & CloudBridge UI to come online..."
for i in {1..30}; do
  if curl -fsS "http://localhost:${PORT}/CloudBridge" >/dev/null 2>&1; then
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "http://localhost:${PORT}/api/CloudBridge/status")
    echo "ShokoCloudBridge is ONLINE! (UI: 200 OK, Unauthenticated API check: HTTP $HTTP_CODE)"
    exit 0
  fi
  sleep 2
done

echo "Warning: Timed out waiting for /CloudBridge on port ${PORT}. Check 'docker logs $CONTAINER'."
exit 1
