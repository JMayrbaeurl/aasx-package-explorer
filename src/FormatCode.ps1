﻿<#
.SYNOPSIS
This script formats the code in-place.
#>

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot Common.psm1) -Function `
    AssertDotnet, `
    AssertDotnetFormatVersion

function Main
{
    AssertDotnet
    AssertDotnetFormatVersion

    Set-Location $PSScriptRoot
    dotnet format --exclude "**/DocTest*.cs"
}

Push-Location
try { Main } finally { Pop-Location }
