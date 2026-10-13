#!/usr/bin/env nu
# Minimal helper script to build and package FilterType

def main [version: string = "alpha"] {
    nu scripts/package.nu $version
}
