#!/usr/bin/env node
/*
 * MongoDB wire-protocol parity check (real Node `mongodb` driver).
 *
 * Drives the emulator's MongoDB TCP listener (default port 10255) with an
 * unmodified official driver, proving that a real client completes the
 * connection handshake (`hello`/`isMaster`) and that the diagnostic/admin
 * command surface (`ping`, `buildInfo`) round-trips over `OP_MSG`.
 *
 * Scope note: document CRUD over the MongoDB wire is a *documented scaffold* in
 * BOTH the .NET emulator and this Rust port (the server answers the handshake
 * and returns `{ ok: 1 }` for other commands; collection storage over the Mongo
 * wire is a TODO). This check therefore validates the implemented surface —
 * the handshake and admin commands a driver issues on connect — with a real
 * driver, rather than asserting collection reads/writes that neither emulator
 * claims to support.
 *
 * Opt-in: requires `npm install mongodb` and is NOT run by `cargo test`.
 *
 * Usage:
 *   cargo run -p cosmos-cli -- start --key <key> --mongo-port 10255
 *   npm install mongodb
 *   node crates/parity/sdk/parity_mongo.js --uri mongodb://localhost:10255
 */
"use strict";

function arg(name, fallback) {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

function assert(cond, msg) {
  if (!cond) throw new Error(msg);
}

async function main() {
  let MongoClient;
  try {
    ({ MongoClient } = require("mongodb"));
  } catch (e) {
    console.error("mongodb is not installed. Run: npm install mongodb");
    process.exit(2);
  }

  // directConnection avoids topology/replica-set discovery so the driver talks
  // to this single node; short timeout so a wedged handshake fails fast.
  const uri = arg("--uri", "mongodb://localhost:10255");
  const client = new MongoClient(uri, {
    directConnection: true,
    serverSelectionTimeoutMS: 5000,
  });

  await client.connect();
  console.log("[ok] driver connected (handshake completed)");

  const admin = client.db("admin").admin();

  const ping = await admin.ping();
  assert(ping && ping.ok === 1, `ping not ok: ${JSON.stringify(ping)}`);
  console.log("[ok] ping -> ok");

  const info = await admin.buildInfo();
  assert(info && typeof info.version === "string", "buildInfo missing version");
  assert(Array.isArray(info.versionArray), "buildInfo missing versionArray");
  console.log(`[ok] buildInfo -> version ${info.version}`);

  const hello = await client.db("admin").command({ hello: 1 });
  assert(hello.ok === 1, `hello not ok: ${JSON.stringify(hello)}`);
  assert(hello.maxWireVersion >= 6, `maxWireVersion too low: ${hello.maxWireVersion}`);
  assert(hello.isWritablePrimary === true || hello.ismaster === true, "hello not primary");
  console.log(
    `[ok] hello -> maxWireVersion ${hello.maxWireVersion}, writablePrimary ${
      hello.isWritablePrimary
    }`
  );

  await client.close();
  console.log("PARITY_SDK_MONGO_OK");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
