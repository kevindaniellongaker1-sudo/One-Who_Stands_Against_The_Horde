#!/bin/bash
cd "$(dirname "$0")"

if ! command -v dotnet &>/dev/null; then
    echo ".NET 8 SDK is required. Download it free from:"
    echo "  https://dotnet.microsoft.com/download"
    read -rp "Press Enter to exit..."
    exit 1
fi

dotnet run --project goblinminigame.csproj
read -rp "Press Enter to exit..."
