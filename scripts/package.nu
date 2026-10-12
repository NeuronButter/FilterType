#!/usr/bin/env nu
# Packages the published FilterType app into FilterTypeApp.zip

def main [--output-dir (-o): string = "artifacts"] {
    let root_dir = if ("FilterType.csproj" | path exists) {
        $env.PWD
    } else {
        ($env.FILE_PWD | path join "..")
    }

    let publish_dir = ($root_dir | path join "bin" "Release" "publish" | path expand)
    let artifacts_dir = ($root_dir | path join $output_dir | path expand)
    mkdir $artifacts_dir

    let dll_path = ($publish_dir | path join "FilterTypeApp.dll")
    if not ($dll_path | path exists) {
        error make { msg: $"Publish directory missing or FilterTypeApp.dll not found at '($dll_path)'. Run 'mise run build' first." }
    }

    let zip_path = ($artifacts_dir | path join "FilterTypeApp.zip")
    let root_zip = ($root_dir | path join "FilterTypeApp.zip")

    if ($nu.os-info.name == "windows") {
        powershell -NoProfile -Command $"Compress-Archive -Path '($publish_dir)/*' -DestinationPath '($zip_path)' -Force"
    } else {
        ^zip -j $zip_path ($publish_dir | path join "dnsApp.config") ($publish_dir | path join "FilterTypeApp.deps.json") ($publish_dir | path join "FilterTypeApp.dll") ($publish_dir | path join "FilterTypeApp.pdb") ($publish_dir | path join "README.md")
    }

    cp -f $zip_path $root_zip

    print $"Package created: ($zip_path)"
}
