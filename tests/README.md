# Test Suite

## Projects

| Project | Type | Count | Runtime |
|---------|------|-------|---------|
| `ParentalControlsApp.Tests` | Unit + Property | ~310 | ~5s |
| `ParentalControlsApp.IntegrationTests` | Integration (Docker) | 15 | ~15s |
| `ParentalControlsApp.Benchmarks` | Performance | 3 suites | ~2min |

## Running Tests

All test commands are run inside Docker containers that provide the Technitium SDK dependencies.

### Unit Tests

```bash
docker build -f Dockerfile.test -t parental-controls-tests .
docker run --rm parental-controls-tests
```

### Unit Tests with Coverage

```bash
docker run --rm parental-controls-tests \
  dotnet test --no-restore --settings /src/app/tests/coverlet.runsettings
```

Coverage thresholds: 85% line, 80% branch (configured in `coverlet.runsettings`).

### Integration Tests

Requires Docker socket access (Testcontainers launches a Technitium container):

```bash
docker build -f Dockerfile.integration-test -t parental-controls-integration .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock parental-controls-integration
```

### Filtering by Category

Run only unit tests (skip property-based):

```bash
docker run --rm parental-controls-tests \
  dotnet test --no-restore --filter "Category=Unit"
```

Run only property-based tests:

```bash
docker run --rm parental-controls-tests \
  dotnet test --no-restore --filter "Category=Property"
```

### Benchmarks

```bash
docker build -f Dockerfile.test -t parental-controls-tests .
docker run --rm parental-controls-tests \
  dotnet run --project /src/app/tests/ParentalControlsApp.Benchmarks -c Release -- --filter "*"
```

### Mutation Testing

Requires [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) installed:

```bash
dotnet tool install -g dotnet-stryker
dotnet stryker -f stryker-config.json
```

## Test Architecture

### Categories

Tests are tagged with `[Trait("Category", "...")]` for CI filtering:

- **Unit** -- Fast, deterministic, no external dependencies
- **Property** -- FsCheck randomized input testing
- **Integration** -- Requires Docker, tests against real Technitium container

### Property Test Reproducibility

Property tests use `[ReproducibleProperties]` which sets `QuietOnSuccess = false`, ensuring seeds are always logged. When a property test fails, FsCheck prints the seed. To reproduce:

```csharp
[Property(Replay = "12345,42")]  // paste the seed from the failure output
public Property MyFailingTest() { ... }
```

### Integration Test Isolation

Integration tests share a single Technitium container via `ICollectionFixture<TechnitiumFixture>`:

- Each test pushes its own config via `SetConfigAsync()`
- DNS cache is flushed between config changes to prevent cross-test contamination
- Tests run sequentially within the collection (xUnit default for `[Collection]`)
- Test domains are unique per test to avoid overlap

### Mocking Strategy

- **HTTP**: Custom `HttpMessageHandler` subclasses (no framework dependency)
- **Time**: `IsBlockingActiveNow` accepts `DateTime? utcNow` parameter
- **DNS metadata**: Real `DnsDatagram` instances from TechnitiumLibrary
- No over-mocking: core services tested with real objects

## Coverage

The test suite covers:

- **DomainMatcher**: Exact match, subdomain walking, trailing dots, case insensitivity
- **FilteringService**: All 8 evaluation steps, CIDR matching (IPv4/IPv6), client ID resolution, schedule logic with DST
- **ProfileCompiler**: Service expansion, custom rules, allowlists, blocklists, base profile merging, rewrites
- **BlockListManager**: HTTP download, caching, staleness checks, parsing (plain/adblock/hosts formats)
- **ConfigService**: JSON load/save, backward-compatible converters, atomic writes
- **Negative cases**: Malformed domains, punycode/IDN, large configs (100K domains, 100 profiles), deeply nested subdomains
