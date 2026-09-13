param([string]$Message = "wip")

$ErrorActionPreference = "Stop"
Set-Location "D:\v100\TERA_SERVER.100\TeraSharp"

Write-Host "== build + tests"
dotnet build TeraSharp.sln -nologo -v q
if ($LASTEXITCODE) { throw "build failed" }
dotnet run --project src\TeraSharp.Arbiter.Tests | Select-String "passed|FAIL"
if ($LASTEXITCODE) { throw "tests failed" }

Write-Host "== commit"
git add -A
git diff --cached --quiet
if ($LASTEXITCODE) { git commit -m $Message } else { Write-Host "nothing to commit" }

Write-Host "== publish"
dotnet publish src\TeraSharp.Arbiter\TeraSharp.Arbiter.csproj -c Release -r win-x64 --self-contained -o D:\TeraSharp-publish 2>&1 | Select-String "error|TeraSharp-publish"
if ($LASTEXITCODE) { throw "publish failed" }
New-Item -ItemType Directory D:\TeraSharp-publish\data -Force | Out-Null
Copy-Item D:\v100\TERA_SERVER.100\TeraSharp\data\starter_blob.bin D:\TeraSharp-publish\data\ -Force
Copy-Item D:\v100\TERA_SERVER.100\TeraSharp\data\starter_inventory.bin D:\TeraSharp-publish\data\ -Force
Copy-Item D:\v100\TERA_SERVER.100\TeraSharp\data\promotions_147E.bin D:\TeraSharp-publish\data\ -Force
Copy-Item D:\v100\TERA_SERVER.100\TeraSharp\data\promotions_147E.bin D:\TeraSharp-publish\data\ -Force

Write-Host "== 7z"
Remove-Item D:\TeraSharp-bin.7z -Force -ErrorAction SilentlyContinue
& "C:\Program Files\7-Zip\7z.exe" a "D:\TeraSharp-bin.7z" "D:\TeraSharp-publish\*" -mx1 | Select-String "Everything"

Write-Host "== done: $(git log --oneline -1)"