#!/bin/sh
# Seeds the fixture data the Maestro flows expect (folders Movies, Books, Travel, Music, Health,
# Technology, Finance; "The Matrix" with tags sci-fi + fiction and the phrase "red pill", ...).
# Runs INSIDE the test-stand container, against its own API, with the node's internal key:
#
#   docker exec -i bee-teststand-bmb-1 sh < mobile/maestro-tests/stand/seed-fixtures.sh
#
# Idempotent: an article whose title already exists is skipped. The node must be unlocked.
# The phone picks the data up through normal sync.

K=$(cat /app/data/.internal-key)
API=http://localhost:5300

call() { # method path [json]
  if [ -n "$3" ]; then
    curl -s -X "$1" -H "X-Internal-Key: $K" -H "X-User-Role: superadmin" -H "Content-Type: application/json" "$API$2" -d "$3"
  else
    curl -s -X "$1" -H "X-Internal-Key: $K" -H "X-User-Role: superadmin" "$API$2"
  fi
}

EXISTING=$(call GET "/api/articles?limit=1000")

folder() { # path
  call POST /api/folders "{\"path\":\"$1\"}" > /dev/null
}

article() { # title treePath content tagsJson
  case "$EXISTING" in
    *"\"title\":\"$1\""*) echo "skip   $1"; return ;;
  esac
  out=$(call POST /api/articles "{\"title\":\"$1\",\"treePath\":\"$2\",\"content\":\"$3\",\"conceptTags\":$4}")
  case "$out" in
    *"\"id\""*) echo "create $1" ;;
    *) echo "FAILED $1: $out" ;;
  esac
}

for f in /Movies/ /Books/ /Travel/ /Music/ /Health/ /Technology/ /Finance/; do folder "$f"; done

article "The Matrix" "/Movies/" "A hacker learns that reality is a simulation. He takes the red pill and wakes up." '["sci-fi","fiction"]'
article "Interstellar" "/Movies/" "Explorers travel through a wormhole to find a new home for humanity." '["sci-fi"]'
article "The Shawshank Redemption" "/Movies/" "A banker spends decades in prison and never loses hope." '["drama","classic"]'
article "The Master and Margarita" "/Books/" "The devil visits Moscow in the 1930s." '["fiction","classic"]'
article "Japan in Spring" "/Travel/" "Cherry blossoms, trains on time, and quiet temples in Kyoto." '["travel"]'
article "Music Theory" "/Music/" "Scales, intervals and chords: the grammar of music." '["music"]'
article "Healthy Eating" "/Health/" "Vegetables first, less sugar, enough water." '["health"]'
article "Home Server Notes" "/Technology/" "A small box in the closet that runs everything." '["tech"]'
article "Monthly Budget" "/Finance/" "Rent, food, savings: the plan for the month." '["money"]'
exit 0
