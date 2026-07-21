#!/usr/bin/env node
/*
 * Official-SDK parity check (Node `@azure/cosmos`).
 *
 * Drives a *running* Rust emulator with the real Azure Cosmos DB JavaScript SDK.
 * Opt-in layer: requires network access to `npm install @azure/cosmos` and is
 * NOT run by `cargo test`.
 *
 * Usage:
 *   cargo run -p cosmos-cli -- start --key <master-key>
 *   npm install @azure/cosmos
 *   node crates/parity/sdk/parity_sdk.js --endpoint http://localhost:8081 --key <master-key>
 */
"use strict";

const DEFAULT_KEY =
  "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

function arg(name, fallback) {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

async function main() {
  let CosmosClient;
  try {
    ({ CosmosClient } = require("@azure/cosmos"));
  } catch (e) {
    console.error("@azure/cosmos is not installed. Run: npm install @azure/cosmos");
    process.exit(2);
  }

  const endpoint = arg("--endpoint", "http://localhost:8081");
  const key = arg("--key", DEFAULT_KEY);
  const client = new CosmosClient({ endpoint, key });

  const dbId = `db-${Date.now().toString(16)}`;
  const collId = `coll-${Date.now().toString(16)}`;

  const { database } = await client.databases.create({ id: dbId });
  console.log(`[ok] created database ${dbId}`);

  const { container } = await database.containers.create({
    id: collId,
    partitionKey: { paths: ["/partitionKey"] },
  });
  console.log(`[ok] created container ${collId}`);

  const docId = Math.random().toString(16).slice(2);
  const { resource: created } = await container.items.create({
    id: docId,
    partitionKey: "tenant-1",
    value: "created",
  });
  if (created.value !== "created") throw new Error("create mismatch");
  console.log(`[ok] created document ${docId}`);

  const { resource: read } = await container.item(docId, "tenant-1").read();
  if (read.value !== "created") throw new Error("read mismatch");
  console.log("[ok] read document");

  read.value = "updated";
  const { resource: replaced } = await container.item(docId, "tenant-1").replace(read);
  if (replaced.value !== "updated") throw new Error("replace mismatch");
  console.log("[ok] replaced document");

  await container.item(docId, "tenant-1").delete();
  console.log("[ok] deleted document");

  await database.delete();
  console.log("[ok] deleted database");

  console.log("PARITY_SDK_NODE_OK");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
