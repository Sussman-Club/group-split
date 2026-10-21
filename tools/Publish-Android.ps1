#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [uri] $ApiUrl,
    [Parameter(Mandatory)] [uri] $Authority,
    [Parameter(Mandatory)] [string] $KeyStore,
    [Parameter(Mandatory)] [string] $PasswordFile,
    [string] $KeyAlias = 'groupsplit-upload',
    [ValidateRange(1, 2100000000)] [int] $BuildVersion = 1,
    [ValidatePattern('^\d+\.\d+(\.\d+)?$')] [string] $DisplayVersion = '1.0.0',
    [ValidateSet('aab', 'apk')] [string] $Format = 'aab',
    [string] $JavaSdkDirectory,
    [string] $AndroidSdkDirectory,
    [string] $OutputDirectory,
    [string] $DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
foreach ($endpoint in @($ApiUrl, $Authority)) {
    if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https' -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment) {
        throw 'Release API and realm addresses must be absolute HTTPS URLs without credentials, query strings or fragments.'
    }
}
$keyStorePath = (Resolve-Path -LiteralPath $KeyStore).Path
$passwordPath = (Resolve-Path -LiteralPath $PasswordFile).Path
$repositoryPath = Split-Path -Parent $PSScriptRoot
foreach ($secretPath in @($keyStorePath, $passwordPath)) {
    if ($secretPath.StartsWith($repositoryPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep the keystore and password file outside the repository.'
    }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryPath 'artifacts/android' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($value in @($ApiUrl.AbsoluteUri, $Authority.AbsoluteUri, $keyStorePath, $passwordPath, $KeyAlias, $outputPath)) {
    if ($value -match '[;\r\n]') { throw 'Build arguments must not contain semicolons or line breaks.' }
}
$projectPath = Join-Path $repositoryPath 'src/GroupSplit.App/GroupSplit.App/GroupSplit.App.csproj'
$keyPasswordDirectory = Join-Path ([IO.Path]::GetTempPath()) "groupsplit-key-password-$([guid]::NewGuid().ToString('N'))"
$keyPasswordPath = Join-Path $keyPasswordDirectory 'key.signing-password'
$publishArguments = @(
    'publish', $projectPath, '-f', 'net10.0-android', '-c', 'Release',
    '-p:AndroidOnly=true', "-p:NativeApiUrl=$($ApiUrl.AbsoluteUri)", "-p:NativeAuthority=$($Authority.AbsoluteUri)",
    "-p:ApplicationVersion=$BuildVersion", "-p:ApplicationDisplayVersion=$DisplayVersion",
    "-p:AndroidPackageFormats=$Format", '-p:AndroidKeyStore=true',
    "-p:AndroidSigningKeyStore=$keyStorePath", "-p:AndroidSigningKeyAlias=$KeyAlias",
    "-p:AndroidSigningKeyPass=file:$keyPasswordPath", "-p:AndroidSigningStorePass=file:$passwordPath",
    '-o', $outputPath
)
foreach ($sdkProperty in @('JavaSdkDirectory', 'AndroidSdkDirectory')) {
    $sdkPath = Get-Variable -Name $sdkProperty -ValueOnly
    if ($sdkPath) {
        $sdkPath = (Resolve-Path -LiteralPath $sdkPath).Path
        if ($sdkPath -match '[;\r\n]') { throw 'SDK paths must not contain semicolons or line breaks.' }
        $publishArguments += "-p:${sdkProperty}=$sdkPath"
    }
}
try {
    # apksigner reads each password from the next line of a file. Reusing one file for
    # both arguments exhausts a one-line password file, even when the passwords match.
    New-Item -ItemType Directory -Path $keyPasswordDirectory | Out-Null
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode($keyPasswordDirectory, [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
    }
    Copy-Item -LiteralPath $passwordPath -Destination $keyPasswordPath
    & $DotnetPath @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "Android publish failed (exit $LASTEXITCODE)." }
}
finally {
    if (Test-Path -LiteralPath $keyPasswordPath) { Remove-Item -LiteralPath $keyPasswordPath -Force }
    if (Test-Path -LiteralPath $keyPasswordDirectory) { Remove-Item -LiteralPath $keyPasswordDirectory -Force }
}
$signedPackages = @(Get-ChildItem -LiteralPath $outputPath -Filter "*.$Format" -File |
    Where-Object { $_.BaseName -match '-signed$' })
if ($signedPackages.Count -eq 0) { throw "Publish produced no signed .$Format package in $outputPath." }
$signedPackages | Select-Object FullName, Length
