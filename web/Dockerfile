FROM alpine:3.21 AS tailwind-build
WORKDIR /build
RUN apk add --no-cache curl && \
    curl -sLO https://github.com/tailwindlabs/tailwindcss/releases/download/v3.4.17/tailwindcss-linux-x64 && \
    chmod +x tailwindcss-linux-x64
COPY tailwind.config.js ./
COPY templates/ ./templates/
COPY static/js/ ./static/js/
COPY static/css/input.css ./static/css/input.css
RUN ./tailwindcss-linux-x64 -i ./static/css/input.css -o ./static/css/styles.css --minify

FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim

WORKDIR /app
COPY pyproject.toml uv.lock* ./
RUN uv sync --no-dev

COPY . .
COPY --from=tailwind-build /build/static/css/styles.css ./static/css/styles.css

EXPOSE 8000
CMD ["uv", "run", "hypercorn", "app:app", "--bind", "0.0.0.0:8000"]
