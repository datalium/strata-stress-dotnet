# The scenarios on Linux, on the same machine as the Windows runs: RESULTS.md's Linux figures.
# The SDK image is pinned by digest (Ubuntu 24.04, SDK 10.0.404, runtime 10.0.15), so a rerun uses the same runtime.
#
#   docker compose up -d --wait
#   docker compose run --rm --build linux scenarios/01-burst -- --quick
#
# It builds the solution once, in the image, and runs one scenario a call. The compose profile (see
# docker-compose.yml) puts it on Postgres's network and mounts ./results, for the JSON.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb

WORKDIR /repo
COPY . .
RUN dotnet restore Strata.Stress.slnx \
    && dotnet build Strata.Stress.slnx --configuration Release --no-restore

ENTRYPOINT ["dotnet", "run", "--configuration", "Release", "--no-build", "--project"]
