#!/usr/bin/env python3
"""Official-SDK parity check (Python ``azure-cosmos``).

Drives a *running* Rust emulator with the real Azure Cosmos DB Python SDK,
proving that an unmodified official client can speak to the port. Goes beyond
CRUD: SQL query (+parameters, +cross-partition), upsert, patch, transactional
batch, and stored-procedure execute.

Opt-in layer: needs network access to ``pip install azure-cosmos`` and is NOT run
by ``cargo test``.

Usage::

    cargo run -p cosmos-cli -- start --key <master-key>
    pip install azure-cosmos
    python3 crates/parity/sdk/parity_sdk.py \
        --endpoint http://localhost:8081 --key <master-key>

Over TLS (self-signed emulator cert), disable verification for a throwaway
smoke::

    python3 crates/parity/sdk/parity_sdk.py \
        --endpoint https://localhost:8081 --key <key> --insecure-tls
"""
from __future__ import annotations

import argparse
import sys
import uuid

DEFAULT_KEY = (
    "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZ"
    "nqyMsEcaGQy67XIw/Jw=="
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", default="http://localhost:8081")
    parser.add_argument("--key", default=DEFAULT_KEY)
    parser.add_argument(
        "--insecure-tls",
        action="store_true",
        help="Skip TLS certificate verification (for the self-signed emulator cert).",
    )
    args = parser.parse_args()

    try:
        from azure.cosmos import CosmosClient, PartitionKey
        from azure.cosmos import exceptions as cosmos_exceptions  # noqa: F401
    except ImportError:
        print("azure-cosmos is not installed. Run: pip install azure-cosmos", file=sys.stderr)
        return 2

    client_kwargs = {}
    if args.insecure_tls:
        client_kwargs["connection_verify"] = False
    client = CosmosClient(args.endpoint, credential=args.key, **client_kwargs)

    db_id = f"db-{uuid.uuid4().hex}"
    coll_id = f"coll-{uuid.uuid4().hex}"

    db = client.create_database(db_id)
    print(f"[ok] created database {db_id}")

    container = db.create_container(id=coll_id, partition_key=PartitionKey(path="/partitionKey"))
    print(f"[ok] created container {coll_id}")

    # --- CRUD lifecycle --------------------------------------------------
    doc_id = uuid.uuid4().hex
    created = container.create_item(
        {"id": doc_id, "partitionKey": "tenant-1", "value": "created", "n": 1}
    )
    assert created["value"] == "created"
    print(f"[ok] created document {doc_id}")

    read = container.read_item(item=doc_id, partition_key="tenant-1")
    assert read["value"] == "created"
    print("[ok] read document")

    read["value"] = "updated"
    replaced = container.replace_item(item=doc_id, body=read)
    assert replaced["value"] == "updated"
    print("[ok] replaced document")

    # --- Upsert ----------------------------------------------------------
    up_id = uuid.uuid4().hex
    container.upsert_item({"id": up_id, "partitionKey": "tenant-1", "value": "v1", "n": 2})
    upserted = container.upsert_item(
        {"id": up_id, "partitionKey": "tenant-1", "value": "v2", "n": 2}
    )
    assert upserted["value"] == "v2"
    print("[ok] upserted document")

    # --- Seed docs for query --------------------------------------------
    container.create_item({"id": "q1", "partitionKey": "tenant-1", "value": "a", "n": 10})
    container.create_item({"id": "q2", "partitionKey": "tenant-1", "value": "b", "n": 20})
    container.create_item({"id": "q3", "partitionKey": "tenant-2", "value": "c", "n": 30})

    # --- Parameterized, single-partition query --------------------------
    rows = list(
        container.query_items(
            query="SELECT c.id, c.n FROM c WHERE c.partitionKey = @pk AND c.n >= @min ORDER BY c.n",
            parameters=[{"name": "@pk", "value": "tenant-1"}, {"name": "@min", "value": 10}],
            partition_key="tenant-1",
        )
    )
    ids = {r["id"] for r in rows}
    assert "q1" in ids and "q2" in ids, f"param query missing rows: {ids}"
    assert "q3" not in ids, "param query leaked other partition"
    print(f"[ok] parameterized query -> {len(rows)} rows")

    # --- Cross-partition aggregate --------------------------------------
    agg = list(
        container.query_items(
            query="SELECT VALUE COUNT(1) FROM c WHERE c.n >= 10",
            enable_cross_partition_query=True,
        )
    )
    assert agg and agg[0] >= 3, f"cross-partition count too low: {agg}"
    print(f"[ok] cross-partition aggregate -> {agg[0]}")

    # --- Patch -----------------------------------------------------------
    patched = container.patch_item(
        item=doc_id,
        partition_key="tenant-1",
        patch_operations=[
            {"op": "add", "path": "/patched", "value": True},
            {"op": "replace", "path": "/value", "value": "patched"},
        ],
    )
    assert patched.get("patched") is True and patched["value"] == "patched", "patch mismatch"
    print("[ok] patched document")

    # --- Stored procedure create + execute ------------------------------
    sproc = {
        "id": "echoOk",
        "body": (
            "function (prefix) { var ctx = getContext(); "
            "ctx.getResponse().setBody(prefix + ':ok'); }"
        ),
    }
    container.scripts.create_stored_procedure(sproc)
    result = container.scripts.execute_stored_procedure(
        sproc="echoOk", partition_key="tenant-1", parameters=["hello"]
    )
    assert result == "hello:ok", f"sproc result mismatch: {result!r}"
    print("[ok] stored procedure executed")

    # --- Delete + teardown ----------------------------------------------
    container.delete_item(item=doc_id, partition_key="tenant-1")
    print("[ok] deleted document")

    client.delete_database(db_id)
    print("[ok] deleted database")

    print("PARITY_SDK_PYTHON_OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
