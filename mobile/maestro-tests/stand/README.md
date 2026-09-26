# Running the Maestro flows against a test stand

The flows in `mobile/maestro-tests` drive the Android app on a real phone (or emulator) through
[Maestro](https://maestro.mobile.dev). They need a Bee node for the phone to sync with and a fixed
set of fixture data on it.

## 1. A node the phone can reach

Any node works. The local test stand is a Docker container on the developer machine with its API
published on `127.0.0.1:5400`; the phone reaches it over USB:

```sh
adb reverse tcp:5400 tcp:5400
```

Set the phone up once (Setup page: server `localhost:5400`, the node's password) and unlock it.

## 2. Fixture data

The flows expect folders `Movies`, `Books`, `Travel`, `Music`, `Health`, `Technology`, `Finance`,
articles such as "The Matrix" (tags `sci-fi` and `fiction`, text containing "red pill") and a few
more. Create them on the node (idempotent, safe to repeat); the phone receives them through sync:

```sh
docker exec -i <container> sh < mobile/maestro-tests/stand/seed-fixtures.sh
```

The script runs inside the container and talks to the node's own API with its internal key, so
the node must be unlocked.

## 3. Run

```sh
BMB_TEST_PASSWORD='<node password>' mobile/maestro-tests/run_tests.sh            # every flow
BMB_TEST_PASSWORD='<node password>' mobile/maestro-tests/run_tests.sh create_article.yaml
```

Flows that change data (rename a tag, edit an article) put it back at the end, so the suite can
be run again without reseeding.
