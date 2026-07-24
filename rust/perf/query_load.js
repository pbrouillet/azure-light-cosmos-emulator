#!/usr/bin/env node
// Query load driver for the Rust Cosmos emulator.
//
// Exercises the known memory hotspot: SqlQueryEngine materializes the *entire*
// container per query, so concurrent full-scan queries drive peak RSS. Uses
// only Node built-ins (http/https) so it needs no npm install. Auth uses the
// local Explorer bypass header (x-ms-cosmos-explorer) — this is a perf probe,
// not an auth test.
//
// Subcommands:
//   seed  --docs N --doc-size S   Create a database + container and bulk-insert
//                                 N documents of ~S bytes across P partitions.
//   load  --concurrency C --duration D [--query Q]
//                                 Drive C concurrent full-scan queries for D
//                                 seconds; print a JSON summary + PERF_LOAD_JSON.
//
// Common flags: --endpoint URL --db NAME --coll NAME --partitions P --insecure
//
// Exit code is non-zero on fatal errors (e.g. seed failure).

'use strict';

const http = require('http');
const https = require('https');
const { URL } = require('url');

function parseArgs(argv) {
  const args = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) {
        args[key] = true;
      } else {
        args[key] = next;
        i++;
      }
    } else {
      args._.push(a);
    }
  }
  return args;
}

const args = parseArgs(process.argv.slice(2));
const cmd = args._[0] || 'load';

const ENDPOINT = args.endpoint || 'http://localhost:8081';
const DB = args.db || 'perfdb';
const COLL = args.coll || 'perfcoll';
const PARTITIONS = parseInt(args.partitions || '16', 10);
const INSECURE = !!args.insecure;

const base = new URL(ENDPOINT);
const agentLib = base.protocol === 'https:' ? https : http;
const agent = new agentLib.Agent({
  keepAlive: true,
  maxSockets: parseInt(args.concurrency || '64', 10) + 8,
  ...(base.protocol === 'https:' && INSECURE ? { rejectUnauthorized: false } : {}),
});

function request(method, path, headers, bodyObj) {
  return new Promise((resolve, reject) => {
    const body = bodyObj === undefined ? null : Buffer.from(JSON.stringify(bodyObj));
    const opts = {
      protocol: base.protocol,
      hostname: base.hostname,
      port: base.port,
      method,
      path,
      agent,
      headers: {
        'x-ms-version': '2018-12-31',
        'x-ms-cosmos-explorer': '1',
        ...(body ? { 'Content-Length': body.length } : {}),
        ...headers,
      },
    };
    const req = agentLib.request(opts, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        resolve({ status: res.statusCode, text });
      });
    });
    req.on('error', reject);
    if (body) req.write(body);
    req.end();
  });
}

function pkFor(i) {
  return `pk-${i % PARTITIONS}`;
}

function makeDoc(i, sizeBytes) {
  // Nested-ish document padded to roughly sizeBytes so JsonNode overhead during
  // full-scan materialization is realistic.
  const doc = {
    id: `doc-${i}`,
    pk: pkFor(i),
    seq: i,
    value: (i * 7919) % 1000,
    active: i % 2 === 0,
    tags: [`t${i % 10}`, `g${i % 3}`],
    nested: { a: i, b: `label-${i % 50}`, c: (i % 4) === 0 },
  };
  const size = Buffer.byteLength(JSON.stringify(doc));
  const pad = Math.max(0, sizeBytes - size - 12);
  if (pad > 0) doc.filler = 'x'.repeat(pad);
  return doc;
}

async function seed() {
  const nDocs = parseInt(args.docs || '25000', 10);
  const docSize = parseInt(args['doc-size'] || '1024', 10);
  const concurrency = parseInt(args.concurrency || '32', 10);

  // Best-effort create db + container (ignore 409 Conflict).
  await request('POST', '/dbs', {}, { id: DB });
  await request('POST', `/dbs/${DB}/colls`, {}, {
    id: COLL,
    partitionKey: { paths: ['/pk'], kind: 'Hash' },
  });

  console.log(`seeding ${nDocs} docs (~${docSize}B) into /dbs/${DB}/colls/${COLL} across ${PARTITIONS} partitions`);
  const startedAt = Date.now();
  let created = 0;
  let errors = 0;
  let next = 0;

  async function worker() {
    while (true) {
      const i = next++;
      if (i >= nDocs) return;
      const doc = makeDoc(i, docSize);
      try {
        const res = await request(
          'POST',
          `/dbs/${DB}/colls/${COLL}/docs`,
          {
            'x-ms-documentdb-partitionkey': JSON.stringify([doc.pk]),
            'Content-Type': 'application/json',
          },
          doc,
        );
        if (res.status >= 200 && res.status < 300) created++;
        else { errors++; if (errors <= 3) console.error(`  create ${i} -> ${res.status} ${res.text.slice(0, 120)}`); }
      } catch (e) {
        errors++; if (errors <= 3) console.error(`  create ${i} error: ${e.message}`);
      }
      if (created % 5000 === 0 && created > 0) {
        process.stdout.write(`  ${created}/${nDocs}\r`);
      }
    }
  }

  await Promise.all(Array.from({ length: concurrency }, worker));
  const secs = (Date.now() - startedAt) / 1000;
  console.log(`\nseeded ${created} docs, ${errors} errors in ${secs.toFixed(1)}s (${(created / secs).toFixed(0)}/s)`);
  if (created === 0) process.exit(1);
}

function percentile(sorted, p) {
  if (sorted.length === 0) return 0;
  const idx = Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length));
  return sorted[idx];
}

async function load() {
  const concurrency = parseInt(args.concurrency || '16', 10);
  const durationS = parseInt(args.duration || '20', 10);
  const query = args.query || 'SELECT * FROM c WHERE c.active = true';

  const queryHeaders = {
    'x-ms-documentdb-isquery': 'true',
    'x-ms-documentdb-query-enablecrosspartition': 'true',
    'Content-Type': 'application/query+json',
  };
  const queryBody = { query, parameters: [] };

  const latencies = [];
  let ok = 0;
  let failed = 0;
  const deadline = Date.now() + durationS * 1000;
  let running = true;

  async function worker() {
    while (running && Date.now() < deadline) {
      const t0 = process.hrtime.bigint();
      try {
        const res = await request(
          'POST',
          `/dbs/${DB}/colls/${COLL}/docs`,
          queryHeaders,
          queryBody,
        );
        const ms = Number(process.hrtime.bigint() - t0) / 1e6;
        if (res.status >= 200 && res.status < 300) {
          ok++;
          latencies.push(ms);
        } else {
          failed++;
          if (failed <= 3) console.error(`  query -> ${res.status} ${res.text.slice(0, 160)}`);
        }
      } catch (e) {
        failed++;
        if (failed <= 3) console.error(`  query error: ${e.message}`);
      }
    }
  }

  const startedAt = Date.now();
  console.log(`load: ${concurrency} workers x ${durationS}s, query="${query}"`);
  await Promise.all(Array.from({ length: concurrency }, worker));
  running = false;
  const secs = (Date.now() - startedAt) / 1000;

  latencies.sort((a, b) => a - b);
  const summary = {
    endpoint: ENDPOINT,
    db: DB,
    coll: COLL,
    concurrency,
    duration_s: Number(secs.toFixed(2)),
    queries_ok: ok,
    queries_failed: failed,
    throughput_qps: Number((ok / secs).toFixed(1)),
    latency_ms: {
      p50: Number(percentile(latencies, 50).toFixed(1)),
      p95: Number(percentile(latencies, 95).toFixed(1)),
      p99: Number(percentile(latencies, 99).toFixed(1)),
      max: Number((latencies[latencies.length - 1] || 0).toFixed(1)),
    },
  };
  console.log(JSON.stringify(summary, null, 2));
  console.log('PERF_LOAD_JSON ' + JSON.stringify(summary));
  if (ok === 0) process.exit(1);
}

(async () => {
  try {
    if (cmd === 'seed') await seed();
    else if (cmd === 'load') await load();
    else {
      console.error(`unknown command: ${cmd} (expected seed|load)`);
      process.exit(2);
    }
  } catch (e) {
    console.error('fatal:', e.stack || e.message);
    process.exit(1);
  }
})();
