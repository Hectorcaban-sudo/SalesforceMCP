# ChromaDB Dashboard

A modern web UI for browsing and managing a ChromaDB instance, inspired by [BlackyDrum/chromadb-ui](https://github.com/BlackyDrum/chromadb-ui) but built with a **Python FastAPI backend** that uses the official `chromadb` Python library, plus a **React + Vite + Tailwind** frontend.

## Why a Python backend?

The reference Vue project talks to ChromaDB directly from the browser, which means dealing with CORS and exposing your Chroma server to the public. This implementation keeps Chroma on a private network and exposes a clean REST API the dashboard consumes. It's a more typical production shape.

## Features

- **Overview dashboard** with collection counts, total records, and dimension summaries
- **Collections sidebar** with create, rename, clone, and delete
- **Records table** with pagination, column-per-metadata-key, and row selection
- **Bulk actions**: delete, metadata patch (merge), selected-row CSV export
- **Add / upsert** single records or bulk JSON arrays (with optional explicit embeddings)
- **Semantic text query** using the collection's embedding function
- **Vector (kNN) query** with raw embedding JSON
- **Metadata filter builder** (`where` and `where_document`)
- **Inline editor** for document, metadata, and embedding
- **Vector viewer** with histogram visualization for large embeddings
- **Collection metrics** with document stats, metadata coverage, sampled embedding stats, and a quality audit
- **Activity log** for recent operations
- **CSV export** of the current view or selection
- **Connection settings** for host / tenant / database, plus a local persistent-path mode

## Quick start (Docker, recommended)

```bash
docker compose up -d --build
python seed.py            # optional: load sample data
```

Open <http://localhost:8090>.

## Local development

Terminal 1 — start ChromaDB:

```bash
docker compose up -d chromadb
```

Terminal 2 — backend:

```bash
cd backend
pip install -r requirements.txt
uvicorn main:app --reload --port 8765
```

Terminal 3 — frontend:

```bash
cd frontend
npm install
npm run dev
```

Open <http://localhost:5173>. Vite proxies `/api` to the backend on `:8765`.

## Architecture

```
┌───────────┐  HTTP  ┌──────────────────┐  chromadb client  ┌──────────┐
│  Browser  ├───────►│  FastAPI backend ├──────────────────►│ ChromaDB │
│  (React)  │ /api/* │   (Python)       │                   │  server  │
└───────────┘        └──────────────────┘                   └──────────┘
```

- **Frontend**: `frontend/` — Vite + React 18 + Tailwind + Lucide icons. Dark IDE-ish aesthetic with JetBrains Mono for IDs and Instrument Serif as a display accent.
- **Backend**: `backend/main.py` — FastAPI exposing REST endpoints around the `chromadb` Python client (`HttpClient` or `PersistentClient`).
- **Seed**: `seed.py` — populates three demo collections so the UI has something to render on first open.

## API endpoints (backend)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/health` | Connection health + current config |
| POST | `/api/connect` | Reconnect to a different Chroma instance |
| GET | `/api/overview` | Dashboard summary |
| GET | `/api/activity` | Recent operation log |
| GET | `/api/collections` | List all collections |
| POST | `/api/collections` | Create a collection |
| GET | `/api/collections/{name}` | Collection details |
| PATCH | `/api/collections/{name}` | Rename / update metadata |
| DELETE | `/api/collections/{name}` | Delete collection |
| POST | `/api/collections/{name}/clone` | Clone collection (records + metadata) |
| GET | `/api/collections/{name}/records` | List records (paginated, filtered) |
| GET | `/api/collections/{name}/records/{id}` | Get one record |
| POST | `/api/collections/{name}/records` | Add records |
| POST | `/api/collections/{name}/upsert` | Upsert records |
| PATCH | `/api/collections/{name}/records/{id}` | Update one record |
| POST | `/api/collections/{name}/records/bulk-delete` | Bulk delete by IDs or `where` |
| POST | `/api/collections/{name}/records/bulk-metadata` | Patch metadata on many records |
| POST | `/api/collections/{name}/query/text` | Semantic text query |
| POST | `/api/collections/{name}/query/vector` | Vector kNN query |
| GET | `/api/collections/{name}/metrics` | Sampled stats + quality audit |
| GET | `/api/collections/{name}/export.csv` | CSV export |

## License

MIT.
