#requires -Version 7.0
# Exercises the real publishing helpers with a fake dotnet process; no signing key or Android SDK needed.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) "groupsplit-release-check-$([guid]::NewGuid().ToString('N'))"
$repository = Join-Path $fixture 'repository'
$savedEnvironment = @{}
foreach ($name in @('RUNNER_TEMP', 'ANDROID_KEYSTORE_BASE64', 'ANDROID_SIGNING_PASSWORD', 'ANDROID_KEY_ALIAS', 'ANDROID_GOOGLE_SERVICES_JSON_BASE64')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Assert($condition, $message) {
    if (-not $condition) { throw $message }
}
function Set-TestSecrets {
    $env:ANDROID_KEYSTORE_BASE64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('test-keystore'))
    $env:ANDROID_SIGNING_PASSWORD = 'test-password'
    $env:ANDROID_KEY_ALIAS = 'test-upload'
    $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(
        '{"project_info":{"project_id":"test-project"},"client":[{"client_info":{"android_client_info":{"package_name":"club.sussman.groupsplit"}}}]}'))
}
function Assert-Cleaned {
    Assert (@(Get-ChildItem -LiteralPath $env:RUNNER_TEMP -Force).Count -eq 0) 'Signing files survived the build.'
    Assert (-not $env:ANDROID_KEYSTORE_BASE64 -and -not $env:ANDROID_SIGNING_PASSWORD) 'Secrets survived in the build environment.'
    Assert (-not $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64) 'Firebase input survived in the build environment.'
    Assert (-not (Test-Path -LiteralPath $firebasePath)) 'Firebase config survived the CI build.'
    foreach ($arguments in $releaseCheckState.Calls) {
        $keyPasswordPath = ($arguments | Where-Object { $_ -like '-p:AndroidSigningKeyPass=file:*' }) -replace '^-p:AndroidSigningKeyPass=file:', ''
        Assert (-not (Test-Path -LiteralPath $keyPasswordPath)) 'The temporary key password file survived publishing.'
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $repository 'tools') -Force | Out-Null
    $firebasePath = Join-Path $repository 'src/GroupSplit.App/GroupSplit.App/Platforms/Android/google-services.json'
    New-Item -ItemType Directory -Path (Split-Path $firebasePath) -Force | Out-Null
    $env:RUNNER_TEMP = Join-Path $fixture 'runner'
    New-Item -ItemType Directory -Path $env:RUNNER_TEMP | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../tools/Publish-Android.ps1') -Destination (Join-Path $repository 'tools')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../tools/Publish-AndroidCI.ps1') -Destination (Join-Path $repository 'tools')
    $releaseCheckState = @{ Calls = [Collections.Generic.List[object]]::new(); Fail = $false }

    function dotnet {
        $arguments = @($args)
        Assert (-not $env:ANDROID_KEYSTORE_BASE64 -and -not $env:ANDROID_SIGNING_PASSWORD) 'Build inherited secrets.'
        Assert (-not $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64) 'Build inherited Firebase input.'
        $firebase = Get-Content -LiteralPath $firebasePath -Raw | ConvertFrom-Json
        Assert ($firebase.project_info.project_id -eq 'test-project') 'Firebase config was not supplied to the Android build.'
        $keyPath = ($arguments | Where-Object { $_ -like '-p:AndroidSigningKeyStore=*' }) -replace '^-p:AndroidSigningKeyStore=', ''
        $passwordPath = ($arguments | Where-Object { $_ -like '-p:AndroidSigningStorePass=file:*' }) -replace '^-p:AndroidSigningStorePass=file:', ''
        $keyPasswordPath = ($arguments | Where-Object { $_ -like '-p:AndroidSigningKeyPass=file:*' }) -replace '^-p:AndroidSigningKeyPass=file:', ''
        Assert ($keyPasswordPath -ne $passwordPath) 'Android signer would read both passwords from the same file.'
        Assert ([IO.File]::ReadAllText($keyPasswordPath) -eq 'test-password') 'Key password file is incorrect.'
        Assert ([IO.File]::ReadAllText($keyPath) -eq 'test-keystore') 'Keystore decoding failed.'
        Assert ([IO.File]::ReadAllText($passwordPath) -eq 'test-password') 'Password file is incorrect.'
        Assert ($arguments -contains '-p:AndroidSigningKeyAlias=test-upload') 'Key alias was lost.'
        Assert ($arguments -contains '-p:ApplicationVersion=42') 'Build version was lost.'
        Assert ($arguments -contains '-p:ApplicationDisplayVersion=1.2.3') 'Display version was lost.'
        Assert ($arguments -contains '-p:NativeApiUrl=https://groupsplit.sussman.win/native/api/') 'Production endpoint was lost.'
        $releaseCheckState.Calls.Add($arguments)
        if ($releaseCheckState.Fail) { $global:LASTEXITCODE = 17; return }
        $format = ($arguments | Where-Object { $_ -like '-p:AndroidPackageFormats=*' }) -replace '^-p:AndroidPackageFormats=', ''
        $outputPath = $arguments[[Array]::IndexOf($arguments, '-o') + 1]
        New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $outputPath "club.sussman.groupsplit-Signed.$format"), 'fake package')
        $global:LASTEXITCODE = 0
    }
    $helper = Join-Path $repository 'tools/Publish-AndroidCI.ps1'
    Set-TestSecrets
    & $helper -BuildVersion 42 -DisplayVersion 1.2.3 -Format both | Out-Null
    Assert ($releaseCheckState.Calls.Count -eq 2) 'Both package formats were not built.'
    Assert-Cleaned

    $releaseCheckState = @{ Calls = [Collections.Generic.List[object]]::new(); Fail = $false }
    Set-TestSecrets
    & $helper -BuildVersion 42 -DisplayVersion 1.2.3 -Format aab | Out-Null
    Assert ($releaseCheckState.Calls.Count -eq 1) 'A single-format release built extra packages.'
    Assert-Cleaned

    $releaseCheckState = @{ Calls = [Collections.Generic.List[object]]::new(); Fail = $false }
    $releaseCheckState.Fail = $true
    Set-TestSecrets
    $failed = $false
    try { & $helper -BuildVersion 42 -DisplayVersion 1.2.3 -Format both | Out-Null }
    catch { $failed = $_.Exception.Message -match 'Android publish failed' }
    Assert $failed 'A failed build was accepted.'
    Assert ($releaseCheckState.Calls.Count -eq 1) 'Publishing continued after a failure.'
    Assert-Cleaned

    foreach ($invalid in @('missing', 'invalid-base64', 'invalid-version', 'missing-firebase', 'invalid-firebase', 'wrong-package')) {
        Set-TestSecrets
        if ($invalid -eq 'missing') { $env:ANDROID_SIGNING_PASSWORD = $null }
        if ($invalid -eq 'invalid-base64') { $env:ANDROID_KEYSTORE_BASE64 = 'not base64!' }
        if ($invalid -eq 'missing-firebase') { $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64 = $null }
        if ($invalid -eq 'invalid-firebase') { $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64 = 'not base64!' }
        if ($invalid -eq 'wrong-package') {
            $env:ANDROID_GOOGLE_SERVICES_JSON_BASE64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(
                '{"project_info":{"project_id":"test-project"},"client":[{"client_info":{"android_client_info":{"package_name":"wrong.app"}}}]}'))
        }
        $failed = $false
        try {
            $version = if ($invalid -eq 'invalid-version') { '1.2.3;bad' } else { '1.2.3' }
            & $helper -BuildVersion 42 -DisplayVersion $version | Out-Null
        }
        catch { $failed = $true }
        Assert $failed "Accepted $invalid input."
        Assert (@(Get-ChildItem -LiteralPath $env:RUNNER_TEMP -Force).Count -eq 0) 'Invalid input created signing files.'
    }
    $global:LASTEXITCODE = 0
    Write-Output 'Android release checks passed: both formats, single format, failure cleanup, missing secrets, invalid base64, invalid version.'
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Remove-Item Function:dotnet -ErrorAction SilentlyContinue
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolvedFixture.StartsWith($temporaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test fixture is outside the temporary directory.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
