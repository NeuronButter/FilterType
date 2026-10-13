#!/usr/bin/env nu
# Packages the FilterType app into FilterTypeApp.zip with dynamic versioning

def main [
    version: string = "alpha",
    --output-dir (-o): string = "artifacts"
] {
    let root_dir = if ("FilterType.csproj" | path exists) {
        $env.PWD
    } else {
        ($env.FILE_PWD | path join "..")
    }

    if ($version | str starts-with "v") or ($version | str starts-with "V") {
        error make { msg: $"Invalid version '($version)'. Versions must not start with 'v' - use format like '1.0.0' or 'alpha'." }
    }

    let is_alpha = ($version | str lowercase) == "alpha" or ($version | is-empty)
    let semver = if $is_alpha { "0.0.0-alpha" } else { $version }
    let info_ver = if $is_alpha { "alpha" } else { $version }

    let csproj_path = ($root_dir | path join "FilterType.csproj" | path expand)
    let publish_dir = ($root_dir | path join "bin" "Release" "publish" | path expand)
    let artifacts_dir = ($root_dir | path join $output_dir | path expand)

    print $"Publishing FilterType with version: ($info_ver) [SemVer: ($semver)]..."
    dotnet publish $csproj_path -c Release -o $publish_dir $"-p:Version=($semver)" $"-p:InformationalVersion=($info_ver)" -p:IncludeSourceRevisionInInformationalVersion=false

    mkdir $artifacts_dir

    let dll_path = ($publish_dir | path join "FilterTypeApp.dll")
    if not ($dll_path | path exists) {
        error make { msg: $"Publish failed: FilterTypeApp.dll not found at '($dll_path)'." }
    }

    let zip_path = ($artifacts_dir | path join "FilterTypeApp.zip")
    let root_zip = ($root_dir | path join "FilterTypeApp.zip")

    if ($nu.os-info.name == "windows") {
        powershell -NoProfile -Command $"Compress-Archive -Path '($publish_dir)/*' -DestinationPath '($zip_path)' -Force"
    } else {
        ^zip -j $zip_path ($publish_dir | path join "dnsApp.config") ($publish_dir | path join "FilterTypeApp.deps.json") ($publish_dir | path join "FilterTypeApp.dll") ($publish_dir | path join "FilterTypeApp.pdb") ($publish_dir | path join "README.md")
    }

    cp -f $zip_path $root_zip

    print $"Package created: ($zip_path) [Version: ($info_ver)]"
}
