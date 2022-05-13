[CmdletBinding(PositionalBinding=$false)]

param (
    [string]$command = 'Help',
    [switch]$productionEnv = $false,
    [string]$artifactoryUsername,
    [string]$artifactoryPassword,
    [string]$octopusApyKey
)

$configuration = "Release"
$framework = "net6.0"
$runtime = "win-x64"
$projectName = "PriceChecker"
$projectPath = "./src/$projectName"
$projectFile = "$projectPath/$projectName.csproj"
$testProjectName = "PriceChecker.Test"
$testProjectPath = "./test/$testProjectName"
$testProjectFile = "$testProjectPath/$testProjectName.csproj"
$testAppsettingsFile = "$testProjectPath/bin/$configuration/$framework/appsettings.test.json"
$ciTestAppsettingsFile = "$testProjectPath/bin/$configuration/$framework/appsettings.test.ci.json"
$projectFileOutput = "./bin/$configuration/publish"
$publishedProjectFolder = "$projectPath/bin/$configuration/$framework/$runtime/publish"

if (!$productionEnv) {
    $env:NUGET_LIBRARY_FEED = "http://artifactory.yoox.net/artifactory/api/nuget/ynap-virtual-nuget-library"
    $env:EXTENDED_VERSION = "0.0.0.42"
}

function Help {
    Write-Output "Usage: -command (Help|Nuke|Build|Test|Pack|Push|Release) [-productionEnv]"
}

function Nuke {
    FailFastCmdlet { Remove-Item $projectPath/obj -recurse -force }
    FailFastCmdlet { Remove-Item $projectPath/bin -recurse -force }
	FailFastCmdlet { Remove-Item $testProjectPath/obj -recurse -force }
    FailFastCmdlet { Remove-Item $testProjectPath/bin -recurse -force }
}

function Build {
    FailFast { dotnet restore --source $env:NUGET_LIBRARY_FEED }
    FailFast { dotnet publish $projectFile -c $configuration -f $framework -r $runtime }
}

function Test {
	FailFast { dotnet build $testProjectFile -c $configuration }
    if ($productionEnv) {
        FailFastCmdlet { ReplaceTestAppSettings -ciAppsettings $ciTestAppsettingsFile -localAppSettings $testAppsettingsFile }
    }
    FailFast { dotnet test $testProjectFile -c $configuration --no-build }
}

function Pack {
    FailFast { dotnet-octo pack --overwrite --id $projectName --version $env:EXTENDED_VERSION --basePath $publishedProjectFolder --outFolder $publishedProjectFolder }
}

function Push {
    FailFast { dotnet nuget push $publishedProjectFolder/$projectName.$env:EXTENDED_VERSION.nupkg --source $env:NUGET_FEED --api-key "$artifactoryUsername`:$artifactoryPassword" }
}

function Release {
    $releaseNotes = "<p><strong>Release informations:</strong></p><ul><li>Revision number is <em>$env:EXTENDED_VERSION</em></li><li>Revision hash is <em>$env:COMMIT_SHA</em></li><li>Branchname is <em>$env:BRANCH_NAME</em></li><li>Username is <em>$env:COMMIT_AUTHOR</em></li><li>Commit date is <em>$env:COMMIT_DATE</em></li><li>Summary is <em>$env:NORMALIZED_COMMIT_COMMENT</em></li></ul>"
	New-Item releasenotes.txt -type file -force -value $releaseNotes
    FailFast { dotnet-octo create-release --project="$projectName" --package "$projectName`:$env:EXTENDED_VERSION" --version "$env:EXTENDED_VERSION" --server="$env:OCTOPUS_URL" --apiKey="$octopusApyKey" --releaseNotesFile=/work/releasenotes.txt --packageVersion "$env:EXTENDED_VERSION" --ignoreSslErrors }
}

function FailFast($function) {
    Try
    {
        & $function
        if ($LASTEXITCODE -ne 0) {
            Write-Error "ERROR. ExitCode '$LASTEXITCODE'"
            exit $LASTEXITCODE
        }
    }
    Catch
    {
        Write-Error $_.Exception.Message
        exit -1
    }
}

function FailFastCmdlet($function) {
    Try
    {
        & $function
    }
    Catch
    {
        Write-Error $_.Exception.Message
        exit -1
    }
}

function RunInProductionEnv($function) {
    if ($productionEnv) {
        & $function
    } else {
        $stepName = (Get-Item "function:$function").Name
        Write-Host "Step $stepName skipped due to non-production env" -ForegroundColor Yellow
    }
}

function ReplaceTestAppSettings($ciAppsettings, $localAppsettings){
    $appConfigLocal = Resolve-Path -Path $localAppsettings
    $appConfigTest = Resolve-Path -Path $ciAppsettings
    Move-Item -Path $appConfigTest -Destination $appConfigLocal -Force
}

switch ($command) {
    'Nuke' { Nuke }
    'Build' { Build }
    'Test' { Test }
    'Pack' { RunInProductionEnv Pack }
    'Push' { RunInProductionEnv Push }
    'Release' { RunInProductionEnv Release }
    default { Help }
}
