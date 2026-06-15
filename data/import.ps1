# ============================================================
# PaperlessAI – Daten-Import (PowerShell)
# Legt Dokumententypen, Tags, Custom Fields und Speicherpfade
# in Paperless-NGX an und speichert KI-Beschreibungen in der
# PaperlessAI-Anwendung.
#
# Verwendung:
#   .\import.ps1 `
#     -PaperlessUrl   http://localhost:8000 `
#     -PaperlessToken cb7f599026... `
#     -AppUrl         http://localhost:5050
#
# Das Skript ist idempotent – bereits vorhandene Einträge
# (gleicher Name) werden übersprungen.
# ============================================================
param(
    [Parameter(Mandatory)] [string] $PaperlessUrl,
    [Parameter(Mandatory)] [string] $PaperlessToken,
    [Parameter(Mandatory)] [string] $AppUrl
)

$ErrorActionPreference = 'Stop'
$PaperlessUrl = $PaperlessUrl.TrimEnd('/')
$AppUrl       = $AppUrl.TrimEnd('/')

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- Hilfsfunktionen ---

$PHeaders = @{
    'Authorization' = "Token $PaperlessToken"
    'Content-Type'  = 'application/json'
}
$AHeaders = @{ 'Content-Type' = 'application/json' }

function Invoke-Paperless($Method, $Endpoint, $Body = $null) {
    $uri = "$PaperlessUrl/api/$Endpoint"
    $params = @{ Uri = $uri; Headers = $PHeaders; Method = $Method }
    if ($Body) { $params['Body'] = ($Body | ConvertTo-Json -Depth 10) }
    Invoke-RestMethod @params
}

function Invoke-App($Method, $Endpoint, $Body = $null) {
    $uri = "$AppUrl/api/$Endpoint"
    $params = @{ Uri = $uri; Headers = $AHeaders; Method = $Method; ErrorAction = 'SilentlyContinue' }
    if ($Body) { $params['Body'] = ($Body | ConvertTo-Json -Depth 10) }
    try { Invoke-RestMethod @params } catch { }
}

function Find-ByName($Endpoint, $Name) {
    $encoded = [Uri]::EscapeDataString($Name)
    $result = Invoke-Paperless GET "${Endpoint}/?name=${encoded}"
    return $result.results | Where-Object { $_.name -eq $Name } | Select-Object -First 1 -ExpandProperty id
}

function Find-InList($List, $Name) {
    return $List | Where-Object { $_.name -eq $Name } | Select-Object -First 1 -ExpandProperty id
}

function Save-Description($AppType, $EntityId, $Name, $Description) {
    if (-not $Description) { return }
    Invoke-App PUT "metadata/$AppType/$EntityId/description" @{
        name        = $Name
        description = $Description
    } | Out-Null
}

function Write-Ok($msg)   { Write-Host "  $([char]0x2713) $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "  ~ $msg" -ForegroundColor DarkGray }
function Write-Err($msg)  { Write-Host "  $([char]0x2717) $msg" -ForegroundColor Red }

# --- Start ---

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " PaperlessAI - Daten-Import" -ForegroundColor Cyan
Write-Host " Paperless: $PaperlessUrl" -ForegroundColor Cyan
Write-Host " App:       $AppUrl" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

# --- 1. Dokumententypen ---

Write-Host ""
Write-Host "[ Dokumententypen ]" -ForegroundColor Yellow
$dokFile = Join-Path $ScriptDir "dokumententypen.json"
$dokTypes = Get-Content $dokFile -Raw | ConvertFrom-Json
$dokCount = 0

foreach ($dt in $dokTypes) {
    $existingId = Find-ByName "document_types" $dt.name

    if ($existingId) {
        Write-Skip "$($dt.name) (ID=$existingId)"
    } else {
        $body = @{
            name              = $dt.name
            match             = $dt.match ?? ""
            matching_algorithm = $dt.matching_algorithm ?? 0
            is_insensitive    = $dt.is_insensitive ?? $true
        }
        $created = Invoke-Paperless POST "document_types/" $body
        $existingId = $created.id
        Write-Ok "$($dt.name) (ID=$existingId)"
        $dokCount++
    }

    Save-Description "document-types" $existingId $dt.name $dt.document_ai_description
}
Write-Host "  -> $dokCount Dokumententypen angelegt"

# --- 2. Tags ---

Write-Host ""
Write-Host "[ Tags ]" -ForegroundColor Yellow
$tagsFile = Join-Path $ScriptDir "tags.json"
$tags = Get-Content $tagsFile -Raw | ConvertFrom-Json
$tagCount = 0

foreach ($tag in $tags) {
    $existingId = Find-ByName "tags" $tag.name

    if ($existingId) {
        Write-Skip "$($tag.name) (ID=$existingId)"
    } else {
        $body = @{
            name        = $tag.name
            color       = $tag.color ?? "#6B7280"
            is_inbox_tag = $tag.is_inbox_tag ?? $false
        }
        $created = Invoke-Paperless POST "tags/" $body
        $existingId = $created.id
        Write-Ok "$($tag.name) (ID=$existingId)"
        $tagCount++
    }

    Save-Description "tags" $existingId $tag.name $tag.description
}
Write-Host "  -> $tagCount Tags angelegt"

# --- 3. Custom Fields ---

Write-Host ""
Write-Host "[ Custom Fields ]" -ForegroundColor Yellow
$felderFile = Join-Path $ScriptDir "felder.json"
$felder = Get-Content $felderFile -Raw | ConvertFrom-Json
$feldCount = 0

# Custom Fields haben keinen Name-Filter → alle laden und lokal suchen
$allFields = Invoke-Paperless GET "custom_fields/?page_size=200"

foreach ($feld in $felder) {
    $existingId = Find-InList $allFields.results $feld.name

    if ($existingId) {
        Write-Skip "$($feld.name) (ID=$existingId)"
    } else {
        $body = @{
            name      = $feld.name
            data_type = $feld.data_type ?? "string"
        }
        if ($feld.extra_data) { $body['extra_data'] = $feld.extra_data }
        $created = Invoke-Paperless POST "custom_fields/" $body
        $existingId = $created.id
        Write-Ok "$($feld.name) [$($feld.data_type)] (ID=$existingId)"
        $feldCount++
    }

    Save-Description "custom-fields" $existingId $feld.name $feld.description
}
Write-Host "  -> $feldCount Custom Fields angelegt"

# --- 4. Speicherpfade ---

Write-Host ""
Write-Host "[ Speicherpfade ]" -ForegroundColor Yellow
$pfadeFile = Join-Path $ScriptDir "speicherpfade.json"
$pfade = Get-Content $pfadeFile -Raw | ConvertFrom-Json
$pfadCount = 0

foreach ($pfad in $pfade) {
    $existingId = Find-ByName "storage_paths" $pfad.name

    if ($existingId) {
        Write-Skip "$($pfad.name) (ID=$existingId)"
    } else {
        $body = @{
            name               = $pfad.name
            path               = $pfad.path
            matching_algorithm = $pfad.matching_algorithm ?? 0
        }
        $created = Invoke-Paperless POST "storage_paths/" $body
        $existingId = $created.id
        Write-Ok "$($pfad.name) (ID=$existingId)"
        $pfadCount++
    }

    Save-Description "storage-paths" $existingId $pfad.name $pfad.description
}
Write-Host "  -> $pfadCount Speicherpfade angelegt"

# --- 5. Token in App-Einstellungen speichern ---

Write-Host ""
Write-Host "[ App-Einstellungen ]" -ForegroundColor Yellow
try {
    Invoke-App PUT "settings" @{
        'Paperless:BaseUrl' = $PaperlessUrl
        'Paperless:Token'   = $PaperlessToken
    } | Out-Null
    Write-Ok "Paperless-URL und Token in App gespeichert"
} catch {
    Write-Err "App-Einstellungen konnten nicht gespeichert werden (App läuft?)"
}

# --- Fertig ---

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " Import abgeschlossen!" -ForegroundColor Green
Write-Host " Naechste Schritte:" -ForegroundColor Cyan
Write-Host " 1. App oeffnen -> Einstellungen -> Azure-Credentials eintragen"
Write-Host " 2. Metadaten-Seiten aufrufen und Beschreibungen pruefen"
Write-Host "============================================" -ForegroundColor Cyan
