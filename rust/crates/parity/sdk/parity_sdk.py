#!/usr/bin/env python3
"""Official-SDK parity check (Python `azure-cosmos`).

This drives a *running* Rust emulator with the real Azure Cosmos DB Python SDK,
proving that an official client can speak to the port unchanged. It is an
**opt-in** layer: it requires network access to install the SDK and is NOT run by
`cargo test`.

Usage:
    # 1. Start the emulator (foreground), noting the key it prints:
    cargo run -p cosmos-cli -- start --key <master-key>
    # 2. In another shell:
    pip install azure-cosmos
    python3 crates/parity/sdk/parity_sdk.py \
        --endpoint http://localhost:8081 --key <master-key>

The default endpoint/key match the emulator's well-known development defaults.
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
    args = parser.parse_args()

    try:
        from azure.cosmos import CosmosClient, PartitionKey
    except ImportError:
        print("azure-cosmos is not installed. Run: pip install azure-cosmos", file=sys.stderr)
        return 2

    client = CosmosClient(args.endpoint, credential=args.key)

    db_id = f"db-{uuid.uuid4().hex}"
    coll_id = f"coll-{uuid.uuid4().hex}"

    db = client.create_database(db_id)
    print(f"[ok] created database {db_id}")

    container = db.create_container(id=coll_id, partition_key=PartitionKey(path="/partitionKey"))
    print(f"[ok] created container {coll_id}")

    doc_id = uuid.uuid4().hex
    created = container.create_item({"id": doc_id, "partitionKey": "tenant-1", "value": "created"})
    assert created["value"] == "created"
    print(f"[ok] created document {doc_id}")

    read = container.read_item(item=doc_id, partition_key="tenant-1")
    assert read["value"] == "created"
    print("[ok] read document")

    read["value"] = "updated"
    replaced = container.replace_item(item=doc_id, body=read)
    assert replaced["value"] == "updated"
    print("[ok] replaced document")

    container.delete_item(item=doc_id, partition_key="tenant-1")
    print("[ok] deleted document")

    client.delete_database(db_id)
    print("[ok] deleted database")

    print("PARITY_SDK_PYTHON_OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
