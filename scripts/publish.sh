#!/usr/bin/env bash
#
# Builds the Windows binaries from the macOS dev machine.
#
# Produces scripts/publish/, containing self-contained win-x64 executables that need no .NET
# runtime installed on the target machine. Copy the whole scripts/ folder to the Windows box
# and run install.ps1 (first time) or update.ps1 (afterwards) from an elevated prompt.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$repo_root/scripts/publish"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if ! command -v dotnet >/dev/null 2>&1; then
    if [ -x "$HOME/.dotnet/dotnet" ]; then
        export PATH="$HOME/.dotnet:$PATH"
    else
        echo "dotnet not found. Install the .NET 8 SDK:" >&2
        echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0" >&2
        exit 1
    fi
fi

echo "Running the test suite first..."
dotnet test "$repo_root/RestMind.sln" --nologo --verbosity quiet

rm -rf "$output"
mkdir -p "$output"

# All three publish into one folder: the service looks for RestMind.Agent.exe beside itself,
# and a single folder keeps the install scripts simple. They are self-contained (no .NET
# install needed on the target) but deliberately not single-file, so the three apps share one
# copy of the runtime instead of carrying three.
for project in RestMind.Service RestMind.Agent RestMind.Ctl; do
    echo "Publishing $project..."
    dotnet publish "$repo_root/src/$project/$project.csproj" \
        --configuration Release \
        --runtime win-x64 \
        --self-contained true \
        -p:DebugType=none \
        --output "$output" \
        --nologo \
        --verbosity quiet
done

echo
echo "Published to $output"
ls -1 "$output"/*.exe 2>/dev/null || true
echo
echo "Next: copy the scripts/ folder to the Windows machine and run, as Administrator:"
echo "  powershell -ExecutionPolicy Bypass -File .\\install.ps1"
