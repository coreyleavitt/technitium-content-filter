# Contributing

## Development Setup

### C# Plugin

The plugin builds inside Docker -- no local .NET SDK required:

```bash
# Build
docker build -f Dockerfile.build -o dist .

# Test
docker build -f Dockerfile.test -t content-filter-tests .
docker run --rm content-filter-tests
```

### Python Web UI

```bash
cd web
uv sync --extra test --group dev

# Run tests
uv run pytest

# Lint
uvx ruff check app.py tests/
uvx ruff format --check app.py tests/

# Type check
uv run mypy --strict app.py

# E2E tests
uv run playwright install chromium
uv run pytest -m e2e --no-cov
```

## Code Style

### C#

- Standard C# conventions with nullable reference types enabled
- XML doc comments on public APIs
- `ImplicitUsings` enabled

### Python

- Ruff for linting and formatting (line length 100, target Python 3.12)
- mypy strict mode on `app.py`
- Lint rules: `E, F, W, I, UP, S, B, C4, PIE, SIM`
- Test files exempt from `S101` (assert), `S105`/`S106` (hardcoded passwords)

### JavaScript

- Vanilla JS, no framework or build step (besides Tailwind CSS)
- Functions organized by page, shared utilities in `common.js`

## Pull Request Process

1. Ensure all tests pass:
    - C# unit tests via `Dockerfile.test`
    - Python tests via `uv run pytest`
    - Python E2E tests via `uv run pytest -m e2e --no-cov`
2. Ensure lint and formatting pass:
    - `uvx ruff check app.py tests/`
    - `uvx ruff format --check app.py tests/`
3. CI runs automatically on pull requests

## Project Structure

```
├── src/ContentFilter/       # C# plugin source
├── tests/                         # C# test projects
├── web/                           # Python web UI
│   ├── app.py                     # Application code
│   ├── templates/                 # Mako HTML templates
│   ├── static/                    # JS + CSS
│   └── tests/                     # Python tests
├── docs/                          # Documentation (MkDocs)
├── Dockerfile.build               # Plugin build
├── Dockerfile.test                # C# test runner
└── Dockerfile.integration-test    # Integration test runner
```
