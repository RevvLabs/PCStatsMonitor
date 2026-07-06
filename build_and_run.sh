#!/usr/bin/env bash
# PCStatsMonitor v2 — Build & Run (Linux)
# Requires: .NET 10 SDK  →  https://aka.ms/dotnet/download/linux
set -e

cd "$(dirname "$0")"

if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET 10 SDK not found. See https://aka.ms/dotnet/download/linux"
    exit 1
fi

echo "Building solution..."
dotnet build PCStatsMonitor.sln -c Release

echo ""
echo "Build OK. Launching..."
# NVMe SMART temp requires CAP_SYS_RAWIO; RAPL needs /sys/class/powercap read access.
# Run with sudo if some readings are missing, or use: sudo setcap cap_sys_rawio+ep ./PCStatsMonitor
dotnet run --project src/PCStatsMonitor.App/PCStatsMonitor.App.csproj -c Release --no-build
