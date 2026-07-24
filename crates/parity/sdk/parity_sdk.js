#!/usr/bin/env node
/*
 * Official-SDK parity check (Node `@azure/cosmos`).
 *
 * Drives a *running* Rust emulator with the real Azure Cosmos DB JavaScript SDK,
 * proving that an unmodified official client can speak to the port. Goes well
 * beyond CRUD: SQL query (+parameters, +cross-partition), upsert, patch,
 * transactional batch, and stored-procedure execute.
 *
 * Opt-in layer: requires network access to `npm install @azure/cosmos` and is
 * NOT run by `cargo test`.
 *
 * Usage:
 *   cargo run -p cosmos-cli -- start --key <master-key>
 *   npm install @azure/cosmos
 *   node crates/parity/sdk/parity_sdk.js --endpoint http://localhost:8081 --key <master-key>
 *
 * Over TLS (self-signed emulator cert), trust it first:
 *   NODE_EXTRA_CA_CERTS=<data-dir>/certs/localhost.pem \
 *     node crates/parity/sdk/parity_sdk.js --endpoint https://localhost:8081 --key <key>
 *   # or, for a throwaway smoke: NODE_TLS_REJECT_UNAUTHORIZED=0
 */
"use strict";

const DEFAULT_KEY =
  "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

function arg(name, fallback) {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

function assert(cond, msg) {
  if (!cond) throw new Error(msg);
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

  // --- CRUD lifecycle ---------------------------------------------------
  const docId = Math.random().toString(16).slice(2);
  const { resource: created } = await container.items.create({
    id: docId,
    partitionKey: "tenant-1",
    value: "created",
    n: 1,
  });
  assert(created.value === "created", "create mismatch");
  console.log(`[ok] created document ${docId}`);

  const { resource: read } = await container.item(docId, "tenant-1").read();
  assert(read.value === "created", "read mismatch");
  console.log("[ok] read document");

  read.value = "updated";
  const { resource: replaced } = await container.item(docId, "tenant-1").replace(read);
  assert(replaced.value === "updated", "replace mismatch");
  console.log("[ok] replaced document");

  // --- Upsert (insert then update via upsert) ---------------------------
  const upId = Math.random().toString(16).slice(2);
  await container.items.upsert({ id: upId, partitionKey: "tenant-1", value: "v1", n: 2 });
  const { resource: upserted } = await container.items.upsert({
    id: upId,
    partitionKey: "tenant-1",
    value: "v2",
    n: 2,
  });
  assert(upserted.value === "v2", "upsert mismatch");
  console.log("[ok] upserted document");

  // --- Seed a few more docs for query coverage --------------------------
  await container.items.create({ id: "q1", partitionKey: "tenant-1", value: "a", n: 10 });
  await container.items.create({ id: "q2", partitionKey: "tenant-1", value: "b", n: 20 });
  await container.items.create({ id: "q3", partitionKey: "tenant-2", value: "c", n: 30 });

  // --- Parameterized, single-partition query ----------------------------
  {
    const { resources } = await container.items
      .query(
        {
          query:
            "SELECT c.id, c.n FROM c WHERE c.partitionKey = @pk AND c.n >= @min ORDER BY c.n",
          parameters: [
            { name: "@pk", value: "tenant-1" },
            { name: "@min", value: 10 },
          ],
        },
        { partitionKey: "tenant-1" }
      )
      .fetchAll();
    const ids = resources.map((r) => r.id);
    assert(ids.includes("q1") && ids.includes("q2"), `param query missing rows: ${ids}`);
    assert(!ids.includes("q3"), "param query leaked other partition");
    console.log(`[ok] parameterized query -> ${ids.length} rows`);
  }

  // --- Cross-partition query --------------------------------------------
  {
    const { resources } = await container.items
      .query("SELECT VALUE COUNT(1) FROM c WHERE c.n >= 10")
      .fetchAll();
    const count = resources[0];
    assert(count >= 3, `cross-partition count too low: ${count}`);
    console.log(`[ok] cross-partition aggregate -> ${count}`);
  }

  // --- Aggregate SUM ----------------------------------------------------
  {
    const { resources } = await container.items
      .query(
        {
          query: "SELECT VALUE SUM(c.n) FROM c WHERE c.partitionKey = @pk",
          parameters: [{ name: "@pk", value: "tenant-1" }],
        },
        { partitionKey: "tenant-1" }
      )
      .fetchAll();
    console.log(`[ok] SUM aggregate -> ${resources[0]}`);
  }

  // --- Patch ------------------------------------------------------------
  {
    const { resource: patched } = await container.item(docId, "tenant-1").patch([
      { op: "add", path: "/patched", value: true },
      { op: "replace", path: "/value", value: "patched" },
    ]);
    assert(patched.patched === true && patched.value === "patched", "patch mismatch");
    console.log("[ok] patched document");
  }

  // --- Transactional batch ---------------------------------------------
  {
    const batchPk = "tenant-batch";
    const ops = [
      { operationType: "Create", resourceBody: { id: "b1", partitionKey: batchPk, value: "x" } },
      { operationType: "Upsert", resourceBody: { id: "b2", partitionKey: batchPk, value: "y" } },
      { operationType: "Read", id: "b1" },
    ];
    const res = await container.items.batch(ops, batchPk);
    assert(res.code === 200 || res.code === undefined, `batch status ${res.code}`);
    assert(Array.isArray(res.result) && res.result.length === 3, "batch result length");
    console.log(`[ok] transactional batch -> ${res.result.length} ops`);
  }

  // --- Stored procedure create + execute --------------------------------
  {
    const sprocBody = {
      id: "echoOk",
      body: function (prefix) {
        var ctx = getContext();
        var res = ctx.getResponse();
        res.setBody(prefix + ":ok");
      }.toString(),
    };
    await container.scripts.storedProcedures.create(sprocBody);
    const { resource: sprocResult } = await container.scripts
      .storedProcedure("echoOk")
      .execute("tenant-1", ["hello"]);
    assert(sprocResult === "hello:ok", `sproc result mismatch: ${JSON.stringify(sprocResult)}`);
    console.log("[ok] stored procedure executed");
  }

  // --- Delete + teardown ------------------------------------------------
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
