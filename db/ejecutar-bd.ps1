<#
.SYNOPSIS
    Instala la base de datos de Cotifly desde cero: crea el esquema dbo_cotifly y siembra la configuración inicial.

.DESCRIPTION
    Corre en orden, con sqlcmd y deteniéndose en el primer error:
      1. (opcional, -CrearBase) crea la base de datos si no existe.
      2. scripts/01_crear_esquema.sql  -> schema, tablas, restricciones e índices.
      3. scripts/02_*.sql en adelante  -> ajustes al esquema (ALTER), en orden de nombre. Se pueden repetir.
      4. seeder/*.sql                  -> líneas de negocio, mapeo SAP TM y roles (en orden de nombre).

    Es SOLO para una instalación desde cero: si dbo_cotifly ya tiene tablas, el paso 2 se detiene sin
    tocar nada. Para una base que ya tiene el esquema viejo se usan las migraciones (db/migrations_mssql/).

    No tiene valores por defecto para el servidor ni la base, a propósito: así nunca se corre por
    accidente contra el servidor compartido. Antes de ejecutar muestra a dónde va y pide confirmación.

.PARAMETER Servidor
    Instancia de SQL Server. Ej.: localhost, localhost,1433 o tbsdatacenter.

.PARAMETER Base
    Base de datos donde se crea el schema dbo_cotifly. Ej.: dbTarifario_Nexo_Pruebas.

.PARAMETER Usuario
    Usuario de SQL Server. Si no se indica, usa la autenticación de Windows.

.PARAMETER Clave
    Contraseña del usuario. Si se indica -Usuario y no -Clave, la pide por consola.

.PARAMETER CrearBase
    Crea la base de datos si no existe (con la collation Latin1_General_100_CI_AS_SC_UTF8).

.PARAMETER SoloEsquema
    Corre solo los pasos 2 y 3 (sin seeders).

.PARAMETER SoloSeeder
    Corre solo el paso 4 (el esquema ya debe existir). Se puede repetir sin problema: los seeders son idempotentes.

.PARAMETER Auto
    Modo automático: si dbo_cotifly ya tiene tablas, se salta el paso 2 y corre los ajustes (paso 3) y los seeders.

.PARAMETER Si
    No pide confirmación.

.EXAMPLE
    # Instalación completa en un SQL Server local, creando la base:
    .\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -CrearBase

.EXAMPLE
    # Solo volver a correr los seeders, con usuario de SQL Server:
    .\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -Usuario sa -SoloSeeder
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Servidor,
    [Parameter(Mandatory = $true)] [string] $Base,
    [string] $Usuario,
    [string] $Clave,
    [switch] $CrearBase,
    [switch] $SoloEsquema,
    [switch] $SoloSeeder,
    [switch] $Auto,
    [switch] $Si
)

$ErrorActionPreference = 'Stop'

if ($SoloEsquema -and $SoloSeeder) { throw 'Use -SoloEsquema o -SoloSeeder, no los dos a la vez.' }
if ($Base -notmatch '^[A-Za-z0-9_]+$') { throw "Nombre de base no válido: '$Base'. Solo letras, números y guion bajo." }

$sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
if (-not $sqlcmd) { throw 'No se encontró sqlcmd. Instale las herramientas de línea de comandos de SQL Server (sqlcmd) y vuelva a intentar.' }

$esquema = Join-Path $PSScriptRoot 'scripts\01_crear_esquema.sql'
$ajustes = Get-ChildItem -Path (Join-Path $PSScriptRoot 'scripts') -Filter '*.sql' | Where-Object { $_.Name -ne '01_crear_esquema.sql' } | Sort-Object Name
$seeders = Get-ChildItem -Path (Join-Path $PSScriptRoot 'seeder') -Filter '*.sql' | Sort-Object Name
if (-not (Test-Path $esquema)) { throw "No existe $esquema" }
if (-not $seeders) { throw 'No hay archivos .sql en db\seeder.' }

# Credenciales: la clave va por variable de entorno (SQLCMDPASSWORD) para que no quede en la lista de procesos.
$autenticacion = @('-E')
if ($Usuario) {
    if (-not $Clave) {
        $segura = Read-Host -Prompt "Contraseña de $Usuario" -AsSecureString
        $Clave = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($segura))
    }
    $env:SQLCMDPASSWORD = $Clave
    $autenticacion = @('-U', $Usuario)
}

Write-Host ''
Write-Host 'Instalación de la base de datos de Cotifly' -ForegroundColor Cyan
Write-Host "  Servidor : $Servidor"
Write-Host "  Base     : $Base$(if ($CrearBase) { '  (se crea si no existe)' })"
Write-Host "  Acceso   : $(if ($Usuario) { "usuario $Usuario" } else { 'autenticación de Windows' })"
Write-Host "  Pasos    : $(if ($SoloSeeder) { 'seeders' } elseif ($SoloEsquema) { 'esquema' } else { 'esquema + seeders' })"
Write-Host ''
if (-not $Si) {
    $respuesta = Read-Host 'Escriba SI para continuar'
    if ($respuesta -ne 'SI') { Write-Host 'Cancelado, no se tocó nada.'; return }
}

function Invoke-Sql {
    param([string] $Descripcion, [string[]] $Argumentos)
    Write-Host "-> $Descripcion" -ForegroundColor Yellow
    & sqlcmd @Argumentos
    if ($LASTEXITCODE -ne 0) { throw "Falló: $Descripcion (código $LASTEXITCODE). No se ejecutaron los pasos siguientes." }
}

try {
    # -C confía en el certificado del servidor, -I = QUOTED_IDENTIFIER ON, -b detiene el script en el primer error,
    # -f 65001 lee los archivos como UTF-8 (los .sql llevan acentos).
    $comunes = @('-S', $Servidor, '-C', '-I', '-b', '-f', '65001') + $autenticacion

    if ($CrearBase) {
        $crear = "IF DB_ID(N'$Base') IS NULL CREATE DATABASE [$Base] COLLATE Latin1_General_100_CI_AS_SC_UTF8;"
        Invoke-Sql "Crear la base $Base si no existe" ($comunes + @('-d', 'master', '-Q', $crear))
    }

    $enBase = $comunes + @('-d', $Base)
    $saltarCreacion = $false

    if ($Auto -and -not $SoloSeeder -and -not $SoloEsquema) {
        $consulta = "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly';"
        $tablas = (& sqlcmd @enBase -h -1 -W -Q $consulta | Out-String).Trim()
        if ($tablas -match '^\d+$' -and [int]$tablas -gt 0) {
            Write-Host "dbo_cotifly ya tiene $tablas tablas: se salta la creación del esquema y se corren los ajustes y los seeders."
            $saltarCreacion = $true
        }
    }

    if (-not $SoloSeeder -and -not $saltarCreacion) {
        Invoke-Sql 'Crear el esquema dbo_cotifly (scripts\01_crear_esquema.sql)' ($enBase + @('-i', $esquema))
    }

    # Ajustes al esquema (02 en adelante): cada uno revisa si su cambio ya está, así que se pueden repetir.
    if (-not $SoloSeeder) {
        foreach ($archivo in $ajustes) {
            Invoke-Sql "Ajustar el esquema: scripts\$($archivo.Name)" ($enBase + @('-i', $archivo.FullName))
        }
    }

    if (-not $SoloEsquema) {
        foreach ($archivo in $seeders) {
            Invoke-Sql "Sembrar: seeder\$($archivo.Name)" ($enBase + @('-i', $archivo.FullName))
        }
    }

    Write-Host ''
    Write-Host 'Resumen' -ForegroundColor Cyan
    $resumen = "SET NOCOUNT ON; SELECT 'Tablas' AS Objeto, COUNT(*) AS Total FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly' " +
               "UNION ALL SELECT 'Lineas de negocio', COUNT(*) FROM dbo_cotifly.MargenesPolitica " +
               "UNION ALL SELECT 'Mapeos SAP TM', COUNT(*) FROM dbo_cotifly.SapMapeoTrqType " +
               "UNION ALL SELECT 'Roles', COUNT(*) FROM dbo_cotifly.Roles;"
    & sqlcmd @enBase -Q $resumen
    Write-Host ''
    Write-Host 'Listo.' -ForegroundColor Green
}
finally {
    Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
}
