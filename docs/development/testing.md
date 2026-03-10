# Testing

The project has comprehensive test coverage across both the C# plugin and Python web UI.

## Test Summary

| Suite | Technology | Count | What It Covers |
|-------|-----------|-------|----------------|
| C# Unit | xUnit + FsCheck | ~310 | Domain matching, filtering logic, profile compilation, config parsing |
| C# Integration | xUnit + Testcontainers | 15 | End-to-end DNS queries against a real Technitium instance |
| C# Benchmarks | BenchmarkDotNet | 3 suites | Performance of domain matching, filtering, and compilation |
| Python Unit/API | pytest | 138 | Config I/O, API endpoints, route rendering, property-based testing |
| Python E2E | Playwright | 81 | Browser tests for all UI pages and interactions |

## Running C# Tests

All C# tests run inside Docker containers that provide the Technitium SDK dependencies.

### Unit + Property Tests

```bash
docker build -f Dockerfile.test -t content-filter-tests .
docker run --rm content-filter-tests
```

### With Coverage

```bash
docker run --rm content-filter-tests \
  dotnet test --no-restore --settings /src/app/tests/coverlet.runsettings
```

Coverage thresholds: 85% line, 80% branch.

### Integration Tests

Requires Docker socket access (Testcontainers launches a Technitium container):

```bash
docker build -f Dockerfile.integration-test -t content-filter-integration .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock content-filter-integration
```

### Benchmarks

```bash
docker run --rm content-filter-tests \
  dotnet run --project /src/app/tests/ParentalControlsApp.Benchmarks -c Release -- --filter "*"
```

### Mutation Testing

```bash
dotnet tool install -g dotnet-stryker
dotnet stryker -f stryker-config.json
```

## Running Python Tests

```bash
cd web
uv sync --extra test
```

### Unit, API, Route, and Property Tests

```bash
uv run pytest
```

This runs 138 tests with 100% code coverage. E2E tests are excluded by default via the `-m 'not e2e'` marker.

### E2E Browser Tests

```bash
uv run playwright install chromium
uv run pytest -m e2e --no-cov
```

Runs 81 Playwright browser tests covering all UI pages and interactions.

### All Tests Together

```bash
uv run pytest -m '' --no-cov
```

## Test Architecture

### C# Test Categories

Tests are tagged with `[Trait("Category", "...")]` for CI filtering:

- **Unit** -- Fast, deterministic, no external dependencies
- **Property** -- FsCheck randomized input testing with reproducible seeds
- **Integration** -- Requires Docker, tests against a real Technitium container

### Python Test Markers

- **unit** -- Config loading, migrations, helpers
- **api** -- API endpoint request/response testing
- **route** -- Page route rendering verification
- **property** -- Hypothesis property-based testing
- **e2e** -- Playwright browser tests (excluded from default run)

### E2E Test Design

The E2E tests use a live server fixture that:

1. Starts a real uvicorn server on a random port
2. Patches config paths and API tokens for isolation
3. Uses sample config data with known profiles, clients, and filters
4. Verifies both UI behavior and on-disk persistence after mutations

Each test file covers a specific page: dashboard, profiles, clients, and each of the five filter pages.

### Property Test Reproducibility

Both FsCheck (C#) and Hypothesis (Python) property tests log seeds for reproducibility. When a property test fails, the seed can be used to replay the exact same inputs.
