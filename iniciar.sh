#!/usr/bin/env sh
set -e
cd "$(dirname "$0")"

if ! command -v docker >/dev/null 2>&1; then
  echo "Instale o Docker (Docker Desktop ou Docker Engine com o plugin compose) e rode de novo."
  exit 1
fi

segredo() {
  LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c "${1:-32}"
}

definir() {
  chave="$1"
  valor="$2"
  if grep -q "^${chave}=$" .env; then
    sed "s|^${chave}=$|${chave}=${valor}|" .env > .env.tmp && mv .env.tmp .env
  fi
}

if [ ! -f .env ]; then
  cp .env.example .env
  echo "Arquivo .env criado a partir do .env.example."
fi

definir TURNO_POSTGRES_PASSWORD "$(segredo 32)"
definir TURNO_RABBITMQ_PASSWORD "$(segredo 32)"
definir TURNO_REDIS_PASSWORD "$(segredo 32)"
definir TURNO_API_KEY "$(segredo 40)"

porta=$(grep '^TURNO_PORT=' .env | cut -d= -f2)
porta=${porta:-8080}
chave=$(grep '^TURNO_API_KEY=' .env | cut -d= -f2)

docker compose up -d --build

printf "Aguardando a API ficar pronta"
tentativas=0
until curl -fsS "http://127.0.0.1:${porta}/health/ready" >/dev/null 2>&1; do
  tentativas=$((tentativas + 1))
  if [ "$tentativas" -ge 90 ]; then
    echo
    echo "A API não respondeu em 3 minutos. Veja os logs com: docker compose logs api"
    exit 1
  fi
  printf "."
  sleep 2
done
echo

echo "Painel:          http://localhost:${porta}"
echo "Documentação:    http://localhost:${porta}/docs"
echo "RabbitMQ:        http://localhost:$(grep '^TURNO_RABBITMQ_UI_PORT=' .env | cut -d= -f2)"
if [ -n "$chave" ]; then
  echo "Chave de acesso: ${chave}"
fi

if command -v xdg-open >/dev/null 2>&1; then
  xdg-open "http://localhost:${porta}" >/dev/null 2>&1 || true
elif command -v open >/dev/null 2>&1; then
  open "http://localhost:${porta}" || true
fi
