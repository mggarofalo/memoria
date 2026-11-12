# syntax=docker/dockerfile:1
# The mutation runner: the toolchains CI's mutation jobs set up (.NET 10, Node 26, Go), with git, in one image.
# The repository's scripts and configs are not baked in: each run uses those of the commit it mutates.
FROM node:26-bookworm AS node
FROM golang:1.27-bookworm AS go

FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx
COPY --from=go /usr/local/go /usr/local/go
# git comes with the SDK image; procps gives the mutation scripts' tests ps.
# python3, make and g++ let npm ci compile better-sqlite3, which has no prebuilt binary for every Node release.
RUN apt-get update \
 && apt-get install -y --no-install-recommends procps python3 make g++ \
 && rm -rf /var/lib/apt/lists/*
# The runner is root and holds the token; the scripts and the tests run as this account, which cannot read it.
# Its cache directories exist here so that the volumes compose mounts on them start out owned by it.
RUN useradd --create-home --shell /bin/sh mutation \
 && mkdir -p /home/mutation/.nuget/packages /home/mutation/.dotnet/tools /home/mutation/.npm /home/mutation/go /home/mutation/.cache/go-build \
 && chown -R mutation:mutation /home/mutation
# auto: Go fetches the toolchain cli/go.mod names, as setup-go does in CI.
ENV PATH=/usr/local/go/bin:/home/mutation/go/bin:$PATH \
    GOTOOLCHAIN=auto \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    MUTATION_HOME=/state \
    MUTATION_USER=mutation
WORKDIR /runner
COPY runner.mts ./
ENTRYPOINT ["node", "/runner/runner.mts"]
CMD ["serve"]
