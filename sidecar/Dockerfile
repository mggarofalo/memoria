# syntax=docker/dockerfile:1

# memoria-qmd for a host that keeps the index and sends model work to workers. It holds no
# models and asks for no GPU. Build from this directory. See docs/deployment.md.

# The full image for its compiler, as better-sqlite3 builds from source. The runtime below is
# the same Debian release, so what this builds runs there.
FROM node:26-bookworm AS build
WORKDIR /app

# The patch has to be here before the install: `npm ci` runs it as postinstall, and the
# sidecar refuses to start on an unpatched SDK. So no --ignore-scripts.
COPY package.json package-lock.json ./
COPY scripts/patch-qmd.ts scripts/
RUN npm ci --no-audit --no-fund

COPY tsconfig.json tsconfig.build.json ./
COPY src/ src/
RUN npx tsc -p tsconfig.build.json

# The version comes from the release tag (.github/workflows/docker.yml). The build has no
# .git to describe, so an image built without it reports 0.0.0-dev.
ARG MEMORIA_VERSION=0.0.0-dev
RUN printf '%s\n' "$MEMORIA_VERSION" > dist/VERSION

RUN npm prune --omit=dev --no-audit --no-fund

# llama.cpp's prebuilt binaries, CUDA and Vulkan among them. Nothing here may run a model, so
# the image cannot: it is a fact about the image, as well as a setting.
RUN rm -rf node_modules/@node-llama-cpp

FROM node:26-bookworm-slim AS runtime
WORKDIR /app

ENV NODE_ENV=production \
    XDG_CACHE_HOME=/index \
    XDG_CONFIG_HOME=/config \
    MEMORIA_QMD_DB_PATH=/index/qmd/index.sqlite \
    MEMORIA_QMD_HOST=0.0.0.0 \
    MEMORIA_QMD_LLM=remote \
    MEMORIA_QMD_LLM_FALLBACK=0

COPY --from=build /app/package.json ./
COPY --from=build /app/node_modules node_modules/
COPY --from=build /app/dist dist/
COPY scripts/init-index.mjs scripts/snapshot-db.mjs scripts/

# A named volume mounted on one of these takes its ownership on first use, so the
# non-root user can write to it.
RUN mkdir -p /index/qmd /config/qmd /corpus && chown -R node:node /index /config /corpus

USER node
EXPOSE 8282

# Liveness only. Readiness, which covers workers, is /ready.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["node", "-e", "fetch('http://127.0.0.1:8282/health').then(r=>process.exit(r.ok?0:1),()=>process.exit(1))"]

ENTRYPOINT ["node", "--max-old-space-size=4096"]
CMD ["dist/main.js"]
