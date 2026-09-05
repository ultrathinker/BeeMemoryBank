#!/usr/bin/env bash
# Seeds test1 with a deterministic corpus. test2 gets everything by sync — never seed it directly,
# or the two nodes diverge for reasons that have nothing to do with the code under test.
#
# Safe to re-run: it refuses to touch a node that already has articles unless RESEED=1 is set.
set -euo pipefail

PASSWORD="${BMB_TEST_PASSWORD:-TestNode-1-Pass!2026}"
API="${BMB_TEST_API:-http://127.0.0.1:5013}"
NODE="${BMB_TEST_NODE:-test1}"

KEY=$(docker exec "$NODE" cat /app/data/.internal-key)
hdr=(-H "Content-Type: application/json" -H "X-Internal-Key: $KEY"
     -H "X-User-Role: superadmin" -H "X-User-Id: 1")

api() { curl -sS -m 120 "${hdr[@]}" "$@"; }

# The vault must be open before anything below: creating an article encrypts its body, which needs
# the master DEK. A locked node answers with a 403 that says nothing about the real cause.
api -X POST "$API/api/session/unlock" -d "{\"password\":\"$PASSWORD\"}" >/dev/null

existing=$(api "$API/api/articles" | grep -o '"id"' | wc -l)
if [ "$existing" -gt 1 ] && [ "${RESEED:-0}" != "1" ]; then
  echo "$NODE already has $existing articles; refusing to seed on top of them. Set RESEED=1 to override." >&2
  exit 1
fi

mk() { # mk <path> <title> <tags-csv> <body>
  local tags="" IFS=,
  for t in $3; do tags="$tags,\"$t\""; done
  tags="[${tags#,}]"
  unset IFS
  api -X POST "$API/api/articles" -d "$(cat <<JSON
{"title":"$2","treePath":"$1","conceptTags":$tags,"content":"$4"}
JSON
)" >/dev/null
}

echo "seeding $NODE ..."
n=0

# A tree with the shapes that actually get exercised: nested folders, a deny/allow boundary
# (/Private), an archive that only matters to search, and mixed-language bodies because the
# embedding model is multilingual and an all-English corpus hides tokenizer problems.
seed_folder() { # seed_folder <path> <count> <tag> <lang>
  local path=$1 count=$2 tag=$3 lang=$4 i title body
  for i in $(seq 1 "$count"); do
    if [ "$lang" = "ru" ]; then
      title="Заметка $i — $tag"
      body="Это тестовая статья номер $i в разделе $path. Она существует только для проверки поиска, синхронизации и прав доступа. Ключевое слово: $tag."
    else
      title="Note $i — $tag"
      body="Test article number $i under $path. It exists only to exercise search, sync and access control. Keyword: $tag."
    fi
    mk "$path" "$title" "$tag,seed" "$body"
    n=$((n+1))
  done
}

seed_folder "/Public/Docs"          25 "docs"      en
seed_folder "/Public/Docs/API"      20 "api"       en
seed_folder "/Public/Notes"         25 "notes"     ru
seed_folder "/Private/Personal"     20 "personal"  ru
seed_folder "/Private/Finance"      15 "finance"   en
seed_folder "/Projects/Alpha"       25 "alpha"     en
seed_folder "/Projects/Alpha/Specs" 15 "specs"     en
seed_folder "/Projects/Beta"        20 "beta"      ru
seed_folder "/Archive/2025"         25 "archive"   en
seed_folder "/Archive/2024"         10 "archive"   ru

echo "seeded $n articles on $NODE"
echo "waiting for sync to test2 ..."
K2=$(docker exec test2 cat /app/data/.internal-key)
for i in $(seq 1 60); do
  c=$(curl -sS -m 30 -H "X-Internal-Key: $K2" -H "X-User-Role: superadmin" -H "X-User-Id: 1" \
        "http://127.0.0.1:5014/api/articles" | grep -o '"id"' | wc -l)
  echo "  test2 has $c / $((n+1))"
  [ "$c" -ge "$((n+1))" ] && { echo "sync complete after ~$((i*10))s"; exit 0; }
  sleep 10
done
echo "sync did not complete within 600s — check 'docker logs test2'" >&2
exit 1
