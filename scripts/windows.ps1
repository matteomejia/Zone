param(
  [ValidateSet('build', 'test', 'probe', 'pair', 'run', 'package')][string]$Action = 'build',
  [string]$DeviceName = "$env:COMPUTERNAME (Zone probe)",
  [ValidateRange(1, 3600)][int]$Seconds = 30
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repoRoot
try {
  switch ($Action) {
    'package' {
      [xml]$versionProperties = Get-Content -LiteralPath (Join-Path $repoRoot 'apps/windows/Directory.Build.props')
      $releaseVersion = [string]$versionProperties.Project.PropertyGroup.Version
      if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid Windows release version.' }
      $sourceCommit = git rev-parse HEAD
      if ($LASTEXITCODE -ne 0) { throw 'Could not identify the source commit.' }
      $sourceDirty = [bool](git status --porcelain)
      $stagingRoot = Join-Path $repoRoot ("target/windows-package/" + [guid]::NewGuid().ToString('N'))
      $packageName = "Zone-$releaseVersion-windows-x64"
      $publishDirectory = Join-Path $stagingRoot $packageName
      $buildDirectory = Join-Path $stagingRoot 'build'
      dotnet publish apps/windows/Zone.Desktop/Zone.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true --artifacts-path $buildDirectory --output $publishDirectory -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:PublishTrimmed=false --nologo
      if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
      foreach ($requiredFile in @('Zone.Desktop.exe', 'App.xbf', 'Zone.Desktop.pri', 'coreclr.dll', 'Microsoft.ui.xaml.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile))) { throw "Published package is missing $requiredFile." }
      }
      Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $publishDirectory 'LICENSE.txt')
      Copy-Item -LiteralPath (Join-Path $repoRoot 'apps/windows/RELEASE-README.txt') -Destination (Join-Path $publishDirectory 'README.txt')
      [ordered]@{ version = $releaseVersion; platform = 'windows-x64'; sourceCommit = $sourceCommit.Trim(); sourceDirty = $sourceDirty; builtAtUtc = [DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDirectory 'BUILD-INFO.json') -Encoding utf8
      $artifactDirectory = Join-Path $repoRoot 'artifacts'
      New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
      $zipPath = Join-Path $artifactDirectory "$packageName.zip"
      Compress-Archive -LiteralPath $publishDirectory -DestinationPath $zipPath -Force
      $digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
      "$digest  $packageName.zip" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii
      Write-Output "Package: $zipPath"
      Write-Output "SHA256: $digest"
    }
    'build' {
      dotnet build apps/windows/Zone.Probe/Zone.Probe.csproj --configuration Release --nologo
      if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
      dotnet build apps/windows/Zone.Desktop/Zone.Desktop.csproj --configuration Release --nologo
    }
    'run' {
      dotnet build apps/windows/Zone.Desktop/Zone.Desktop.csproj --configuration Release --nologo
      if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
      $desktopExecutable = Join-Path $repoRoot 'apps/windows/Zone.Desktop/bin/Release/net8.0-windows10.0.19041.0/win-x64/Zone.Desktop.exe'
      Start-Process -FilePath $desktopExecutable -WindowStyle Hidden
    }
    'test' { dotnet test apps/windows/Zone.Tests/Zone.Tests.csproj --configuration Release --nologo }
    'probe' { dotnet run --project apps/windows/Zone.Probe/Zone.Probe.csproj --configuration Release -- $DeviceName $Seconds }
    'pair' {
      if (-not $PSBoundParameters.ContainsKey('Seconds')) { $Seconds = 600 }
      dotnet run --project apps/windows/Zone.Probe/Zone.Probe.csproj --configuration Release -- --pair $DeviceName $Seconds
    }
  }
  if ($LASTEXITCODE -ne 0) { throw "Windows $Action failed (exit $LASTEXITCODE)." }
}
finally { Pop-Location }
