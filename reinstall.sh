#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Stopping all daemons..."
roslyn-query daemon stop --all 2>/dev/null || true

echo "Uninstalling..."
dotnet tool uninstall -g roslyn-query 2>/dev/null || true

echo "Packing..."
dotnet pack "$SCRIPT_DIR/src/roslyn-query.csproj" -c Release -o "$SCRIPT_DIR/bin/nupkg" --nologo -v q

echo "Installing..."
dotnet tool install -g roslyn-query --add-source "$SCRIPT_DIR/bin/nupkg"

echo "Done. Testing..."
roslyn-query list-projects
