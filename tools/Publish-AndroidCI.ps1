#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateRange(1, 2100000000)] [int] $BuildVersion,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+(\.\d+)?$')] [string] $DisplayVersion,
    [ValidateSet('both', 'apk', 'aab')] [string] $Format = 'both'
)

$ErrorActionPreference = 'Stop'
foreach ($name in @('ANDROID_KEYSTORE_BASE64', 'ANDROID_SIGNING_PASSWORD', 'ANDROID_GOOGLE_SERVICES_JSON_BASE64', 'RUNNER_TEMP')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Required signing setting $name is missing."
    }
}

# Decode before creating files, without printing a secret or including it in arguments.
try { $keyBytes = [Convert]::FromBase64String($env:ANDROID_KEYSTORE_BASE64) }
catch { throw 'ANDROID_KEYSTORE_BASE64 must contain a base64-encoded upload keystore.' }
if ($keyBytes.Length -eq 0) { throw 'The upload keystore is empty.' }
try {
    $firebaseBytes = [Convert]::FromBase64String($env:ANDROID_GOOGLE_SERVICES_JSON_BASE64)
    $firebase = [Text.Encoding]::UTF8.GetString($firebaseBytes) | ConvertFrom-Json
    if (-not $firebase.project_info.project_id -or
        'club.sussman.groupsplit' -notin @($firebase.client.client_info.android_client_info.package_name)) {
        throw 'Wrong Android Firebase configuration.'
    }
}
catch { throw 'ANDROID_GOOGLE_SERVICES_JSON_BASE64 must contain google-services.json for club.sussman.groupsplit.' }
$firebasePath = Join-Path $PSScriptRoot '../src/GroupSplit.App/GroupSplit.App/Platforms/Android/google-services.json'
if (Test-Path -LiteralPath $firebasePath) { throw 'The CI Firebase configuration path already exists.' }
$firebaseWritten = $false
$signingDirectory = Join-Path $env:RUNNER_TEMP "groupsplit-signing-$([guid]::NewGuid().ToString('N'))"
$keyPath = Join-Path $signingDirectory 'upload.keystore'
$passwordPath = Join-Path $signingDirectory 'upload.signing-password'
$keyAlias = if ($env:ANDROID_KEY_ALIAS) { $env:ANDROID_KEY_ALIAS } else { 'groupsplit-upload' }

try {
    $firebaseWritten = $true
    [IO.File]::WriteAllBytes($firebasePath, $firebaseBytes)
    $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64 = $null
    New-Item -ItemType Directory -Path $signingDirectory | Out-Null
    if (-not $IsWindows) {
        [IO.File]::SetUnixFileMode($signingDirectory, [IO.UnixFileMode]::UserRead -bor
            [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
    }
    [IO.File]::WriteAllBytes($keyPath, $keyBytes)
    [IO.File]::WriteAllText($passwordPath, $env:ANDROID_SIGNING_PASSWORD)
    # Child build processes need only the protected files, not the original secrets.
    $env:ANDROID_KEYSTORE_BASE64 = $null
    $env:ANDROID_SIGNING_PASSWORD = $null
    $formats = if ($Format -eq 'both') { @('apk', 'aab') } else { @($Format) }
    foreach ($packageFormat in $formats) {
        & (Join-Path $PSScriptRoot 'Publish-Android.ps1') `
            -ApiUrl 'https://groupsplit.sussman.win/native/api/' `
            -Authority 'https://groupsplit.sussman.win/idp/realms/group-split' `
            -KeyStore $keyPath -PasswordFile $passwordPath -KeyAlias $keyAlias `
            -BuildVersion $BuildVersion -DisplayVersion $DisplayVersion -Format $packageFormat `
            -JavaSdkDirectory $env:JAVA_HOME -AndroidSdkDirectory $env:ANDROID_HOME `
            -OutputDirectory (Join-Path $PSScriptRoot "../artifacts/android/$packageFormat")
    }
}
finally {
    $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64 = $null
    if ($firebaseWritten) { Remove-Item -LiteralPath $firebasePath -Force -ErrorAction SilentlyContinue }
    $env:ANDROID_KEYSTORE_BASE64 = $null
    $env:ANDROID_SIGNING_PASSWORD = $null
    [Array]::Clear($keyBytes, 0, $keyBytes.Length)
    # The path is a freshly created, uniquely named child of RUNNER_TEMP.
    if (Test-Path -LiteralPath $signingDirectory) {
        Remove-Item -LiteralPath $keyPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $passwordPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $signingDirectory -Force
    }
}
