param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

dotnet build "NvimWinFormsGui.sln" `
  -nologo `
  -m:1 `
  -p:RestoreDisableParallel=true `
  -p:BuildInParallel=false `
  -p:UseSharedCompilation=false `
  -c $Configuration

exit $LASTEXITCODE
