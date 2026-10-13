#!/usr/bin/env bash
# Деплой без CI и без registry: образ собирается локально и уезжает на сервер по SSH.
# Запуск из корня репозитория: scripts/deploy.sh user@host [каталог на сервере]
# Файл .env на сервере создаётся один раз вручную — скрипт его не трогает.
set -euo pipefail

HOST=${1:?usage: scripts/deploy.sh user@host [remote dir]}
DIR=${2:-/opt/interview-bot}
COMPOSE="docker compose -f docker-compose.prod.yml"

docker build --platform linux/amd64 -t interview-bot:latest .
docker save interview-bot:latest | gzip | ssh "$HOST" "gunzip | docker load"

ssh "$HOST" "mkdir -p '$DIR'"
scp -r docker-compose.prod.yml seed "$HOST:$DIR/"

ssh "$HOST" "cd '$DIR' && test -f .env || { echo 'Нет $DIR/.env — создай его по .env.example'; exit 1; }
  $COMPOSE up -d && sleep 20 && $COMPOSE ps && $COMPOSE logs --tail 30 bot"
