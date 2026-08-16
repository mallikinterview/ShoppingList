# Shopping List API

**A .NET 10 WebAPI for a shopping list with hybrid vector + full-text search, running as a complete containerised stack.**

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-17%20%2B%20pgvector-336791)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED)
![Keycloak](https://img.shields.io/badge/Auth-Keycloak%2026-4D4D4D)
![Tests](https://img.shields.io/badge/tests-130%20passing-brightgreen)
[![CI/CD](https://github.com/mallikinterview/Shopping-List/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/mallikinterview/Shopping-List/actions/workflows/ci-cd.yml)

---

## What This Is

- A **REST API** where a user signs up, signs in, and maintains a private shopping list.
- Items can carry **attached images**, stored in Minio and served through short-lived presigned URLs.
- Search is **hybrid**: pgvector cosine similarity, PostgreSQL full-text search, and metadata filters — fused in a **single SQL statement**.
- The whole stack runs from **one `docker compose` command**: API, PostgreSQL + pgvector, Minio, Redis, Ollama, Keycloak, Prometheus, Loki, Grafana.
- Every design decision that a reader might question is **explained in a comment next to the code**, not left to be guessed at.

---

## Table of Contents

- [Prerequisites](#prerequisites)
- [Docker Resources](#docker-resources)
- [Only Docker](#only-docker)
- [Setup — Run This First](#setup--run-this-first)
- [Using Visual Studio](#using-visual-studio)
- [Using Command Line](#using-command-line)
- [Check The Processes](#check-the-processes)
- [Using Each Endpoint — Scalar UI](#using-each-endpoint--scalar-ui)
- [Using Each Endpoint — Command Line](#using-each-endpoint--command-line)
- [Embed Search](#embed-search)
- [A/B Testing](#ab-testing)
- [Architecture Decision](#architecture-decision)
- [System Production Ready](#system-production-ready)
- [CI/CD](#cicd)
- [Viewing Endpoint Documentation in Scalar](#viewing-endpoint-documentation-in-scalar)
- [Known Limitations](#known-limitations)
- [If I Had More Time](#if-i-had-more-time)

---

## Prerequisites

### **.NET 10 — read this first**

- **This project targets .NET 10 and will not build on anything earlier.**

  ```xml
  <TargetFramework>net10.0</TargetFramework>        <!-- Directory.Build.props -->
  ```

  ```json
  { "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }    // global.json
  ```

- `global.json` pins the SDK **feature band**, so any `10.0.x` at or above `10.0.100` works, and anything below fails immediately with a clear message rather than a confusing one later.
- **Visual Studio 2022 cannot open this solution.** It has no .NET 10 support. **Visual Studio 2026 is required.**
- **.NET 8 or 9 will not do.** `dotnet build` and `dotnet test` fail on any earlier SDK.
- Check what you have:

  ```bash
  dotnet --list-sdks
  ```

  At least one `10.0.x` entry must appear.
- **If you only intend to run the stack in Docker and never build locally, you do not need the SDK at all** — the API is compiled inside its container image.

### **The machine itself**

- The stack is **eleven containers**, one of which runs a local embedding model.

  | | Minimum | Comfortable |
  |---|---|---|
  | RAM available to Docker | 8 GB | 12 GB |
  | Free disk | 12 GB | 20 GB |
  | CPU | 4 cores | 8 cores |

- The **first** `docker compose up` pulls every image and downloads the `nomic-embed-text` model — allow **10–15 minutes**. Subsequent starts take under a minute.

### **These host ports must be free**

A port conflict is the most common first-run failure.

| Port | Service | If it clashes, change |
|---|---|---|
| 5080 | API | `API_PORT` |
| 8080 | Keycloak | `KEYCLOAK_PORT` |
| 5432 | PostgreSQL | `POSTGRES_PORT` — set `5433` if you already run Postgres locally |
| 6379 | Redis | `REDIS_PORT` |
| 9000 | Minio API | `MINIO_API_PORT` |
| 9001 | Minio console | `MINIO_CONSOLE_PORT` |
| 9090 | Prometheus | `PROMETHEUS_PORT` |
| 3100 | Loki | `LOKI_PORT` |
| 3000 | Grafana | `GRAFANA_PORT` |

- Every one is remappable in `.env`. **Nothing inside the stack depends on the host port** — services reach each other over the Docker network by service name.
- Check what is already listening:

  ```powershell
  netstat -ano | findstr "5080 8080 5432 6379 9000 9001 9090 3100 3000"      # Windows
  ```

  ```bash
  lsof -nP -iTCP -sTCP:LISTEN | grep -E '5080|8080|5432|6379|9000|9001|9090|3100|3000'   # macOS/Linux
  ```

### **Route A — Running with the UI / Visual Studio**

**Required**

| Tool | Version | Why |
|---|---|---|
| **Docker Desktop** | current | Runs the whole stack. Must be started before anything else. |
| **Visual Studio 2026** | any edition | VS 2022 will not open this solution — no .NET 10 support |
| **.NET SDK** | **10.0.100 or later** | Bundled with VS 2026 |
| **Git** | any | |
| **A modern browser** | Chrome or Edge | Scalar, Minio console, Grafana and Keycloak are all browser UIs |

- Visual Studio workloads: **ASP.NET and web development**.
- *Container development tools* is **optional** — useful for the Containers window, not required.

**Recommended — for inspecting state**

| Tool | What you use it for | Connection |
|---|---|---|
| **DBeaver** | Read `shopping_items`, `item_images`, `embedding_status`; run the hybrid-search SQL by hand | `localhost`, port `5432` (or `5433`), db/user/password from `.env` |
| **RedisInsight** | See cached search results, their TTL, and cache keys partitioned by user and A/B variant | `localhost`, port `6379`, no password |

- Neither is required. **Both are there so you can watch it work rather than take the API's word for it.**

**Not needed**

- **No local PostgreSQL, Redis, Minio, Keycloak or Ollama installation.** All run in containers.
- Installing local copies is the usual cause of the port clashes above.

### **Route B — Running from the command line**

**Required**

| Tool | Version | Why |
|---|---|---|
| **Docker Engine + Compose v2** | current | `docker compose`, not the older `docker-compose` |
| **Git** | any | |
| **curl** | any | Present by default on Windows 10+, macOS and most Linux distributions |

**Optional**

| Tool | Why |
|---|---|
| **.NET SDK 10.0.100+** | Only to run `dotnet test` |
| **jq** | Pretty-printing JSON responses |

- **No IDE required**, and no database or cache client either — `psql` and `redis-cli` are already inside the containers:

  ```bash
  docker compose exec postgres psql -U shoppinglist -d shoppinglist
  docker compose exec redis redis-cli
  ```

- **Windows PowerShell users:** use `curl.exe`, not `curl`. The bare name is an alias for `Invoke-WebRequest`, which does not understand `-d`, `-F` or `-H`. Line continuation is a backtick `` ` ``, not a backslash.

### **Verify before starting**

```bash
docker --version           # 24.x or newer
docker compose version     # v2.x
git --version
dotnet --list-sdks         # must include a 10.0.x entry, if you intend to build or test
docker ps                  # an error here means Docker Desktop has not started
```

---

## Docker Resources

**The stack is not small.** Eleven containers, nine images and a local embedding model. Budget the space before the first `docker compose up --build`, because running out mid-pull leaves a half-populated cache that is slower to recover from than starting clean.

### **Approximate download and disk footprint**

Figures are indicative — image sizes drift with each tag, and your platform (amd64 vs arm64) changes them. Verify on your own machine with `docker system df` after the first run.

| What | Approx. size | Notes |
|---|---|---|
| `ollama/ollama` | ~1.5 GB | Largest single image — ships GPU runtime libraries whether or not you have a GPU |
| `pgvector/pgvector:pg17` | ~450 MB | PostgreSQL 17 with the vector extension compiled in |
| `quay.io/keycloak/keycloak:26.0` | ~450 MB | JVM-based |
| `grafana/grafana` | ~450 MB | |
| `shopping-list-api:local` | ~300 MB | Built locally from the ASP.NET runtime base |
| `minio/minio` | ~180 MB | |
| `prom/prometheus` | ~120 MB | |
| `grafana/loki` | ~85 MB | |
| `redis:7.4-alpine` | ~45 MB | |
| **Images subtotal** | **≈ 3.5 GB** | |
| .NET SDK image + build layer cache | ~1.5–2 GB | Pulled by the multi-stage `Dockerfile` build, kept for fast rebuilds |
| `nomic-embed-text` model | ~275 MB | Downloaded by `sl-ollama-init` into the `ollama-models` volume |
| Data volumes (Postgres, Minio, Redis, Prometheus, Loki) | ~200–500 MB | Grows with use; Prometheus and Loki grow fastest if the stack runs for days |
| **Realistic total** | **≈ 6 GB** | |

- **Plan for 12 GB free**, not 6. Docker needs headroom to unpack layers, and the build cache grows with each rebuild.
- **The first run is the slow one** — 10–15 minutes on a normal connection. Later starts take under a minute because nothing is re-downloaded.

### **Check what you are actually using**

```bash
docker system df            # images, containers, volumes, build cache — with reclaimable amounts
docker system df -v         # itemised, per image and per volume
docker images
docker volume ls
```

### **Reclaiming space**

```bash
docker builder prune                # build cache only — safe, keeps images and data
docker image prune -a               # images not used by any container
docker system prune -a --volumes    # everything unused, INCLUDING volumes
```

> **`--volumes` deletes your data.** All users, items, images and Keycloak state go with it. It is the same effect as `docker compose down -v` — you will need to sign up again. Use the first two commands unless you specifically want a clean slate.

### **If your C: drive is short on space — move Docker to another drive**

On Windows, Docker Desktop stores everything in a single virtual disk. Moving it moves images, volumes and build cache together — you do not need to relocate anything per-project, and **the repository itself can stay wherever it is**. Nothing in this project references a Docker path.

**Docker Desktop with the WSL 2 backend (the normal setup):**

1. Quit any running stack: `docker compose down`
2. Open **Docker Desktop → Settings → Resources → Advanced**
3. Find **Disk image location**
4. Click **Browse** and choose a folder on the drive with space, for example `D:\DockerData`
5. **Apply & restart**

Docker Desktop moves the existing virtual disk for you. Allow several minutes and do not interrupt it — the move is a full copy of everything above.

**If the setting is greyed out or the move fails**, do it manually. Quit Docker Desktop first:

```powershell
wsl --shutdown
wsl --export docker-desktop-data D:\docker-desktop-data.tar
wsl --unregister docker-desktop-data
wsl --import docker-desktop-data D:\DockerData D:\docker-desktop-data.tar --version 2
```

Then start Docker Desktop and confirm your images survived with `docker images`. Delete the `.tar` once you have.

**On Linux**, set `data-root` in `/etc/docker/daemon.json` and restart the daemon:

```json
{ "data-root": "/mnt/bigdisk/docker" }
```

```bash
sudo systemctl restart docker
```

**One thing to know about WSL 2:** the virtual disk **grows but never shrinks on its own**. Deleting images frees space inside it, not on your drive. To reclaim the space on Windows itself, run `Optimize-VHD` from an elevated PowerShell, or use Docker Desktop's **Settings → Resources → Advanced → Disk image size** slider after a prune.

## Only Docker

- **This project is intended to be run through Docker Compose.**
- Configuration is supplied **entirely through the `.env` file**, which Compose reads and injects into the containers.
- **Running the API outside Docker** — from an IDE, or with `dotnet run` — **bypasses that file**, so the application falls back to its built-in defaults and may not produce the intended results.
- Copy `.env.example` to `.env` before starting the stack, and start it with `docker compose up -d --build`.
- **`.env.example` is the configuration surface of record.** Every value a reviewer might want to change — the vector relevance floor, page-size caps, ranking weights, token lifetimes — is listed there with an explanatory comment.

> **One exception.** The **integration tests run outside Compose** and start their own containers through Testcontainers. `dotnet test` therefore works without `.env` — only Docker running and the .NET 10 SDK.

---

## Setup — Run This First

### **1. Clone the repository**

```bash
git clone https://github.com/mallikinterview/Shopping-List.git
cd Shopping-List
```

### **2. Create your `.env` file**

After cloning or downloading the repository, **copy `.env.example` to `.env`** in the repository root. **Compose reads `.env`, and nothing starts correctly without it.**

```powershell
Copy-Item .env.example .env      # Windows PowerShell
```

```bash
cp .env.example .env             # macOS / Linux
```

- The values in `.env.example` are **working development defaults** — the stack comes up with them unchanged.
- **If PostgreSQL is already running on your machine**, set `POSTGRES_PORT=5433` in `.env`. That port is published for local tools such as DBeaver only; nothing inside the stack uses it.
- **`.env` is gitignored and must never be committed.**

### **3. Start the stack**

```bash
docker compose up -d --build
```

### **4. Wait for it to settle**

```bash
docker compose ps
```

- Nine long-running services should report **`healthy`**.
- `sl-ollama-init` and `sl-migrator` should read **`exited (0)`** — they are one-shot jobs, and that is success.

### **5. Confirm**

```bash
curl http://localhost:5080/health/ready
```

- All four dependencies should report **`Healthy`**.
- Then open **<http://localhost:5080/scalar/v1>**.

### **Starting over from a clean slate**

If the stack gets into a confusing state — or you simply want to prove it comes up correctly from nothing, which is what a reviewer's first run will be:

```bash
docker compose down -v
docker compose up -d --build
```

- **`-v` removes the volumes**, so PostgreSQL, Minio, Redis and Keycloak all start empty.
- **Everything you created is gone**: your account, your items, your uploaded images. Sign up again and re-create a few items.
- **Every existing token stops working.** Keycloak re-imports the realm with new signing keys, so an access or refresh token from before the wipe is rejected with a 401 that looks identical to an expired one. This surprises people — it is the volume wipe, not a bug.
- The embedding model is **not** re-downloaded unless you also remove the `ollama-models` volume, so this is much faster than the very first run.

**If you are debugging from Visual Studio**, add the third command so the container releases port 5080 for the debugger:

```bash
docker compose down -v
docker compose up -d --build
docker compose stop api
```

Then start the API from Visual Studio as described in [Using Visual Studio](#using-visual-studio).

---

## Using Visual Studio

### **1–2. Clone and create `.env`**

- As in [Setup](#setup--run-this-first) above.

### **3. Start the supporting stack**

```powershell
docker compose up -d --build
docker compose ps
```

- Wait until every service reports `healthy` and `sl-migrator` has exited with code 0.

### **4. Confirm the stack is up**

- Open <http://localhost:5080/health/ready>. All dependencies should report `Healthy`.

### **5. Free the API port for the debugger**

```powershell
docker compose stop api
```

- **The containerised API holds port 5080.** Stopping it lets Visual Studio bind the same port, so every URL in this README keeps working.
- **Everything the API depends on stays running** — PostgreSQL, Keycloak, Redis, Minio, Ollama, Prometheus, Loki, Grafana.

### **6. Open and run**

- Open `ShoppingList.sln` in **Visual Studio 2026**.
- Set **ShoppingList.Api** as the startup project.
- Select the **http** launch profile — not IIS Express, not the container profile.
- Press **F5**. The browser opens on <http://localhost:5080/scalar/v1>.

### **7. Breakpoints worth setting first**

| File | What it shows |
|---|---|
| `Features/Auth/AuthEndpoints.cs` | Signup and token exchange against Keycloak |
| `Common/Validation/ValidationFilter.cs` | Every request body before the handler sees it |
| `Features/Items/ItemEndpoints.cs` | CRUD, and the keyset pagination predicate |
| `Features/Search/HybridSearchService.cs` | Variant assignment, cache lookup, fusion |
| `Infrastructure/Embeddings/` | The background embedding worker |
| `Common/Errors/GlobalExceptionHandler.cs` | How every exception becomes ProblemDetails |

### **8. When you are finished**

```powershell
docker compose start api      # restore the containerised API
docker compose down           # or stop everything, keeping data
docker compose down -v        # or wipe data — you will need to sign up again
```

> **Caveat.** Running from Visual Studio **bypasses `.env`** — the .NET host reads `appsettings.json` instead, so any value you overrode in `.env` will not apply. See [Only Docker](#only-docker). For an accurate picture of the system as delivered, run the API in its container.

---

## Using Command Line

### **1. Clone and configure**

```bash
git clone https://github.com/mallikinterview/Shopping-List.git
cd Shopping-List
cp .env.example .env
```

- On Windows PowerShell: `Copy-Item .env.example .env`
- If you already run PostgreSQL on 5432, set `POSTGRES_PORT=5433` in `.env`.

### **2. Start everything**

```bash
docker compose up -d --build
```

### **3. Verify**

```bash
docker compose ps
curl http://localhost:5080/health/live
curl http://localhost:5080/health/ready
```

### **4. Where everything is**

| Service | URL | Credentials |
|---|---|---|
| **API (Scalar)** | <http://localhost:5080/scalar/v1> | — |
| **OpenAPI document** | <http://localhost:5080/openapi/v1.json> | — |
| **Keycloak** | <http://localhost:8080> | `KEYCLOAK_ADMIN` / `KEYCLOAK_ADMIN_PASSWORD` in `.env` |
| **Minio console** | <http://localhost:9001> | `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` in `.env` |
| **Grafana** | <http://localhost:3000> | `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` in `.env` |
| **Prometheus** | <http://localhost:9090> | — |
| **PostgreSQL** | `localhost:5432` | `POSTGRES_USER` / `POSTGRES_PASSWORD` in `.env` |

### **5. Logs**

```bash
docker compose logs -f api
docker compose logs --tail=50 ollama
```

- Structured logs also ship to Loki and are queryable in Grafana.

### **6. Run the tests**

```bash
dotnet test
```

- The integration tests start their own containers through **Testcontainers**, so they do not use the Compose stack and do not need `.env` — only Docker running and the .NET 10 SDK.

### **7. Stop**

```bash
docker compose down          # keeps data
docker compose down -v       # wipes volumes: all users, items, images and Keycloak state
```

- **After `down -v`, existing access and refresh tokens are invalid** — Keycloak re-imports the realm with new signing keys. Sign up again.

---

## Check The Processes

### **Everything at once**

```bash
docker compose ps
```

| Container | Component |
|---|---|
| `sl-api` | The WebAPI |
| `sl-postgres` | PostgreSQL with pgvector |
| `sl-minio` | Minio |
| `sl-redis` | Redis |
| `sl-ollama` | Ollama |
| `sl-prometheus` | Prometheus |
| `sl-loki` | Loki |
| `sl-grafana` | Grafana |
| `sl-keycloak` | Keycloak |
| `sl-ollama-init` | Pulls the embedding model, then exits |
| `sl-migrator` | Applies the database schema, then exits |

```bash
curl http://localhost:5080/health/ready
```

- **The fastest single check in this document.** One call reports PostgreSQL, Redis, Ollama and Keycloak. If all four read `Healthy`, the stack is up.

### **i. The WebAPI**

- **Browser** — <http://localhost:5080/scalar/v1> renders the API reference; <http://localhost:5080/openapi/v1.json> returns the OpenAPI document.
- **Command line**

  ```bash
  curl http://localhost:5080/health/live      # liveness — checks nothing external, by design
  curl http://localhost:5080/health/ready     # readiness — reports every dependency
  docker compose logs --tail=30 api
  ```

- **Functional check** — sign up and request a token. If that round trip works, the API, the database and Keycloak are all working together.
- The API listens on **8080 inside the container**, published on **5080** on the host. That is why Prometheus scrapes `api:8080` while you use `localhost:5080`.

### **ii. PostgreSQL (with the pgvector extension)**

- **Tool** — DBeaver: host `localhost`, port `5432` (or `5433`), database `shoppinglist`, user `shoppinglist`, password from `.env`.
- **Command line**

  ```bash
  docker compose exec postgres pg_isready -U shoppinglist
  docker compose exec postgres psql -U shoppinglist -d shoppinglist -c "\dt"
  ```

- **Confirm pgvector is actually installed**

  ```bash
  docker compose exec postgres psql -U shoppinglist -d shoppinglist \
    -c "SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';"
  ```

- **Confirm the column really is a vector**

  ```bash
  docker compose exec postgres psql -U shoppinglist -d shoppinglist -c "\d shopping_items"
  ```

  Look for `embedding | vector(768)` and a `search_vector | tsvector` generated column — **the two halves of hybrid search, side by side**. `\di shopping_items*` shows the HNSW index on the embedding and the GIN index on the tsvector.

- **Functional check** — cosine distance between two stored items:

  ```sql
  SELECT a.name, b.name, ROUND((a.embedding <=> b.embedding)::numeric, 3) AS distance
  FROM   shopping_items a
  JOIN   shopping_items b ON a.id < b.id
  ORDER  BY distance
  LIMIT  5;
  ```

- **Two databases live on this server:** `shoppinglist` for the application, `keycloak` for the identity provider.

### **iii. Minio**

- **Browser** — <http://localhost:9001>, log in with `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` from `.env`. Object Browser → bucket **`shopping-list-images`**.
- **Command line**

  ```bash
  curl -I http://localhost:9000/minio/health/live
  docker compose logs --tail=20 minio
  ```

- **Functional check** — upload an image through the API, then find it in the console. Objects are laid out as `shopping-list-images/{userId}/{itemId}/{imageId}.jpg`.
- While you are there: the bucket's access policy reads **Private**, and **the object name bears no resemblance to the file you uploaded** — your filename is metadata only, never part of the storage path.

### **iv. Redis**

- **Tool** — RedisInsight: host `localhost`, port `6379`, no password.
- **Command line**

  ```bash
  docker compose exec redis redis-cli PING          # PONG
  docker compose exec redis redis-cli INFO server
  ```

- **Functional check** — prove the cache is used, not just running:

  ```bash
  docker compose exec redis redis-cli FLUSHALL
  # ... create an item, list items, get one by id, run a search ...
  docker compose exec redis redis-cli KEYS 'sl:*'
  ```

  ```
  sl:item:{userId}:{itemId}:v{version}                                  ← GET /items/{id}
  sl:items:{userId}:{filterHash}:v{version}                             ← GET /items
  sl:search:{userId}:{variant}:{queryHash}:{filterHash}:v{version}      ← POST /search
  sl:ver:{userId}                                                       ← the version stamp
  ```

- **Note the `variant` segment** on search keys — the A/B arms have separate cache namespaces, so one arm can never be served the other's results.
- **Note the `:v{version}` suffix on all three.** `sl:ver:{userId}` is a counter; every write for that user increments it, which makes every previously cached key for them unreachable in a single `INCR` — no key enumeration, no `KEYS` scan blocking the Redis event loop. The orphans expire on their own TTL.
- Watch it work: run `GET /api/v1/items`, then create an item, then run the list again. `redis-cli GET sl:ver:{userId}` will have gone up by one, and the old page is no longer reachable.
- Check the expiry: `docker compose exec redis redis-cli TTL <key>` → between **300 and 360 seconds**, the base TTL plus random jitter so entries do not all expire at once and stampede the database.
- Send the same search twice: the second response reports `"cached": true`.

### **v. Ollama (running an embedding model)**

- **Command line**

  ```bash
  docker compose exec ollama ollama list
  docker compose logs ollama-init
  ```

- **`ollama list` must show `nomic-embed-text`.** If it does not, the model was never pulled and nothing can be embedded.
- **Functional check** — create an item and watch `"embeddingStatus"` move `Pending` → `Ready` within a few seconds. `Failed` means Ollama was reachable but errored.
- The model produces **768-dimension** vectors, which is why the column is `vector(768)`.

### **vi. Prometheus**

- **Browser** — <http://localhost:9090> → **Status → Targets**. Four jobs, all `UP`:

  | Job | Target |
  |---|---|
  | `shopping-list-api` | `api:8080` |
  | `prometheus` | `localhost:9090` |
  | `keycloak` | `keycloak:9000` |
  | `loki` | `loki:3100` |

- **Functional check** — query `http_server_request_duration_seconds_count` in the expression browser, make a few API calls, refresh, and watch the counter rise.
- Application-specific series are exported too: search duration, cache hits and misses, and A/B assignment counts by variant.
- **Command line**

  ```bash
  curl http://localhost:9090/-/healthy
  curl -s 'http://localhost:9090/api/v1/targets' | jq '.data.activeTargets[].health'
  curl http://localhost:5080/metrics | head -30
  ```

### **vii. Loki**

- Loki has **no UI of its own** — it is queried through Grafana. Check it directly first.

  ```bash
  curl http://localhost:3100/ready                              # "ready"
  curl -s 'http://localhost:3100/loki/api/v1/labels' | jq
  ```

- The labels response confirms it is **receiving** logs, not merely listening.

### **viii. Grafana**

- **Browser** — <http://localhost:3000>, log in with `GRAFANA_ADMIN_USER` / `GRAFANA_ADMIN_PASSWORD` from `.env`.
- **Check the plumbing** — Connections → Data sources. **Prometheus** and **Loki** are provisioned automatically; click **Test** on each.
- **Functional check — seeing logs in Grafana:**
  1. Make a few API calls so there is something to look at.
  2. Grafana → **Explore** → select the **Loki** data source.
  3. Query: `{app="shopping-list-api"}`
  4. Press **Run query**.
- Expand a log line — **every entry carries a `CorrelationId`**, plus `UserId` where the caller was authenticated, so a single request can be followed end to end.
- Filter to one request: `{app="shopping-list-api"} |= "<correlationId from any error response>"`
- **Dashboards** in the left menu show request rates, latency and search metrics from Prometheus.

### **ix. Keycloak**

- **Browser** — <http://localhost:8080>, sign in with `KEYCLOAK_ADMIN` / `KEYCLOAK_ADMIN_PASSWORD` from `.env`.
- Switch to the **shopping-list** realm (top-left dropdown) and confirm:
  - **Clients** → `shopping-list-api` exists
  - **Users** → accounts created through `POST /api/v1/auth/signup` appear here
  - **Realm roles** → `user` and `admin`
  - **Realm settings → Sessions / Tokens** → access token lifespan 1 hour
- **Command line**

  ```bash
  curl http://localhost:8080/realms/shopping-list/.well-known/openid-configuration | jq .issuer
  curl http://localhost:8080/realms/shopping-list/protocol/openid-connect/certs | jq '.keys[0].kid'
  ```

- The second returns the **signing keys**. These are regenerated by `docker compose down -v`, which is why every token issued before a volume wipe stops working.

### **If something is not healthy**

```bash
docker compose ps                      # which one
docker compose logs --tail=100 <name>  # why
docker compose restart <name>
docker compose up -d --force-recreate <name>
```

- **A port already in use** stops a container from starting at all. Every host port is remappable in `.env`.
- **`sl-ollama-init` failing** leaves Ollama running with no model. Everything looks healthy, and every item stays `Pending` forever.

---

## Using Each Endpoint — Scalar UI

Open **<http://localhost:5080/scalar/v1>**. Every request body is pre-filled with a working example, so most endpoints can be exercised by pressing **Send**.

### **First: get a token**

- **Authentication → Create an account** → **Send**
- **Authentication → Exchange credentials for tokens** → **Send**
- Copy the `accessToken` from the response
- Open the **Authentication** panel at the top of the sidebar, choose **Bearer**, and paste it into **Bearer Token**

The token is attached to every subsequent request automatically and **survives a page reload**. It is valid for **one hour**.

### **1. Create an account**

`POST /api/v1/auth/signup` — anonymous

```json
{
  "username": "reviewer",
  "email": "reviewer@example.com",
  "firstName": "Alex",
  "lastName": "Reviewer",
  "password": "Str0ng!Passphrase"
}
```

- **Limits** — `username` 3–64 matching `^[a-zA-Z0-9._-]+$` · `email` 1–254, valid address · `firstName` 1–64 · `lastName` 1–64 · `password` 10–128
- The realm adds its own policy: minimum length 10, must differ from the username, last 3 passwords cannot be reused.

| Status | When |
|---|---|
| **201** | Created |
| **400** | Validation failed |
| **409** | Username or email already taken — pressing Send twice produces this |
| **429** | More than 10 auth requests in 60 seconds |

- **`firstName` and `lastName` are mandatory** because Keycloak's declarative user profile marks them required. An account created without them returns 201 and then fails every login with *"Account is not fully set up"* — the two halves disagree and only the first is visible from outside.
- The password is forwarded to Keycloak through a scoped service account and is **never stored by this API**.

### **2. Exchange credentials for tokens**

`POST /api/v1/auth/token` — anonymous

```json
{ "username": "reviewer", "password": "Str0ng!Passphrase" }
```

```json
{
  "accessToken": "eyJhbGciOi...",
  "refreshToken": "eyJhbGciOi...",
  "expiresIn": 3600,
  "tokenType": "Bearer"
}
```

| Status | When |
|---|---|
| **200** | OK |
| **400** | Wrong password **or** no such user — deliberately indistinguishable, so the endpoint is not a username oracle |
| **429** | Auth rate limit |

- This is the OAuth2 **Direct Access Grant**, deprecated in OAuth 2.1 because the client handles the user's password. It exists so the API can be driven from a terminal. **Authorization Code + PKCE is enabled on the same client** and is the intended production flow.

### **3. Exchange a refresh token for a new access token**

`POST /api/v1/auth/refresh` — anonymous

```json
{ "refreshToken": "eyJhbGciOi..." }
```

- You receive a new `accessToken` **and a new `refreshToken`**.
- **Refresh tokens rotate and are single-use** (`revokeRefreshToken`, `refreshTokenMaxReuse=0`) — replaying one fails.
- Paste the new access token into the Authentication panel and keep the new refresh token.
- Works for up to **2 hours of inactivity**, within a **10-hour session ceiling**.

| Status | When |
|---|---|
| **200** | OK |
| **400** | Expired, already used, or invalid |

### **4. Create a shopping list item**

`POST /api/v1/items` — token required

```json
{
  "name": "Whole milk",
  "notes": "Prefer the organic one",
  "quantity": 2,
  "unit": "litres",
  "category": "Dairy"
}
```

- **Limits** — `name` 1–200 · `notes` ≤ 2000 · `quantity` > 0 and ≤ 100000 · `unit` ≤ 32 · `category` ≤ 64. `notes`, `unit` and `category` accept `null`.
- The response carries the new **`id` — copy it**, every other item endpoint needs it.
- It also carries `"embeddingStatus": "Pending"`. A background worker embeds the item within seconds, after which it becomes reachable by vector search.

| Status | When |
|---|---|
| **201** | Created; `Location` header points at the new item |
| **400** | Validation failed |
| **401** | Missing or expired token |
| **429** | More than 100 requests in 60 seconds |

### **5. List the caller's items**

`GET /api/v1/items` — token required

| Parameter | Default | Limits |
|---|---|---|
| `cursor` | — | Opaque; pass `nextCursor` from the previous response |
| `pageSize` | 20 | 1–50 |
| `category` | — | Exact match, ≤ 64 characters |
| `isPurchased` | — | Omit to return both |

```json
{
  "items": [ ... ],
  "nextCursor": "MjAyNi0wOC0xNlQx...",
  "pageSize": 20,
  "hasMore": true
}
```

- To page: copy `nextCursor` into the `cursor` box and Send again, until `nextCursor` is `null`.
- **This is keyset pagination.** Inserts and deletes elsewhere cannot cause an item to be skipped or repeated, and the last page costs the same as the first.

| Status | When |
|---|---|
| **200** | OK |
| **400** | `pageSize` outside 1–50, `category` over 64 characters, or a cursor that will not decode |

- **Bad input is rejected, not corrected.** A `pageSize` of 500 returns 400 rather than a silently clamped page of 50 — a response that quietly answers a different question is worse than an error. A corrupted cursor likewise fails rather than restarting at page 1, because a client paginating in a loop would never terminate.

### **6. Get a single item**

`GET /api/v1/items/{id}` — token required

- The id comes from the create response, the list response, or a search hit. It is a **server-generated UUIDv7**; clients never construct one.
- The response includes the `images` array, each entry with a freshly signed download URL.

| Status | When |
|---|---|
| **200** | OK |
| **404** | No such item — **or it belongs to another user** |

- **404 rather than 403 is deliberate.** A 403 would confirm the id is real, turning the endpoint into an enumeration oracle.
- Ownership is not checked in the handler at all: **a global query filter scopes every read to the caller**, so another user's row is never fetched in the first place.

### **7. Replace an item**

`PUT /api/v1/items/{id}` — token required

```json
{
  "name": "Whole milk",
  "notes": "Prefer the organic one",
  "quantity": 3,
  "unit": "litres",
  "category": "Dairy",
  "isPurchased": true
}
```

- **This is a replace, not an edit.** Any field omitted is set to `null`. Fetch the item, change what you want, send the whole object back.
- **Changing `name` or `notes` invalidates the embedding**: the response returns `"embeddingStatus": "Pending"` and the item is re-queued. Without that, a renamed item would keep matching searches for its old description — a stale-index bug that produces confident, wrong results.

| Status | When |
|---|---|
| **200** | Updated |
| **400** | Validation failed |
| **404** | No such item, or not yours |
| **409** | Modified concurrently by another request — re-read and reapply |

### **8. Delete an item and any attached images**

`DELETE /api/v1/items/{id}` — token required

- Returns **204 No Content**. **An empty Response panel is success** — a 204 has no body by definition.
- The item's row, its image rows and its Minio objects all go.
- **The database commits first, storage is cleaned up after.** If an object delete fails it is logged rather than thrown, because the row is already gone and reporting failure would misstate what happened.

| Status | When |
|---|---|
| **204** | Deleted |
| **404** | No such item, or not yours |

- Deleting twice returns 404 the second time. The **effect** is idempotent — the item is gone either way.

### **9. Attach an image to an item**

`POST /api/v1/items/{itemId}/images` — token required

- Paste the item id into `itemId`, attach a file to the **`file`** field in the multipart form, **Send**.
- **PNG, JPEG, GIF or WebP, up to 5 MB.**

```json
{
  "id": "01a00b0f-d2a0-7021-abd0-52e465544d25",
  "contentType": "image/jpeg",
  "sizeBytes": 35082,
  "originalFileName": "milk.jpg",
  "url": "http://localhost:9000/shopping-list-images/...?X-Amz-Expires=900&X-Amz-Signature=...",
  "createdAt": "2026-08-16T14:53:01Z"
}
```

- Paste the `url` into a browser tab to view the image. It is a **presigned Minio link valid for 15 minutes**, regenerated every time the item is read — the bucket itself is private.
- **The format is detected from the file's magic bytes**, not the extension or declared `Content-Type`. Rename a text file to `.jpg` and it is rejected with 400.
- **The storage key is server-generated** — `{userId}/{itemId}/{imageId}.jpg`. Your filename is kept as metadata only, so a name like `../../secret.png` cannot influence where bytes land.
- **Size is checked before any byte is read**, so an oversized upload cannot be used to make the server buffer it first.

> **Scalar's file control.** Its uploader may create a *new* form field named after your file rather than filling the declared `file` field. The API accepts the first non-empty file part whatever it is called, so either behaviour works. If the Code Snippet panel shows two `--form` lines, delete the spare row.

| Status | When |
|---|---|
| **201** | Uploaded |
| **400** | No file, empty file, or not a supported image format |
| **404** | No such item, or not yours |
| **413** | Larger than 5 MB |
| **429** | More than 20 uploads in 60 seconds — stricter than the standard limit |

### **10. Remove an image from an item**

`DELETE /api/v1/items/{itemId}/images/{imageId}` — token required

- **`itemId`** — the **outer** `id` from Get a single item
- **`imageId`** — the `id` **inside the `images` array**, or the `id` returned by the upload
- The item survives; only that image is removed, from both the database and Minio.

| Status | When |
|---|---|
| **204** | Deleted |
| **404** | Either id is wrong, or the two do not belong together |

- **Both ids are in the lookup predicate**, so a real `imageId` paired with the wrong `itemId` is a 404 — an image cannot be deleted by guessing its id alone.

### **11. Hybrid search**

`POST /api/v1/search` — token required

- Only `query` is required:

  ```json
  { "query": "something to put on toast" }
  ```

- Full form:

  ```json
  {
    "query": "something to put on toast",
    "category": "Preserves",
    "isPurchased": false,
    "limit": 10,
    "offset": 0
  }
  ```

- **Limits** — `query` 1–500 · `category` ≤ 64 · `limit` > 0 and ≤ 50 (default 20) · `offset` ≥ 0 and ≤ 500 (default 0)

Each hit reports **both component scores**, so a ranking can be explained:

```json
{
  "name": "Strawberry jam",
  "score": 0.0328,
  "vectorSimilarity": 0.71,
  "textScore": null,
  "vectorRank": 1,
  "textRank": null
}
```

- **`textScore: null` means the full-text branch did not find it** — that hit came from vector similarity alone. Run `{"query": "bread"}` for the contrast: a literal word match populates both, and fusion combines them into a higher score.
- `diagnostics` reports the A/B variant, the fusion strategy, whether the vector branch participated, whether the answer came from cache, and the server-side duration. The same information is on the **`X-Experiment-Variant`** and **`X-Ranking-Strategy`** response headers.
- Send an identical query twice and the second reports `"cached": true`.
- **An empty result set is a legitimate answer.** A relevance floor of **0.48** cosine distance keeps unrelated items out. Without it an approximate-nearest-neighbour index returns the *k* nearest rows whether or not any are near — so "car tyres" over a grocery list returns bin bags and bananas, confidently ranked. The value was measured, not chosen: relevant pairs on this corpus run 0.276–0.474 and irrelevant ones start at 0.504, so 0.48 sits in the gap. `.env.example` carries the full table.
- If the embedding service is unavailable the search **degrades to keyword-only** rather than failing, and `"vectorSearchUsed": false` says so.

| Status | When |
|---|---|
| **200** | OK |
| **400** | Validation failed |
| **429** | Rate limited |

### **Health and metrics — anonymous**

| Endpoint | Purpose |
|---|---|
| `GET /health/live` | **Liveness.** Checks nothing external by design — a dependency outage must not cause healthy replicas to be restarted. |
| `GET /health/ready` | **Readiness.** Reports each dependency; returns 200 even when Degraded, because an instance without its cache can still serve traffic. |
| `GET /metrics` | Prometheus scrape endpoint. |

---

## Using Each Endpoint — Command Line

All examples target `http://localhost:5080`.

> **Windows PowerShell:** use `curl.exe`, not `curl`. Replace the trailing `\` continuations with a backtick `` ` ``, and set variables with `$TOKEN = "..."`.

### **First: get a token**

```bash
curl -X POST http://localhost:5080/api/v1/auth/signup \
  -H "Content-Type: application/json" \
  -d '{"username":"reviewer","email":"reviewer@example.com","firstName":"Alex","lastName":"Reviewer","password":"Str0ng!Passphrase"}'

curl -X POST http://localhost:5080/api/v1/auth/token \
  -H "Content-Type: application/json" \
  -d '{"username":"reviewer","password":"Str0ng!Passphrase"}'
```

```bash
TOKEN=<paste the accessToken>
```

With `jq`, in one step:

```bash
TOKEN=$(curl -s -X POST http://localhost:5080/api/v1/auth/token \
  -H "Content-Type: application/json" \
  -d '{"username":"reviewer","password":"Str0ng!Passphrase"}' | jq -r .accessToken)
```

### **1. Create an account** — `POST /api/v1/auth/signup`

```bash
curl -X POST http://localhost:5080/api/v1/auth/signup \
  -H "Content-Type: application/json" \
  -d '{"username":"reviewer","email":"reviewer@example.com","firstName":"Alex","lastName":"Reviewer","password":"Str0ng!Passphrase"}'
```

- **Limits** — `username` 3–64, `^[a-zA-Z0-9._-]+$` · `email` ≤ 254 · `firstName`, `lastName` 1–64 · `password` 10–128
- `201` · `400` validation · `409` already taken · `429` more than 10 auth requests in 60 s

### **2. Exchange credentials for tokens** — `POST /api/v1/auth/token`

```bash
curl -X POST http://localhost:5080/api/v1/auth/token \
  -H "Content-Type: application/json" \
  -d '{"username":"reviewer","password":"Str0ng!Passphrase"}'
```

- `200` · `400` bad credentials, indistinguishable from an unknown user · `429`

### **3. Refresh** — `POST /api/v1/auth/refresh`

```bash
curl -X POST http://localhost:5080/api/v1/auth/refresh \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"PASTE_REFRESH_TOKEN"}'
```

- Returns a new access token **and a new refresh token**; the old one is revoked on use. Replaying it fails with `400`.
- Usable for up to **2 hours of inactivity**, within a **10-hour session ceiling**.

### **4. Create an item** — `POST /api/v1/items`

```bash
curl -X POST http://localhost:5080/api/v1/items \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Whole milk","notes":"Prefer the organic one","quantity":2,"unit":"litres","category":"Dairy"}'
```

- **Limits** — `name` 1–200 · `notes` ≤ 2000 · `quantity` > 0 and ≤ 100000 · `unit` ≤ 32 · `category` ≤ 64
- Capture the id for later calls:

  ```bash
  ITEM_ID=$(curl -s -X POST http://localhost:5080/api/v1/items \
    -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
    -d '{"name":"Whole milk","quantity":2,"category":"Dairy"}' | jq -r .id)
  ```

- `201` · `400` validation · `401` missing or expired token · `429` more than 100 requests in 60 s

### **5. List items** — `GET /api/v1/items`

```bash
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/v1/items

curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5080/api/v1/items?pageSize=5&category=Dairy&isPurchased=false"

curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5080/api/v1/items?cursor=PASTE_NEXT_CURSOR&pageSize=5"
```

- `pageSize` 1–50 (default 20) · `category` exact match, ≤ 64 · `cursor` opaque, from the previous `nextCursor`. Page until `nextCursor` is `null`.
- `200` · `400` for an out-of-range `pageSize`, an over-long `category`, or a cursor that will not decode. **Out-of-range input is rejected, not silently clamped.**

### **6. Get one item** — `GET /api/v1/items/{id}`

```bash
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/v1/items/$ITEM_ID
```

- `200` · `404` no such item, or it belongs to another user. **404 rather than 403** so the endpoint cannot be used to enumerate valid ids.

### **7. Replace an item** — `PUT /api/v1/items/{id}`

```bash
curl -X PUT http://localhost:5080/api/v1/items/$ITEM_ID \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"Whole milk","notes":"Prefer the organic one","quantity":3,"unit":"litres","category":"Dairy","isPurchased":true}'
```

- **Full replacement** — any omitted field becomes `null`.
- Changing `name` or `notes` resets `embeddingStatus` to `Pending` and re-queues the item.
- `200` · `400` validation · `404` not found or not yours · `409` concurrent modification

### **8. Delete an item** — `DELETE /api/v1/items/{id}`

```bash
curl -i -X DELETE http://localhost:5080/api/v1/items/$ITEM_ID \
  -H "Authorization: Bearer $TOKEN"
```

- `-i` prints the status line, since a 204 has no body.
- Removes the item, its image rows and its Minio objects.
- `204` · `404` not found or not yours

### **9. Attach an image** — `POST /api/v1/items/{itemId}/images`

```bash
curl -X POST http://localhost:5080/api/v1/items/$ITEM_ID/images \
  -H "Authorization: Bearer $TOKEN" \
  -F "file=@/path/to/image.jpg"
```

- **PNG, JPEG, GIF or WebP up to 5 MB.** The format is read from the file's magic bytes, so the extension and declared content type are ignored.
- Capture the image id:

  ```bash
  IMAGE_ID=$(curl -s -X POST http://localhost:5080/api/v1/items/$ITEM_ID/images \
    -H "Authorization: Bearer $TOKEN" -F "file=@/path/to/image.jpg" | jq -r .id)
  ```

- The `url` is a presigned Minio link **valid for 15 minutes**, regenerated every time the item is read.
- `201` · `400` empty or unsupported · `404` not found or not yours · `413` over 5 MB · `429` more than 20 uploads in 60 s

### **10. Remove an image** — `DELETE /api/v1/items/{itemId}/images/{imageId}`

```bash
curl -i -X DELETE http://localhost:5080/api/v1/items/$ITEM_ID/images/$IMAGE_ID \
  -H "Authorization: Bearer $TOKEN"
```

- **Both ids must belong together**, or the result is `404`.
- `204` · `404`

### **11. Hybrid search** — `POST /api/v1/search`

```bash
curl -X POST http://localhost:5080/api/v1/search \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"query":"something to put on toast"}'
```

With filters and paging:

```bash
curl -X POST http://localhost:5080/api/v1/search \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"query":"breakfast","category":"Dairy","isPurchased":false,"limit":10,"offset":0}'
```

- **Limits** — `query` 1–500 · `category` ≤ 64 · `limit` 1–50 (default 20) · `offset` 0–500 (default 0)

Semantic against keyword, for the contrast:

```bash
# shares no word with any item — matched by meaning alone, textScore is null
-d '{"query":"omelette ingredients"}'

# literal word match — textScore and textRank are populated
-d '{"query":"bread"}'
```

Read the A/B assignment from the headers, without parsing the body:

```bash
curl -i -X POST http://localhost:5080/api/v1/search \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"query":"breakfast"}' | grep -E "X-Experiment-Variant|X-Ranking-Strategy"
```

- Send the same query twice: the second reports `"cached": true`. Results are cached in Redis for **300 seconds**, keyed by user, variant and query.
- `200` · `400` validation · `429` rate limited

### **Health and metrics — anonymous**

```bash
curl http://localhost:5080/health/live
curl http://localhost:5080/health/ready
curl http://localhost:5080/metrics
```

---

## Embed Search

### **Only `query` is required**

- The full request takes five fields:

  ```json
  {
    "query": "something to put on toast",
    "category": "Preserves",
    "isPurchased": false,
    "limit": 10,
    "offset": 0
  }
  ```

- **But the other four are optional**, so a search can be sent with a single parameter:

  ```json
  { "query": "something to put on toast" }
  ```

- `category` and `isPurchased` are **metadata filters** — omit them and no filter is applied.
- `limit` and `offset` default to **20** and **0**.

### **Queries worth trying**

| Query | Demonstrates |
|---|---|
| `omelette ingredients` | **Pure semantic** — shares no word with any item; `textScore` is `null` |
| `something to put on toast` | Semantic across categories — jam and butter rank above bread |
| `bread` | **Keyword** — `textScore` and `textRank` populated, fused with the vector hit |
| `cleaning` | Finds dish soap while ignoring the food |
| `car tyres` | **Zero results** — the relevance floor working, not a failure |
| `something to put on toast` | Salted butter, then croissants. **Jam does not appear** — see Known Limitations |

### **If `embedding_status` shows `Pending`**

An item is only reachable by vector search once it has been embedded. Newly created and newly renamed items sit at `Pending` for a few seconds while the background worker processes them. **If they stay there, check Ollama:**

```bash
docker compose ps ollama
docker compose logs --tail=50 ollama
docker compose exec ollama ollama list
```

- **The last command must list the embedding model (`nomic-embed-text`).** If it does not, the model was never pulled and nothing can be embedded.
- `embeddingStatus` is visible per item in `GET /api/v1/items` and `GET /api/v1/items/{id}`, and in the `shopping_items.embedding_status` column.
- The three values are **`Pending`**, **`Ready`** and **`Failed`**.
- **`Failed`** means Ollama was reachable but errored; those items remain keyword-searchable.
- Searches against un-embedded items still work — they fall back to the full-text branch, and `diagnostics.vectorSearchUsed` reports it.

---

## A/B Testing

**1.** In `.env`, set:

```
SearchSettings__Experiment__VariantSplit=100
```

**2.** Run:

```
docker compose up -d --force-recreate api
docker compose exec redis redis-cli FLUSHALL
```

**3.** Search `{ "query": "breakfast" }` → note `"variant": "treatment"`, `"strategy": "weighted"` and the scores.

**4.** In `.env`, change to:

```
SearchSettings__Experiment__VariantSplit=0
```

**5.** Repeat step 2.

**6.** Search `{ "query": "breakfast" }` again → now `"variant": "control"`, `"strategy": "rrf"`, different scores.

**Same data, same query, two ranking algorithms. That's A/B testing working.**

Then set it back to `50`.

### **Other scenarios**

- **Two real users** — sign up a second account; roughly half of users land in the other arm.
- **Stickiness** — same user, several different queries: the variant never changes.
- **Off switch** — `SearchSettings__Experiment__Enabled=false` reports `"variant": "off"`, not `"control"`. A user served by default configuration is not *in* the experiment, and folding them into the control arm would contaminate it.
- **Reshuffle** — changing `SearchSettings__Experiment__Key` rebuckets users, so each experiment is independent of the last.
- **Unit tests** — `dotnet test --filter VariantAssignerTests`

> **Note for reviewers comparing results.** Assignment is a SHA-256 hash of `{experimentKey}:{userId}`, so **two people may be served different ranking strategies and see different scores for the same query.** That is the feature, not a bug — `diagnostics.variant` tells you which arm you are in.

---

## Architecture Decision

**Vertical slice architecture, not clean architecture. One project, organised by feature.**

### **What the code looks like**

```
src/ShoppingList.Api/
  Features/
    Auth/       AuthEndpoints, AuthContracts (DTOs + validators)
    Items/      ItemEndpoints, ItemContracts, ItemMappings, UploadImageEndpoint
    Search/     SearchEndpoints, SearchContracts, HybridSearchService,
                HybridSearchSql, RankingStrategy
  Data/            AppDbContext, Entities, Configurations, Interceptors
  Infrastructure/  Caching, Embeddings, Identity, Storage
  Common/          Errors, Middleware, Validation, Pagination, RateLimiting
  Configuration/   Strongly-typed options
  Experimentation/ VariantAssigner
  Telemetry/       Metrics
```

- **A feature is a folder.** Everything a request touches on its way through — route, request DTO, validator, handler, response mapping — sits together.
- **Adding a field to a shopping item means editing one folder.** The clean-architecture alternative organises the same code by *layer* — `Domain`, `Application`, `Infrastructure`, `Api` — with that same change rippling through an entity, a DTO, a command, a handler, a repository interface, its implementation, and a controller.

### **Why slices here**

- **Change is vertical, so the code is too.** Real work arrives as *"add a field"*, *"add an endpoint"*, *"change how search ranks"* — never as *"modify the application layer"*. Layered structure optimises for a kind of change that does not happen, and the cost is paid on every feature forever.
- **The database is not an implementation detail here, and pretending otherwise would be a lie.** The central feature is hybrid search: pgvector cosine distance and PostgreSQL full-text search fused in a single statement, over a stored generated `tsvector` column with a GIN index and an HNSW index on the embedding. That is not persistence *behind* the feature — it **is** the feature. Hiding it behind `IShoppingItemRepository` would abstract away the only thing worth reading, and advertise a portability that does not exist.
- **No repository pattern.** `DbSet<T>` is already a repository and `SaveChangesAsync` is already a unit of work. Wrapping them adds a layer with no behaviour, and the wrapper invariably leaks the moment something needs `Include`, projection, or a raw query.
- **The boundary that actually matters is enforced by a constraint, not by structure.** Ownership is a global query filter on the `DbContext`:

  ```csharp
  builder.Entity<ShoppingItem>()
      .HasQueryFilter(item => item.UserId == currentUser.UserId);
  ```

  A repository would place that rule in a class a future endpoint could forget to call. **A query filter is one a future endpoint cannot bypass.**

### **What was kept from clean architecture**

- **Interfaces exist where there is a genuine seam.** Every abstraction wraps an out-of-process dependency — `IObjectStorage`, `IEmbeddingGenerator`, `IItemCache`, `IVariantAssigner`, the Keycloak clients. Each has a real second implementation (a test double). **There are no interfaces with exactly one implementation added out of habit.**
- **Domain rules live on the entity, not in handlers.** `ShoppingItem.Create` and `ShoppingItem.Update` are the only ways to change an item, and `Update` enforces an invariant the endpoints never need to know about:

  ```csharp
  if (embeddableChanged)
  {
      Embedding = null;
      EmbeddingStatus = EmbeddingStatus.Pending;
  }
  ```

  Rename an item and its stale embedding is invalidated automatically.
- **Requests never bind to entities.** Request DTOs are separate types, so a client physically cannot post `userId`, `id` or `embedding` — **mass-assignment protection made structural rather than remembered.**

### **Trade-offs accepted**

- **No compiler-enforced layer boundary.** A handler *could* reach into infrastructure directly; only review stops it. A four-project solution would have the compiler stop it.
- **Feature folders can drift** in their internal conventions without a shared skeleton to conform to.
- **A second deployable** consuming the same domain would need the domain extracted into its own project first.

### **When this decision should be revisited**

- More than one deployable needs the same domain logic.
- Several teams own different parts of the codebase and need enforced boundaries.
- A piece of infrastructure genuinely becomes swappable.

**None of those is true of a single service whose defining feature is a PostgreSQL query.** The rule applied throughout: **abstract at the boundaries you actually have, not the ones a diagram suggests.**

---

## System Production Ready

### **Operability**

- **Health checks split by purpose.** Liveness checks nothing external, so a dependency outage cannot cause restart storms; readiness checks dependencies and returns 200 for Degraded, because an instance without its cache can still serve traffic.
- **Correlation ID** on every request, in every log line and every error body.
- **Structured logging to Loki**, with request logging placed *outside* the exception handler so domain 404s are not logged as fabricated 500s.
- **Prometheus metrics + Grafana dashboards.**
- **Container healthchecks with `depends_on` conditions**, so services start in a valid order rather than crash-looping until their dependencies appear.
- **Memory limits declared per service** in Compose, so one container cannot starve the host.

### **Resilience**

- **Rate limiting**, with separate policies: 100/min standard, 10/min on auth, 20/min on uploads.
- **Graceful degradation** — search falls back to keyword-only when Ollama is down, and reports it via `vectorSearchUsed`.
- **Optimistic concurrency** → 409 instead of silently discarding an edit.
- **Migrations in a separate one-shot container**, never at API startup — so multiple replicas cannot race the same migration.
- **Cache TTL jitter**, so entries do not all expire together and stampede the database.
- **Cache-aside with single-flight protection** on item reads, list pages and search alike: concurrent misses on the same key produce one query, not N. A Redis outage degrades to computing every time rather than failing the request.
- **Version-stamped invalidation.** One `INCR` per write invalidates every cached shape for that user at once, so an updated item cannot remain readable through a different endpoint.
- **Compensating delete on the upload path** — if the metadata write fails after the object is stored, the object is removed rather than orphaned.
- **`CancellationToken` threaded through every asynchronous path**, so a disconnected client stops consuming database connections and inference capacity.

### **Contract quality**

- **ProblemDetails for every failure**, including the framework 401/403/404/405 that would otherwise return empty bodies.
- **Forwarded headers**, so rate limiting partitions by real client IP behind a proxy.
- **Strongly-typed configuration with validation at startup** — bad config fails fast rather than at first use.
- **Keyset pagination** on the item list: stable while rows are being inserted, and page 500 costs the same as page 1.

### **Security**

- **Ownership enforced by a database-level query filter** rather than per-endpoint checks, so a future endpoint cannot forget it.
- **404 rather than 403** for another user's resource, so the API is not an enumeration oracle.
- **Uploads identified by magic bytes**, stored under server-generated user-namespaced keys, in a **private bucket** served through short-lived presigned URLs.
- **Short-lived access tokens with single-use rotating refresh tokens**; password policy and brute-force lockout delegated to the identity provider.
- **No secrets in source**; the container runs as a **non-root user**.

### **Evolvability**

- **A/B assignment primitive** with sticky, salted, uniform bucketing.
- **OpenAPI constraints and limits derived from the validators**, so documentation cannot drift from enforcement.

---

## CI/CD

The pipeline lives in `.github/workflows/ci-cd.yml`. It runs on **every push to `main`**, **every pull request**, and **on demand** via *Actions → CI/CD → Run workflow*.

### **What each job does**

| Job | What it runs |
|---|---|
| **Build & test** | `dotnet format --verify-no-changes`, `dotnet build -c Release` (warnings are errors), unit tests with coverage, then integration tests that start **real** Postgres + pgvector, Redis, Minio and Keycloak containers through Testcontainers |
| **Security scan** | `dotnet list package --vulnerable` (fails the build), `--deprecated` (reported only), and a Trivy filesystem scan uploaded as SARIF to the repository's **Security** tab |
| **Build image** | Builds the API image. **Pull requests build but do not push** — a broken Dockerfile fails the PR without anything untrusted reaching the registry. On `main` the image is pushed to **GitHub Container Registry** with SBOM and provenance attestations, then scanned again as a published image |
| **Deploy** | Azure Container Apps, followed by a readiness smoke test against the deployed URL |

- **Build & test** and **Security scan** run in parallel; **Build image** waits for both; **Deploy** waits for the image.
- A full run takes roughly **20–25 minutes** cold and **12–15 minutes** with caches warm.

### **Decisions worth noting**

- **The .NET version comes from `global.json`**, not from the workflow — the runner cannot silently build against a different SDK than a developer machine.
- **Superseded runs are cancelled** on the same branch, so three quick commits do not run three full pipelines.
- **The default token is read-only.** Each job opts in to only the permissions it needs, so a compromised action cannot inherit write access to the whole repository.
- **Images are tagged with the full commit SHA**, so *"which code is running"* is always answerable.
- **Integration tests are genuinely executed**, not skipped behind a filter. GitHub-hosted Ubuntu runners have a Docker daemon, so the same containers the application uses are started — not in-memory substitutes.

### **The deploy job is expected to fail**

This is deliberate, and the brief allows it:

> *"Do not worry if the last stages of the pipeline fail during execution since they will need actual resources."*

The secrets and variables exist with **dummy values**, and the pipeline **reads them from repository settings** rather than hard-coding anything — that wiring is the part being demonstrated. Provisioning the real Azure resources would need infrastructure-as-code, which is listed under [Known Limitations](#known-limitations).

### **Repository settings the pipeline reads**

**Settings → Secrets and variables → Actions → Secrets**

| Secret | Dummy value |
|---|---|
| `AZURE_CLIENT_ID` | any GUID |
| `AZURE_TENANT_ID` | any GUID |
| `AZURE_SUBSCRIPTION_ID` | any GUID |

- `GITHUB_TOKEN` is provided automatically — **do not create it**.

**Settings → Secrets and variables → Actions → Variables**

| Variable | Dummy value |
|---|---|
| `APP_URL` | `https://shopping-list-api.example.com` |
| `CONTAINER_APP_NAME` | `shopping-list-api` |
| `AZURE_RESOURCE_GROUP` | `rg-shopping-list` |

**Settings → Environments** → create an environment named **`production`**; the deploy job targets it.

- **Azure login uses OIDC federated credentials**, not a stored client secret. A long-lived cloud credential in CI is one leaked log away from a compromised subscription; a federated token is minted per run and expires with it.

### **Running it yourself on a fork**

- Fork the repository, then add the six values above with any placeholders.
- Push any commit, or use *Actions → CI/CD → Run workflow*.
- Expect **Build & test**, **Security scan** and **Build image** green, and **Deploy** red.

---

## Viewing Endpoint Documentation in Scalar

- The operation description — including the generated **Limits** line for each request body — renders **under the endpoint title**, in the same place the search endpoint's explanation of pgvector and fusion appears.
- Scalar presents **two views**. Click the **sidebar toggle** (the panel icon at the top-left) to switch between the request-builder view and the reference/documentation view.
- In the reference view each endpoint shows, in order:

  **title → summary → description → parameters → schema**

- The machine-readable version of the same information is always at **<http://localhost:5080/openapi/v1.json>**, and it cannot go stale: constraints are **derived from the FluentValidation validators** at document-generation time, not typed by hand.

---

## Known Limitations

*This project was built to a fixed time budget. The scope below was deliberately deferred rather than partially implemented — a half-built feature is worse than a documented absent one.*

### **Data & persistence**

- **No outbox pattern for embedding generation.** Embeddings are queued through an in-process background channel. If the API restarts between item creation and embedding, that item is left unembedded until re-indexed. Chosen because the failure is **visible** (`embedding_status`) and recoverable.
- **No soft delete or audit log table.** Audit columns are present, but deletes are hard and there is no immutable change history.
- **No idempotency keys on writes.** A retried `POST` creates a duplicate item.
- **Single database instance**, no read replicas or backup automation.

### **Search & embeddings**

- **No re-embedding job.** The model name and dimension are recorded per row so a migration is possible, but a model upgrade currently requires manual intervention. **This is the first thing I would add.**
- **HNSW index tuning left at sensible defaults.** `m` and `ef_construction` set from documented guidance rather than benchmarked.
- **A single distance threshold cannot fully separate relevant from irrelevant on a corpus this small.** Measured on the nine-item demo data, "Strawberry jam" for the query *"something to put on toast"* sits at cosine distance 0.551 — **further away than "Dish soap" at 0.529 for the same query**. No threshold admits the jam and rejects the soap. The floor is therefore set for precision (0.48): fewer results, none of them nonsense, with recall recovered by the full-text branch. A larger corpus with richer descriptions separates far better; a cross-encoder re-ranker over the fused candidates would fix it properly, and is the next thing I would build for relevance.
- **No search result highlighting.** `ts_headline` would give users context for why a result matched.
- **Document embeddings are not batched.** Bulk import would benefit; single-item creation does not.

### **Caching**

- **Invalidation is whole-user, not per-key.** A write bumps one version stamp, which makes every cached entry for that user unreachable at once — correct, and immediate, but blunter than it needs to be: adding an item invalidates that user's search results too, including pages the change could not have affected. Tag-based invalidation would evict only what actually changed. The cost of the current design is recomputation, never staleness.
- **The cache key does not include search configuration.** Changing a value such as `MaxVectorDistance` is not reflected until the TTL expires — flush Redis after a config change.
- **No cache warming.** Cold start means the first request per key hits the database.

### **Object storage**

- **Uploads are single-phase.** An upload that succeeds while the metadata write fails is compensated by deleting the object, but a reserve-then-confirm flow plus periodic reconciliation would close the remaining window.
- **No image processing** — no thumbnailing, resizing, format normalisation, or **EXIF stripping**. EXIF carries location data and should be stripped in any real deployment.
- **No server-side encryption or object versioning** configured on the bucket.

### **Authentication & authorization**

- **No token revocation or introspection.** Access tokens live one hour and refresh tokens rotate single-use, so a leaked refresh token is detectable — the legitimate holder's next refresh fails — but a specific access token cannot be invalidated within its hour. **The hour is a deliberate trade of exposure window against usability, not an oversight.**
- **`POST /auth/token` uses ROPC**, deprecated in OAuth 2.1, as a `curl`-able affordance. Authorization Code + PKCE is enabled on the same client and is the intended production flow.
- **Roles are defined but unused for feature-level authorization.** Ownership is the only authorization dimension this domain needs.
- **No identity brokering configured.** Keycloak can federate Okta, Azure AD or Google Workspace — a principal reason for choosing it — but no upstream provider is wired up.

### **Observability**

- **Metrics and logs, but no distributed tracing.** Full span-level timing across Postgres, Redis, Minio and Ollama is the natural next step.
- **Alert rules are not defined.** The dashboard surfaces error rate, latency and dependency health, but nothing routes an alert.
- **No SLO definitions** or error budget tracking.

### **Testing**

- **Coverage is targeted, not comprehensive.** The suite proves the things a reviewer would reasonably doubt — cross-user isolation on every endpoint, cache invalidation, cache isolation between ranking variants, graceful degradation when Ollama and Redis are unavailable, ranking correctness, upload validation. **It does not aim for a coverage percentage.**
- **No contract or snapshot tests on the OpenAPI document**, so an accidental breaking change would not fail the build.
- **No load or soak testing.**

### **CI/CD & deployment**

- **No infrastructure-as-code.** The deploy stage is templated against Azure Container Apps but there is no Bicep or Terraform to provision the resources it targets, so the deploy job cannot succeed as shipped. This is the reason it is expected to fail rather than a defect in the pipeline.
- **Single-environment pipeline.** No staging gate, no blue/green or canary rollout, no automated rollback.
- **Secrets come from GitHub Actions secrets**, not a managed secret store. Production would use Key Vault, Secrets Manager or equivalent, with workload identity rather than static credentials.
- **No CodeQL or Dependabot configuration.** Trivy and `dotnet list package --vulnerable` cover known-vulnerable dependencies, but there is no static analysis of the code itself and no automated dependency updates.

### **Experimentation**

- **Ranking variants are assigned, not managed.** Deliberately *not* built: experiment definition and storage, an assignment service, exposure event logging, and statistical significance analysis. **This implementation demonstrates the integration point and the correctness concerns — assignment stickiness and cache isolation — not the platform.**

### **Operational**

- **Single-instance assumptions in places.** The embedding background channel is in-process, so horizontal scaling would need the work distributed rather than duplicated. Rate limiting and caching are already Redis-backed and scale correctly.
- **No zero-downtime migration strategy.** Migrations run as a startup gate; expand/contract migrations would be needed for rolling deployments.

### **Client quirks worth knowing**

- **Scalar's multipart control** may create a form field named after your file instead of filling the declared `file` field. The API accepts the first non-empty file part, so both work.
- **Presigned image URLs expire after 15 minutes.** A saved URL returning 403 later is by design — re-read the item for a fresh one.
- **`docker compose down -v` invalidates every token**, because Keycloak re-imports the realm with new signing keys.

---

## If I Had More Time

*In priority order.*

1. **Re-embedding background job** — the only current gap that blocks a routine operational task, upgrading the embedding model.
2. **Distributed tracing via OpenTelemetry** — highest observability return; makes the existing logs and metrics far more useful together.
3. **Two-phase upload with orphan reconciliation** — closes the remaining consistency gap in the storage path.
4. **Tag-based cache invalidation** — removes the bounded staleness window on search results.
5. **Transactional outbox for embedding generation** — makes the write path durable across restarts.
6. **Contract tests on the OpenAPI document** — cheap, and prevents silent API breakage.
7. **Alert rules and SLOs** — turns the dashboard from observability into operability.
8. **Infrastructure-as-code for the deploy target** — makes the pipeline genuinely runnable.
