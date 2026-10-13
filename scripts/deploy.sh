#!/usr/bin/env bash
# Деплой без CI и без registry: на сервер уезжает архив исходников последнего коммита (~100 КБ),
# образ собирается там. Незакоммиченные правки не деплоятся.
# Запуск из корня репозитория: scripts/deploy.sh user@host [каталог на сервере]
# Файл .env на сервере создаётся один раз вручную — скрипт его не трогает.
set -euo pipefail

HOST=${1:?usage: scripts/deploy.sh user@host [remote dir]}
DIR=${2:-/opt/interview-bot}
COMPOSE="docker compose -f docker-compose.prod.yml"
# ключ и прочие параметры SSH: SSH_OPTS="-i ~/.ssh/key" scripts/deploy.sh user@host
SSH_OPTS=${SSH_OPTS:-}

echo "Deploying $(git log -1 --format='%h %s')"
# src удаляем перед распаковкой: удалённые в репозитории файлы не должны попадать в сборку
git archive --format=tar.gz HEAD | ssh $SSH_OPTS "$HOST" "mkdir -p '$DIR' && cd '$DIR' && rm -rf src tests && tar -xzf -"

ssh $SSH_OPTS "$HOST" "cd '$DIR' && test -f .env || { echo 'Нет $DIR/.env — создай его по .env.example'; exit 1; }
  $COMPOSE up -d --build && sleep 20 && $COMPOSE ps && $COMPOSE logs --tail 30 bot"
