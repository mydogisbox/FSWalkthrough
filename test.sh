#!/bin/bash

set -e

API_URL="http://localhost:4200"
API_PROJECT="samples/FSWalkthrough.SampleApi"
PID_FILE="/tmp/fswalkthrough-api.pid"

# Check if something is already on port 4200
EXISTING_PID=$(lsof -ti tcp:4200 2>/dev/null || true)
if [ -n "$EXISTING_PID" ]; then
  EXISTING_CMD=$(ps -p "$EXISTING_PID" -o comm= 2>/dev/null)
  # Only kill if it looks like a previous instance of our API
  if [ -f "$PID_FILE" ] && [ "$(cat $PID_FILE)" = "$EXISTING_PID" ]; then
    echo "→ Stopping previous API instance (pid $EXISTING_PID)..."
    kill "$EXISTING_PID" 2>/dev/null
    sleep 1
  else
    echo "✗ Port 4200 is already in use by '$EXISTING_CMD' (pid $EXISTING_PID)."
    echo "  Please free the port and try again."
    exit 1
  fi
fi

# Start the API fresh
echo "→ Starting API (latest build)..."
dotnet run --project "$API_PROJECT" &
API_PID=$!
echo $API_PID > "$PID_FILE"

# Wait for the API to become ready
echo -n "  Waiting for API"
for i in {1..30}; do
  if curl -fs http://localhost:4200/health >/dev/null; then
    echo " ready"
    break
  fi
  echo -n "."
  sleep 1
  if [ $i -eq 30 ]; then
    echo " timed out"
    kill $API_PID 2>/dev/null
    rm "$PID_FILE"
    exit 1
  fi
done

# Run the tests
echo "→ Running tests..."
set +e
dotnet test
TEST_EXIT=$?
set -e

# Shut down the API
echo "→ Stopping API..."
kill $(cat "$PID_FILE") 2>/dev/null
rm "$PID_FILE"

exit $TEST_EXIT
