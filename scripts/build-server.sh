#!/bin/bash
set -e
cd "$(dirname "$0")/../Server/RemoteGamepadServer"
dotnet build
dotnet publish -c Release -r win-x64 --self-contained false
echo "Server built. Run with: dotnet run"
