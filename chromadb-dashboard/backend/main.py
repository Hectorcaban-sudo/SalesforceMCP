"""
ChromaDB Dashboard - FastAPI Backend
====================================
A REST API that wraps the chromadb Python client and exposes endpoints
for browsing collections, querying, inspecting records, and editing
documents/metadata/embeddings.
"""
from __future__ import annotations

import csv
import io
import json
import logging
import os
import statistics
import time
from collections import Counter
from contextlib import asynccontextmanager
from typing import Any, Optional

import chromadb
from chromadb.config import Settings
from fastapi import FastAPI, HTTPException, Query, Response
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("chromadb-dashboard")


# ---------------------------------------------------------------------------
# Connection management
# ---------------------------------------------------------------------------
class ConnectionConfig(BaseModel):
    host: str = Field(default="localhost")
    port: int = Field(default=8000)
    ssl: bool = Field(default=False)
    tenant: str = Field(default="default_tenant")
    database: str = Field(default="default_database")
    # When set, use a persistent local client instead of HTTP
    persist_path: Optional[str] = None


# In-memory connection state. Single-user dashboard, so global is fine.
_state: dict[str, Any] = {
    "config": ConnectionConfig(),
    "client": None,
    "activity": [],  # ring buffer of recent actions
}


def log_activity(action: str, target: str = "", detail: str = "") -> None:
    """Append an action to the activity ring buffer (last 200)."""
    _state["activity"].insert(0, {
        "ts": time.time(),
        "action": action,
        "target": target,
        "detail": detail,
    })
    _state["activity"] = _state["activity"][:200]


def get_client():
    if _state["client"] is None:
        connect(_state["config"])
    return _state["client"]


def connect(cfg: ConnectionConfig):
    """(Re)create the chromadb client from a config object."""
    if cfg.persist_path:
        client = chromadb.PersistentClient(
            path=cfg.persist_path,
            settings=Settings(anonymized_telemetry=False),
        )
    else:
        client = chromadb.HttpClient(
            host=cfg.host,
            port=cfg.port,
            ssl=cfg.ssl,
            tenant=cfg.tenant,
            database=cfg.database,
            settings=Settings(anonymized_telemetry=False),
        )
    # Smoke-test the connection
    client.heartbeat()
    _state["client"] = client
    _state["config"] = cfg
    log_activity("connect", f"{cfg.host}:{cfg.port}", f"tenant={cfg.tenant} db={cfg.database}")
    return client


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Try a default connection at startup; don't fail if it isn't reachable.
    host = os.environ.get("CHROMA_HOST", "localhost")
    port = int(os.environ.get("CHROMA_PORT", "8000"))
    persist = os.environ.get("CHROMA_PERSIST_PATH")
    try:
        cfg = ConnectionConfig(
            host=host, port=port,
            persist_path=persist,
        )
        connect(cfg)
        log.info(f"Connected to ChromaDB at startup: {cfg}")
    except Exception as e:
        log.warning(f"Initial connect failed (ok, user can connect via UI): {e}")
    yield


app = FastAPI(title="ChromaDB Dashboard API", version="1.0.0", lifespan=lifespan)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


# ---------------------------------------------------------------------------
# Pydantic models
# ---------------------------------------------------------------------------
class CreateCollectionBody(BaseModel):
    name: str
    metadata: Optional[dict[str, Any]] = None


class UpdateCollectionBody(BaseModel):
    new_name: Optional[str] = None
    new_metadata: Optional[dict[str, Any]] = None


class CloneCollectionBody(BaseModel):
    new_name: str


class AddRecordsBody(BaseModel):
    ids: list[str]
    documents: Optional[list[Optional[str]]] = None
    metadatas: Optional[list[Optional[dict[str, Any]]]] = None
    embeddings: Optional[list[list[float]]] = None


class UpdateRecordBody(BaseModel):
    document: Optional[str] = None
    metadata: Optional[dict[str, Any]] = None
    embedding: Optional[list[float]] = None


class BulkDeleteBody(BaseModel):
    ids: Optional[list[str]] = None
    where: Optional[dict[str, Any]] = None


class BulkMetadataPatchBody(BaseModel):
    ids: list[str]
    metadata_patch: dict[str, Any]


class TextQueryBody(BaseModel):
    query_texts: list[str]
    n_results: int = 10
    where: Optional[dict[str, Any]] = None
    where_document: Optional[dict[str, Any]] = None


class VectorQueryBody(BaseModel):
    query_embeddings: list[list[float]]
    n_results: int = 10
    where: Optional[dict[str, Any]] = None
    where_document: Optional[dict[str, Any]] = None


# ---------------------------------------------------------------------------
# Connection endpoints
# ---------------------------------------------------------------------------
@app.get("/api/health")
def health():
    try:
        c = get_client()
        c.heartbeat()
        return {"status": "ok", "config": _state["config"].model_dump()}
    except Exception as e:
        return {"status": "error", "error": str(e), "config": _state["config"].model_dump()}


@app.post("/api/connect")
def api_connect(cfg: ConnectionConfig):
    try:
        connect(cfg)
        return {"status": "ok", "config": cfg.model_dump()}
    except Exception as e:
        raise HTTPException(status_code=400, detail=f"Connection failed: {e}")


@app.get("/api/config")
def get_config():
    return _state["config"].model_dump()


# ---------------------------------------------------------------------------
# Collection endpoints
# ---------------------------------------------------------------------------
@app.get("/api/collections")
def list_collections():
    client = get_client()
    cols = client.list_collections()
    out = []
    for c in cols:
        try:
            col = client.get_collection(name=c.name) if isinstance(c, str) else c
            out.append({
                "name": col.name,
                "id": str(col.id) if hasattr(col, "id") else None,
                "metadata": col.metadata or {},
                "count": col.count(),
            })
        except Exception as e:
            log.warning(f"Failed reading collection {c}: {e}")
    return {"collections": out}


@app.post("/api/collections")
def create_collection(body: CreateCollectionBody):
    client = get_client()
    try:
        col = client.create_collection(name=body.name, metadata=body.metadata or None)
        log_activity("create_collection", body.name)
        return {"name": col.name, "metadata": col.metadata or {}, "count": 0}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.get("/api/collections/{name}")
def get_collection_info(name: str):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        return {
            "name": col.name,
            "id": str(col.id) if hasattr(col, "id") else None,
            "metadata": col.metadata or {},
            "count": col.count(),
        }
    except Exception as e:
        raise HTTPException(status_code=404, detail=str(e))


@app.patch("/api/collections/{name}")
def update_collection(name: str, body: UpdateCollectionBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        col.modify(name=body.new_name, metadata=body.new_metadata)
        log_activity("update_collection", name,
                     f"renamed→{body.new_name}" if body.new_name else "metadata updated")
        return {"status": "ok"}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.delete("/api/collections/{name}")
def delete_collection(name: str):
    client = get_client()
    try:
        client.delete_collection(name=name)
        log_activity("delete_collection", name)
        return {"status": "ok"}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/api/collections/{name}/clone")
def clone_collection(name: str, body: CloneCollectionBody):
    client = get_client()
    try:
        src = client.get_collection(name=name)
        new = client.create_collection(name=body.new_name, metadata=src.metadata or None)
        # Pull everything from src and push into new in batches.
        data = src.get(include=["documents", "metadatas", "embeddings"])
        ids = list(data.get("ids") or [])
        if ids:
            docs_all = _coerce_list(data.get("documents"), len(ids))
            metas_all = _coerce_list(data.get("metadatas"), len(ids))
            embs_all = _coerce_list(data.get("embeddings"), len(ids))
            batch = 500
            for i in range(0, len(ids), batch):
                new.add(
                    ids=ids[i:i + batch],
                    documents=docs_all[i:i + batch],
                    metadatas=metas_all[i:i + batch],
                    embeddings=embs_all[i:i + batch],
                )
        log_activity("clone_collection", name, f"→{body.new_name} ({len(ids)} records)")
        return {"status": "ok", "name": body.new_name, "count": len(ids)}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


# ---------------------------------------------------------------------------
# Record endpoints
# ---------------------------------------------------------------------------
def _safe_serialize_embedding(emb):
    """Convert numpy arrays / lists into JSON-safe lists."""
    if emb is None:
        return None
    try:
        return [float(x) for x in emb]
    except Exception:
        return None


def _coerce_list(value, fallback_len: int = 0):
    """Return ``value`` as a plain list, or ``[None] * fallback_len`` if None.

    chromadb returns numpy arrays for some fields, so ``x or default`` raises
    "truth value of an array is ambiguous". This helper avoids that.
    """
    if value is None:
        return [None] * fallback_len
    # numpy arrays and lists both support len()/iteration; convert to list
    try:
        return list(value)
    except TypeError:
        return [None] * fallback_len


@app.get("/api/collections/{name}/records")
def list_records(
    name: str,
    limit: int = Query(50, ge=1, le=500),
    offset: int = Query(0, ge=0),
    include_embeddings: bool = Query(False),
    where: Optional[str] = Query(None, description="JSON-encoded where filter"),
    where_document: Optional[str] = Query(None, description="JSON-encoded where_document filter"),
):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        include = ["documents", "metadatas"]
        if include_embeddings:
            include.append("embeddings")

        where_dict = json.loads(where) if where else None
        where_doc_dict = json.loads(where_document) if where_document else None

        data = col.get(
            limit=limit,
            offset=offset,
            include=include,
            where=where_dict,
            where_document=where_doc_dict,
        )
        ids = list(data.get("ids") or [])
        docs = _coerce_list(data.get("documents"), len(ids))
        metas = _coerce_list(data.get("metadatas"), len(ids))
        embs = _coerce_list(data.get("embeddings"), len(ids))

        records = [
            {
                "id": ids[i],
                "document": docs[i] if i < len(docs) else None,
                "metadata": metas[i] if i < len(metas) else None,
                "embedding": _safe_serialize_embedding(embs[i]) if include_embeddings and i < len(embs) else None,
            }
            for i in range(len(ids))
        ]
        return {"records": records, "total": col.count(), "limit": limit, "offset": offset}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.get("/api/collections/{name}/records/{record_id}")
def get_record(name: str, record_id: str):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        data = col.get(ids=[record_id], include=["documents", "metadatas", "embeddings"])
        if not data.get("ids"):
            raise HTTPException(status_code=404, detail="Record not found")
        docs = _coerce_list(data.get("documents"), 1)
        metas = _coerce_list(data.get("metadatas"), 1)
        embs = _coerce_list(data.get("embeddings"), 1)
        return {
            "id": data["ids"][0],
            "document": docs[0] if docs else None,
            "metadata": metas[0] if metas else None,
            "embedding": _safe_serialize_embedding(embs[0] if embs else None),
        }
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/api/collections/{name}/records")
def add_records(name: str, body: AddRecordsBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        col.add(
            ids=body.ids,
            documents=body.documents,
            metadatas=body.metadatas,
            embeddings=body.embeddings,
        )
        log_activity("add_records", name, f"{len(body.ids)} records")
        return {"status": "ok", "count": len(body.ids)}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/api/collections/{name}/upsert")
def upsert_records(name: str, body: AddRecordsBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        col.upsert(
            ids=body.ids,
            documents=body.documents,
            metadatas=body.metadatas,
            embeddings=body.embeddings,
        )
        log_activity("upsert_records", name, f"{len(body.ids)} records")
        return {"status": "ok", "count": len(body.ids)}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.patch("/api/collections/{name}/records/{record_id}")
def update_record(name: str, record_id: str, body: UpdateRecordBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        kwargs: dict[str, Any] = {"ids": [record_id]}
        if body.document is not None:
            kwargs["documents"] = [body.document]
        if body.metadata is not None:
            kwargs["metadatas"] = [body.metadata]
        if body.embedding is not None:
            kwargs["embeddings"] = [body.embedding]
        col.update(**kwargs)
        log_activity("update_record", name, record_id)
        return {"status": "ok"}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/api/collections/{name}/records/bulk-delete")
def bulk_delete(name: str, body: BulkDeleteBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        col.delete(ids=body.ids, where=body.where)
        n = len(body.ids) if body.ids else 0
        log_activity("bulk_delete", name, f"{n} records" if n else f"where={body.where}")
        return {"status": "ok"}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/api/collections/{name}/records/bulk-metadata")
def bulk_metadata_patch(name: str, body: BulkMetadataPatchBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        # Fetch existing metadata so we can merge rather than overwrite
        existing = col.get(ids=body.ids, include=["metadatas"])
        existing_metas = _coerce_list(existing.get("metadatas"), len(body.ids))
        new_metas = []
        for m in existing_metas:
            merged = dict(m or {})
            merged.update(body.metadata_patch)
            new_metas.append(merged)
        col.update(ids=body.ids, metadatas=new_metas)
        log_activity("bulk_metadata_patch", name, f"{len(body.ids)} records")
        return {"status": "ok"}
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


# ---------------------------------------------------------------------------
# Query endpoints
# ---------------------------------------------------------------------------
@app.post("/api/collections/{name}/query/text")
def query_text(name: str, body: TextQueryBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        res = col.query(
            query_texts=body.query_texts,
            n_results=body.n_results,
            where=body.where,
            where_document=body.where_document,
            include=["documents", "metadatas", "distances"],
        )
        log_activity("query_text", name, f"n={body.n_results}")
        return _flatten_query_results(res)
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/api/collections/{name}/query/vector")
def query_vector(name: str, body: VectorQueryBody):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        res = col.query(
            query_embeddings=body.query_embeddings,
            n_results=body.n_results,
            where=body.where,
            where_document=body.where_document,
            include=["documents", "metadatas", "distances"],
        )
        log_activity("query_vector", name, f"n={body.n_results}")
        return _flatten_query_results(res)
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


def _flatten_query_results(res: dict) -> dict:
    """Convert ChromaDB's per-query list-of-lists into a flat hits list."""
    all_hits = []
    ids_lists = _coerce_list(res.get("ids"), 0)
    docs_lists = _coerce_list(res.get("documents"), 0)
    metas_lists = _coerce_list(res.get("metadatas"), 0)
    dists_lists = _coerce_list(res.get("distances"), 0)
    for qi, ids in enumerate(ids_lists):
        docs = _coerce_list(docs_lists[qi], 0) if qi < len(docs_lists) else []
        metas = _coerce_list(metas_lists[qi], 0) if qi < len(metas_lists) else []
        dists = _coerce_list(dists_lists[qi], 0) if qi < len(dists_lists) else []
        for i, rid in enumerate(ids):
            all_hits.append({
                "query_index": qi,
                "id": rid,
                "document": docs[i] if i < len(docs) else None,
                "metadata": metas[i] if i < len(metas) else None,
                "distance": float(dists[i]) if i < len(dists) and dists[i] is not None else None,
            })
    return {"hits": all_hits, "num_queries": len(ids_lists)}


# ---------------------------------------------------------------------------
# Metrics & audit
# ---------------------------------------------------------------------------
@app.get("/api/collections/{name}/metrics")
def collection_metrics(name: str, sample: int = Query(200, ge=10, le=2000)):
    """Compute summary stats over a sample of records."""
    client = get_client()
    try:
        col = client.get_collection(name=name)
        total = col.count()
        sample_size = min(sample, total) if total else 0
        if sample_size == 0:
            return {
                "total": 0, "sample_size": 0,
                "document_stats": {}, "metadata_keys": {}, "embedding_stats": {},
                "audit": [],
            }

        data = col.get(limit=sample_size, include=["documents", "metadatas", "embeddings"])
        docs = _coerce_list(data.get("documents"), sample_size)
        metas = _coerce_list(data.get("metadatas"), sample_size)
        embs = _coerce_list(data.get("embeddings"), sample_size)

        # Document stats
        doc_lengths = [len(d) for d in docs if isinstance(d, str)]
        document_stats = {
            "with_documents": sum(1 for d in docs if d),
            "without_documents": sum(1 for d in docs if not d),
            "min_chars": min(doc_lengths) if doc_lengths else 0,
            "max_chars": max(doc_lengths) if doc_lengths else 0,
            "avg_chars": round(statistics.mean(doc_lengths), 1) if doc_lengths else 0,
            "median_chars": int(statistics.median(doc_lengths)) if doc_lengths else 0,
        }

        # Metadata keys
        key_counter: Counter = Counter()
        type_counter: dict[str, Counter] = {}
        for m in metas:
            if isinstance(m, dict):
                for k, v in m.items():
                    key_counter[k] += 1
                    type_counter.setdefault(k, Counter())[type(v).__name__] += 1
        metadata_keys = {
            k: {"count": c, "coverage_pct": round(100 * c / sample_size, 1),
                "types": dict(type_counter.get(k, {}))}
            for k, c in key_counter.most_common()
        }

        # Embedding stats
        embedding_stats: dict[str, Any] = {}
        valid_embs = [e for e in embs if e is not None and len(e) > 0]
        if valid_embs:
            dims = {len(e) for e in valid_embs}
            flat = [float(x) for e in valid_embs for x in e]
            embedding_stats = {
                "with_embeddings": len(valid_embs),
                "dimensions": list(dims),
                "min_value": min(flat),
                "max_value": max(flat),
                "mean_value": round(statistics.mean(flat), 6),
                "stdev_value": round(statistics.stdev(flat), 6) if len(flat) > 1 else 0.0,
            }
        else:
            embedding_stats = {"with_embeddings": 0, "dimensions": []}

        # Quality audit
        audit = []
        if document_stats["without_documents"]:
            audit.append({
                "level": "warn",
                "title": "Empty documents",
                "detail": f"{document_stats['without_documents']} of {sample_size} sampled records have no document text.",
            })
        if embedding_stats.get("with_embeddings", 0) < sample_size:
            audit.append({
                "level": "warn",
                "title": "Missing embeddings",
                "detail": f"{sample_size - embedding_stats.get('with_embeddings', 0)} of {sample_size} sampled records have no embedding.",
            })
        if isinstance(embedding_stats.get("dimensions"), list) and len(embedding_stats["dimensions"]) > 1:
            audit.append({
                "level": "error",
                "title": "Inconsistent embedding dimensions",
                "detail": f"Found dimensions {embedding_stats['dimensions']} in the same collection.",
            })
        sparse_meta_keys = [k for k, info in metadata_keys.items() if info["coverage_pct"] < 25]
        if sparse_meta_keys:
            audit.append({
                "level": "info",
                "title": "Sparse metadata keys",
                "detail": f"Keys with <25% coverage: {', '.join(sparse_meta_keys[:8])}",
            })
        if not audit:
            audit.append({"level": "ok", "title": "No issues detected", "detail": "Sample looks healthy."})

        return {
            "total": total,
            "sample_size": sample_size,
            "document_stats": document_stats,
            "metadata_keys": metadata_keys,
            "embedding_stats": embedding_stats,
            "audit": audit,
        }
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


# ---------------------------------------------------------------------------
# Dashboard overview
# ---------------------------------------------------------------------------
@app.get("/api/overview")
def overview():
    client = get_client()
    try:
        cols = client.list_collections()
        col_info = []
        total_records = 0
        dim_set: set[int] = set()
        for c in cols:
            col = c if hasattr(c, "name") else client.get_collection(name=c)
            n = col.count()
            total_records += n
            # Try to detect dimension from one record
            dim = None
            if n > 0:
                try:
                    sample = col.get(limit=1, include=["embeddings"])
                    embs = _coerce_list(sample.get("embeddings"), 0)
                    if embs and embs[0] is not None and len(embs[0]) > 0:
                        dim = len(embs[0])
                        dim_set.add(dim)
                except Exception:
                    pass
            col_info.append({
                "name": col.name,
                "count": n,
                "dimension": dim,
                "metadata_keys": list((col.metadata or {}).keys()),
            })
        return {
            "collections_count": len(col_info),
            "total_records": total_records,
            "dimensions_seen": sorted(dim_set),
            "collections": col_info,
        }
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


# ---------------------------------------------------------------------------
# Activity log
# ---------------------------------------------------------------------------
@app.get("/api/activity")
def activity(limit: int = Query(50, ge=1, le=200)):
    return {"events": _state["activity"][:limit]}


# ---------------------------------------------------------------------------
# CSV export
# ---------------------------------------------------------------------------
@app.get("/api/collections/{name}/export.csv")
def export_csv(
    name: str,
    ids: Optional[str] = Query(None, description="Comma-separated record IDs; omit for full export"),
    include_embeddings: bool = Query(False),
    limit: int = Query(10000, ge=1, le=100000),
):
    client = get_client()
    try:
        col = client.get_collection(name=name)
        include = ["documents", "metadatas"]
        if include_embeddings:
            include.append("embeddings")
        id_list = [s for s in (ids.split(",") if ids else []) if s]
        if id_list:
            data = col.get(ids=id_list, include=include)
        else:
            data = col.get(limit=limit, include=include)
        rows_ids = list(data.get("ids") or [])
        docs = _coerce_list(data.get("documents"), len(rows_ids))
        metas = _coerce_list(data.get("metadatas"), len(rows_ids))
        embs = _coerce_list(data.get("embeddings"), len(rows_ids))

        # Union of metadata keys for stable header
        meta_keys: list[str] = []
        seen = set()
        for m in metas:
            if isinstance(m, dict):
                for k in m.keys():
                    if k not in seen:
                        seen.add(k)
                        meta_keys.append(k)

        buf = io.StringIO()
        writer = csv.writer(buf)
        header = ["id", "document"] + [f"meta.{k}" for k in meta_keys]
        if include_embeddings:
            header.append("embedding")
        writer.writerow(header)
        for i, rid in enumerate(rows_ids):
            row = [rid, docs[i] if i < len(docs) else ""]
            m = metas[i] if i < len(metas) else None
            for k in meta_keys:
                v = (m or {}).get(k, "")
                row.append(v if isinstance(v, (str, int, float, bool)) or v is None else json.dumps(v))
            if include_embeddings:
                e = embs[i] if i < len(embs) else None
                row.append(json.dumps(_safe_serialize_embedding(e)) if e is not None else "")
            writer.writerow(row)

        log_activity("export_csv", name, f"{len(rows_ids)} rows")
        return Response(
            content=buf.getvalue(),
            media_type="text/csv",
            headers={"Content-Disposition": f'attachment; filename="{name}.csv"'},
        )
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


if __name__ == "__main__":
    import uvicorn
    uvicorn.run("main:app", host="0.0.0.0", port=8765, reload=True)
