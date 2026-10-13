#!/usr/bin/env nu
# Fetches the latest upstream Technitium release tag and builds reference assemblies in lib/

def get-latest-upstream-tag [] {
    let result = (do { git ls-remote --tags --sort=-v:refname "https://github.com/TechnitiumSoftware/DnsServer.git" } | complete)
    if $result.exit_code != 0 {
        error make { msg: $"Failed to fetch upstream release tags from GitHub (check internet connection): ($result.stderr | str trim)" }
    }

    let matching_lines = ($result.stdout
        | lines
        | where { not ($in =~ '\^{}') }
        | where { $in =~ 'refs/tags/v\d+\.\d+\.\d+' })

    if ($matching_lines | is-empty) {
        error make { msg: "No valid release tags found in upstream Technitium repository." }
    }

    let tag_line = ($matching_lines | first)
    let parsed = ($tag_line | parse --regex "refs/tags/(?P<tag>v\\d+\\.\\d+\\.\\d+)")
    if ($parsed | is-empty) {
        error make { msg: $"Failed to parse upstream tag from line: '($tag_line)'" }
    }

    $parsed | get tag.0
}

def main [
    --tag (-t): string = ""      # Specific release tag to fetch (e.g. v15.6.1). Defaults to latest upstream release.
    --force (-f)                 # Force re-fetching even if assemblies already exist.
] {
    let root_dir = if ("FilterType.csproj" | path exists) { $env.PWD } else { ($env.FILE_PWD | path join "..") }
    let lib_dir = ($root_dir | path join "lib")
    let tag_file = ($lib_dir | path join ".tag")
    let net_dll = ($lib_dir | path join "TechnitiumLibrary.Net.dll")

    let target_tag = if ($tag | is-empty) {
        get-latest-upstream-tag
    } else {
        if ($tag | str starts-with "v") { $tag } else { $"v($tag)" }
    }

    let tech_tag = $"dns-server-($target_tag)"

    print "========================================================"
    print $" Technitium Reference Assemblies: ($target_tag)"
    print "========================================================"

    # Check if already up-to-date
    if ($net_dll | path exists) and (not $force) {
        let current_tag = if ($tag_file | path exists) { open $tag_file | str trim } else { "" }
        if $current_tag == $target_tag {
            print $"  [cached] lib/ is already up to date with ($target_tag)."
            return {
                tag: $target_tag
                lib_dir: $lib_dir
                status: "cached"
            }
        }
    }

    print $"  [fetching] Building reference assemblies for ($target_tag)..."
    mkdir $lib_dir

    let temp_cache = ($root_dir | path join ".deps-cache" $"build-($target_tag)")
    mkdir $temp_cache

    let local_tech = ($root_dir | path join ".." "TechnitiumLibrary" | path expand)
    let local_dns = ($root_dir | path join ".." "DnsServer" | path expand)

    let tech_repo = ($temp_cache | path join "TechnitiumLibrary")
    let dns_repo = ($temp_cache | path join "DnsServer")

    try {
        # Clone TechnitiumLibrary at the tag
        if not ($tech_repo | path exists) {
            if ($local_tech | path exists) {
                print $"    Cloning from local TechnitiumLibrary \(tag: ($tech_tag)\)..."
                git clone --depth 1 --branch $tech_tag $local_tech $tech_repo
            } else {
                print $"    Cloning from GitHub TechnitiumLibrary \(tag: ($tech_tag)\)..."
                git clone --depth 1 --branch $tech_tag "https://github.com/TechnitiumSoftware/TechnitiumLibrary.git" $tech_repo
            }
        }

        # Clone DnsServer at the tag
        if not ($dns_repo | path exists) {
            if ($local_dns | path exists) {
                print $"    Cloning from local DnsServer \(tag: ($target_tag)\)..."
                git clone --depth 1 --branch $target_tag $local_dns $dns_repo
            } else {
                print $"    Cloning from GitHub DnsServer \(tag: ($target_tag)\)..."
                git clone --depth 1 --branch $target_tag "https://github.com/TechnitiumSoftware/DnsServer.git" $dns_repo
            }
        }

        # Build TechnitiumLibrary.Net
        print "    Building TechnitiumLibrary.Net..."
        let tech_proj = ($tech_repo | path join "TechnitiumLibrary.Net" "TechnitiumLibrary.Net.csproj")
        dotnet build $tech_proj -c Release --source "https://api.nuget.org/v3/index.json" --ignore-failed-sources

        # Build DnsServerCore.ApplicationCommon
        print "    Building DnsServerCore.ApplicationCommon..."
        let dns_proj = ($dns_repo | path join "DnsServerCore.ApplicationCommon" "DnsServerCore.ApplicationCommon.csproj")
        dotnet build $dns_proj -c Release --source "https://api.nuget.org/v3/index.json" --ignore-failed-sources

        # Copy required compiled DLLs to lib/
        let tech_bin = ($tech_repo | path join "bin")
        let app_common_bin = ($dns_repo | path join "DnsServerCore.ApplicationCommon" "bin" "Release")

        cp ($tech_bin | path join "TechnitiumLibrary.Net.dll") $lib_dir
        cp ($tech_bin | path join "TechnitiumLibrary.dll") $lib_dir
        cp ($app_common_bin | path join "DnsServerCore.ApplicationCommon.dll") $lib_dir

        for file in [
            ($tech_bin | path join "TechnitiumLibrary.Net.pdb")
            ($tech_bin | path join "TechnitiumLibrary.pdb")
            ($app_common_bin | path join "DnsServerCore.ApplicationCommon.pdb")
        ] {
            if ($file | path exists) {
                cp $file $lib_dir
            }
        }

        # Save tag marker
        $target_tag | save -f $tag_file

        print $"  Successfully updated lib/ with ($target_tag) assemblies."
        {
            tag: $target_tag
            lib_dir: $lib_dir
            status: "built"
        }
    } catch { |err|
        print $"    Failed to build references for ($target_tag): ($err.msg)"
        error make { msg: $"Failed to fetch/build references for ($target_tag): ($err.msg)" }
    } finally {
        if ($temp_cache | path exists) {
            rm -r -f $temp_cache
        }
    }
}
