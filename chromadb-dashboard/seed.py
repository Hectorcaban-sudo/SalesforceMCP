"""
Seed a running ChromaDB instance with demo collections so you can immediately
see the dashboard with real data.

Usage:
    pip install chromadb
    python seed.py                       # localhost:8000
    CHROMA_HOST=... CHROMA_PORT=... python seed.py
"""
import os
import random
import chromadb

HOST = os.environ.get("CHROMA_HOST", "localhost")
PORT = int(os.environ.get("CHROMA_PORT", "8000"))

client = chromadb.HttpClient(host=HOST, port=PORT)
print(f"Connected to {HOST}:{PORT}")

# --- Collection 1: documentation ---
try: client.delete_collection("docs")
except Exception: pass
docs = client.create_collection("docs", metadata={"description": "Product docs", "owner": "platform"})

samples = [
    ("doc_001", "ChromaDB is an open-source embedding database for AI applications.", {"category": "intro", "version": 1}),
    ("doc_002", "Collections are the primary container in ChromaDB and hold records.", {"category": "concept", "version": 1}),
    ("doc_003", "Embeddings can be auto-generated or supplied as raw vectors.", {"category": "concept", "version": 2}),
    ("doc_004", "Use the where clause to filter by metadata when querying.", {"category": "api", "version": 1}),
    ("doc_005", "ChromaDB supports both persistent local mode and HTTP client mode.", {"category": "intro", "version": 2}),
    ("doc_006", "Bulk delete records using the delete method with a list of ids.", {"category": "api", "version": 1}),
    ("doc_007", "Metadata is stored as JSON and indexed for fast filtering.", {"category": "concept", "version": 2}),
    ("doc_008", "Run nearest-neighbor search with query_embeddings or query_texts.", {"category": "api", "version": 1}),
]
docs.add(
    ids=[s[0] for s in samples],
    documents=[s[1] for s in samples],
    metadatas=[s[2] for s in samples],
)
print(f"Seeded 'docs' with {len(samples)} records")

# --- Collection 2: products with raw vectors ---
try: client.delete_collection("products")
except Exception: pass
products = client.create_collection("products", metadata={"source": "demo"})

product_names = [
    ("prod_001", "wireless mechanical keyboard", "peripheral", 129.99, True),
    ("prod_002", "27-inch 4K monitor", "display", 449.00, True),
    ("prod_003", "ergonomic office chair", "furniture", 299.00, False),
    ("prod_004", "USB-C docking station", "peripheral", 199.00, True),
    ("prod_005", "noise-cancelling headphones", "audio", 349.99, True),
    ("prod_006", "standing desk converter", "furniture", 219.00, False),
    ("prod_007", "wireless trackball mouse", "peripheral", 89.99, True),
    ("prod_008", "1080p webcam", "peripheral", 79.99, False),
    ("prod_009", "studio microphone", "audio", 129.00, True),
    ("prod_010", "monitor arm", "furniture", 159.00, True),
]
random.seed(42)
# Use deterministic 32-dim "embeddings" so the metrics page has data
embeddings = [[random.gauss(0, 1) for _ in range(32)] for _ in product_names]
products.add(
    ids=[p[0] for p in product_names],
    documents=[p[1] for p in product_names],
    metadatas=[{"category": p[2], "price": p[3], "in_stock": p[4]} for p in product_names],
    embeddings=embeddings,
)
print(f"Seeded 'products' with {len(product_names)} records (32-d embeddings)")

# --- Collection 3: empty one for demoing edge cases ---
try: client.delete_collection("empty-collection")
except Exception: pass
client.create_collection("empty-collection")
print("Created empty 'empty-collection'")

print("\nDone. Open the dashboard at http://localhost:8090 (Docker) or http://localhost:5173 (dev).")
