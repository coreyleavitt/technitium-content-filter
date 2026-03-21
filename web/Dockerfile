FROM alpine:3.21 AS tailwind-build
WORKDIR /build
RUN apk add --no-cache curl && \
    curl -sLO https://github.com/tailwindlabs/tailwindcss/releases/download/v3.4.17/tailwindcss-linux-x64 && \
    chmod +x tailwindcss-linux-x64
COPY tailwind.config.js ./
COPY technitium_content_filter/templates/ ./templates/
COPY technitium_content_filter/static/js/ ./static/js/
COPY technitium_content_filter/static/css/input.css ./static/css/input.css
RUN ./tailwindcss-linux-x64 -i ./static/css/input.css -o ./static/css/styles.css --minify

FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim

WORKDIR /app
COPY pyproject.toml uv.lock* ./
RUN uv sync --no-dev --no-install-project
COPY technitium_content_filter/ technitium_content_filter/

COPY --from=tailwind-build /build/static/css/styles.css ./technitium_content_filter/static/css/styles.css

EXPOSE 8000
CMD ["uv", "run", "--no-project", "python", "-m", "technitium_content_filter"]
