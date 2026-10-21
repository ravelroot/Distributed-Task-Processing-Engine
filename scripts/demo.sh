#!/usr/bin/env sh
set -e
cd "$(dirname "$0")/.."

url="${TURNO_URL:-http://localhost:${TURNO_PORT:-8080}}"
quantidade="${1:-40}"
chave="${TURNO_API_KEY:-}"
if [ -z "$chave" ] && [ -f .env ]; then
  chave=$(grep '^TURNO_API_KEY=' .env | cut -d= -f2)
fi

corpo='{"jobs":['
i=1
while [ "$i" -le "$quantidade" ]; do
  case $((i % 5)) in
    0) item='{"type":"report.generate","queue":"reports","payload":{"rows":400000,"segments":12}}' ;;
    1) item='{"type":"file.process","payload":{"sizeMb":48,"chunkMb":4,"delayPerChunkMs":250}}' ;;
    2) item='{"type":"integration.sync","payload":{"records":80,"failureRate":0.35,"latencyMs":600}}' ;;
    3) item='{"type":"notification.send","queue":"critical","payload":{"to":"pessoa@example.com","channel":"email","subject":"Teste"}}' ;;
    *) item='{"type":"file.process","delaySeconds":20,"payload":{"sizeMb":24,"chunkMb":4,"delayPerChunkMs":200}}' ;;
  esac
  if [ "$i" -gt 1 ]; then
    corpo="${corpo},"
  fi
  corpo="${corpo}${item}"
  i=$((i + 1))
done
corpo="${corpo},{\"type\":\"integration.sync\",\"maxAttempts\":2,\"payload\":{\"records\":1,\"failureRate\":1,\"latencyMs\":100}}]}"

curl -fsS -X POST "${url}/api/jobs/batch" \
  -H "Content-Type: application/json" \
  -H "X-Turno-Key: ${chave}" \
  -d "$corpo" >/dev/null

echo "$((quantidade + 1)) tarefas enviadas para ${url}. Acompanhe no painel."
