<#
.SYNOPSIS
    Cotifly: corre la suite de pruebas por área funcional, o completa (equivalente de correr-tests.sh).

.DESCRIPTION
    Envuelve los mismos comandos de docs/08-testing.md §8.4 (MTP: --filter-class, --filter-trait,
    --ignore-exit-code 8), para que cualquiera del equipo corrobore en su máquina lo que ya corrió el
    CI o una sesión de QA, sin memorizar los filtros exactos de cada PR.

    -Integracion necesita Docker (Testcontainers levanta SQL Server). Sin ella, corre solo las
    pruebas sin la categoría Integracion ni sap (equivalente al job "unitarias" del CI).
    -NoBuild omite "dotnet build -c Release" (asume que ya está compilado).

    Resultados: un .trx por proyecto en TestResults/<area-o-suite>-<unitarias|integracion>/
    (TestResults/ está en .gitignore).

    Agregar un área nueva: sumar una entrada al hashtable $Areas de abajo con los patrones de
    --filter-class de esa área (uno o más; el script corre uno por patrón).

.EXAMPLE
    .\backend-dotnet\scripts\correr-tests.ps1 -Listar
.EXAMPLE
    .\backend-dotnet\scripts\correr-tests.ps1 -Area conexos
.EXAMPLE
    .\backend-dotnet\scripts\correr-tests.ps1 -Area licitaciones -Integracion
.EXAMPLE
    .\backend-dotnet\scripts\correr-tests.ps1 -Suite -Integracion
.EXAMPLE
    .\backend-dotnet\scripts\correr-tests.ps1 -Todo
#>
[CmdletBinding(DefaultParameterSetName = 'Area')]
param(
    [Parameter(ParameterSetName = 'Listar')]
    [switch]$Listar,

    [Parameter(ParameterSetName = 'Area', Mandatory)]
    [string]$Area,

    [Parameter(ParameterSetName = 'Suite')]
    [switch]$Suite,

    [Parameter(ParameterSetName = 'Todo')]
    [switch]$Todo,

    [switch]$Integracion,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$Areas = @{
    'conexos'            = @('*ServiciosConexos*')
    'usd-trm'            = @('*EnvioCosteoService*', '*PdfCosteoMoneda*', '*FijacionMoneda*')
    'redactor-ia'        = @('*RedactorCotizacionLlm*', '*TextosCosteoService*')
    'asistente-folio'    = @('*AsistenteFolio*')
    'retiro-google-chat' = @('*RetiroGoogleChat*')
    'pdf-concurrencia'   = @('*GeneradorPdfMigraDoc*', '*MaquetacionPdf*')
    'licitaciones'       = @('*ValidadorFilasLicitacion*', '*LicitacionesCaptura*', '*CapturaFilasLicitacion*')
}

function Listar-Areas {
    Write-Host "Áreas disponibles (definidas en `$Areas de este script):"
    Write-Host "  conexos              - HU-013.3, servicios conexos desde el catálogo de Nexo (#113)"
    Write-Host "  usd-trm              - HU-013.7, USD en el formulario de costeo con TRM fijada (#114)"
    Write-Host "  redactor-ia          - HU-013.5, redactor IA del asunto e introducción del PDF (#115)"
    Write-Host "  asistente-folio      - HU-013.6, asistente IA por folio (#116)"
    Write-Host "  retiro-google-chat   - HU-013.9, retiro de Google Chat y del agente legado (#120)"
    Write-Host "  pdf-concurrencia     - render de PDF serializado, bug de concurrencia (#92, #95, #121)"
    Write-Host "  licitaciones         - HU-013.11, validación de filas de licitación (#122)"
}

if ($Listar) { Listar-Areas; return }

if (-not $NoBuild) {
    Write-Host "== dotnet build -c Release =="
    dotnet build -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build falló ($LASTEXITCODE)" }
}

function Correr([string]$Etiqueta, [string[]]$FiltroCategoria, [string]$PatronClase) {
    $carpeta = "TestResults/$Etiqueta"
    Write-Host "== dotnet test ($Etiqueta) =="
    $args = @('test', '--solution', 'Cotifly.sln', '-c', 'Release', '--no-build') + $FiltroCategoria
    if ($PatronClase) { $args += @('--filter-class', $PatronClase) }
    $args += @('--ignore-exit-code', '8', '--results-directory', $carpeta, '--report-trx')
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet test falló en '$Etiqueta' ($LASTEXITCODE)" }
}

$FiltroUnitarias = @('--filter-not-trait', 'Categoria=Integracion', '--filter-not-trait', 'Categoria=sap')
$FiltroIntegracion = @('--filter-trait', 'Categoria=Integracion', '--filter-not-trait', 'Categoria=sap')

switch ($PSCmdlet.ParameterSetName) {
    'Area' {
        if (-not $Areas.ContainsKey($Area)) {
            Listar-Areas
            throw "área desconocida: $Area"
        }
        $sufijo = if ($Integracion) { 'integracion' } else { 'unitarias' }
        $filtro = if ($Integracion) { $FiltroIntegracion } else { $FiltroUnitarias }
        foreach ($patron in $Areas[$Area]) {
            Correr "area-$Area-$sufijo" $filtro $patron
        }
    }
    'Suite' {
        if ($Integracion) { Correr 'suite-integracion' $FiltroIntegracion $null }
        else { Correr 'suite-unitarias' $FiltroUnitarias $null }
    }
    'Todo' {
        Correr 'suite-unitarias' $FiltroUnitarias $null
        Correr 'suite-integracion' $FiltroIntegracion $null
    }
}

Write-Host "Listo. Resultados (.trx) en TestResults/ (no versionado)."
