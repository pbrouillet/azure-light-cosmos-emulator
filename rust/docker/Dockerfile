# Multi-stage build for the Rust/Axum Cosmos DB light emulator.
#
# Build context is the `rust/` workspace directory (see docker-compose.yml).
# The final image runs the `cosmos-emulator` CLI in the foreground, serving the
# Cosmos NoSQL REST API on 8081 (MongoDB wire protocol on 10255 once wired).

FROM rust:1-bookworm AS build
WORKDIR /build

# System build deps. reqwest is http-only and SurrealKV is pure-Rust, so no
# OpenSSL/RocksDB system libraries are required; pkg-config is kept for crates
# that probe for it during their build scripts.
RUN apt-get update \
    && apt-get install -y --no-install-recommends pkg-config \
    && rm -rf /var/lib/apt/lists/*

# Copy the whole workspace and build the CLI binary in release mode.
COPY . .
RUN cargo build --release -p cosmos-cli

FROM debian:bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --home-dir /home/cosmos --shell /usr/sbin/nologin cosmos

COPY --from=build /build/target/release/cosmos-emulator /usr/local/bin/cosmos-emulator

# Persist emulator data across container restarts.
RUN mkdir -p /data && chown cosmos:cosmos /data
VOLUME ["/data"]

USER cosmos

# 8081 = Cosmos NoSQL REST API; 10255 = MongoDB wire protocol.
EXPOSE 8081 10255

ENTRYPOINT ["cosmos-emulator"]
CMD ["start", "--port", "8081", "--data-dir", "/data"]
