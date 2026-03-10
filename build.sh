#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT_DIR="$PROJECT_DIR/dist"

echo "Building ContentFilter..."
mkdir -p "$OUTPUT_DIR"

docker buildx build \
    -f "$PROJECT_DIR/Dockerfile.build" \
    -o "$OUTPUT_DIR" \
    "$PROJECT_DIR"

echo "Build complete: $OUTPUT_DIR/ContentFilter.zip"
