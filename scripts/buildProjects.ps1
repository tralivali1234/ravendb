function BuildServer ( $srcDir, $outDir, $target, $debug ) {
    write-host "Building Server for $($target.Name)..."
    $command = "dotnet" 
    $commandArgs = @( "publish" )

    $output = [io.path]::combine($outDir, "Server");
    $quotedOutput = '"' + $output + '"'
    $commandArgs += @( "--output", $quotedOutput )

    $configuration = if ($debug) { 'Debug' } else { 'Release' }
    $commandArgs += @( "--configuration", $configuration )
    
    $commandArgs += $( "--runtime", "$($target.Runtime)" )
    $commandArgs += "$srcDir"

    if ([string]::IsNullOrEmpty($target.Arch) -eq $false) {
        $commandArgs += "/p:Platform=$($target.Arch)"
    }

    $commandArgs += '/p:SourceLinkCreate=true'

    write-host -ForegroundColor Cyan "Publish server: $command $commandArgs"
    Invoke-Expression -Command "$command $commandArgs"
    CheckLastExitCode
}

function BuildClient ( $srcDir ) {
    write-host "Building Client"
    & dotnet build /p:SourceLinkCreate=true --no-incremental `
        --configuration "Release" $srcDir;
    CheckLastExitCode
}

function BuildTestDriver ( $srcDir ) {
    write-host "Building TestDriver"
    & dotnet build /p:SourceLinkCreate=true --no-incremental `
        --configuration "Release" $srcDir;
    CheckLastExitCode
}

function BuildTypingsGenerator ( $srcDir ) {
    & dotnet build --no-incremental --configuration "Release" $srcDir;
    CheckLastExitCode
}

function BuildSparrow ( $srcDir ) {
    & dotnet build /p:SourceLinkCreate=true --configuration "Release" $srcDir;
    CheckLastExitCode
}

function NpmInstall () {
    write-host "Doing npm install..."
    $NPM_INSTALL_RETRIES = 3

    foreach ($i in 1..$NPM_INSTALL_RETRIES) {
        try {
            & npm install
            CheckLastExitCode

            break;
        }
        catch {
            write-host "Error doing npm install..."
            if ($i -ge $NPM_INSTALL_RETRIES) {
                throw $_.Exception
            }

            write-host "Retrying npm install..."
        }
    }

}

function BuildStudio ( $srcDir, $version ) {
    write-host "Building Studio..."

    Push-Location

    try {
        Set-Location $srcDir

        NpmInstall

        Write-Host "Update version.txt..."
        $versionJsonPath = [io.path]::combine($srcDir, "wwwroot", "version.txt")

        "{ ""Version"": ""$version"" }" | Out-File $versionJsonPath -Encoding UTF8

        & npm run gulp release
        CheckLastExitCode
    } 
    finally {
        [Console]::ResetColor()
        Pop-Location
    }
}

function ShouldBuildStudio( $studioOutDir, $dontRebuildStudio, $dontBuildStudio ) {
    if ($dontBuildStudio) {
        return $false
    }

    $studioZipPath = [io.path]::combine($studioOutDir, "Raven.Studio.zip")
    if (Test-Path $studioZipPath) {
        return ! $dontRebuildStudio
    }

    return $true
}

function BuildTool ( $toolName, $srcDir, $outDir, $target, $debug ) {
    write-host "Building $toolName for $($target.Name)..."
    $command = "dotnet" 
    $commandArgs = @( "publish" )

    $output = [io.path]::combine($outDir, "${toolName}");
    $quotedOutput = '"' + $output + '"'
    $commandArgs += @( "--output", $quotedOutput )
    $configuration = if ($debug) { 'Debug' } else { 'Release' }
    $commandArgs += @( "--configuration", $configuration )
    $commandArgs += $( "--runtime", "$($target.Runtime)" )
    $commandArgs += "$srcDir"

    if ([string]::IsNullOrEmpty($target.Arch) -eq $false) {
        $commandArgs += "/p:Platform=$($target.Arch)"
    }

    $commandArgs += '/p:SourceLinkCreate=true'

    write-host -ForegroundColor Cyan "Publish ${toolName}: $command $commandArgs"
    Invoke-Expression -Command "$command $commandArgs"
    CheckLastExitCode
}
