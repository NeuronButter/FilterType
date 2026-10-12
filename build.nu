#!/usr/bin/env nu
# Minimal helper script to build and package FilterType

dotnet publish FilterType.csproj -c Release -o bin/Release/publish
nu scripts/package.nu
