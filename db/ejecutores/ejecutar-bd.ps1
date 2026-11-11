<#
.SYNOPSIS
    Instala o pone al día la base de datos de Cotifly: esquema dbo_cotifly, ajustes y configuración inicial.

.DESCRIPTION
    Corre en orden, con sqlcmd y deteniéndose en el primer error:
      1. (opcional, -CrearBase) crea la base de datos si no existe.
      2. esquema/cotifly_esquema.sql -> SOLO si dbo_cotifly no tiene tablas (base nueva): schema, tablas,
                                        restricciones, índices y el procedimiento, en su forma vigente.
                                        Si ya tiene tablas, se salta.
      3. scripts/*.sql (02 en adelante) -> ajustes de las bases existentes, en orden de nombre. Son idempotentes:
                                        sobre una base recién instalada con el paso 2 no cambian nada.
      4. seeder/*.sql                  -> configuración inicial en orden de nombre (líneas de negocio, mapeo SAP TM,
                                        roles, tasa del seguro y política de seguros). Idempotentes.
    Las rutas son relativas a la carpeta db/ (la carpeta padre de este archivo). No corre scripts/operacion/.

    No tiene valores por defecto para el servidor ni la base, a propósito: así nunca se corre por
    accidente contra el servidor compartido. Antes de ejecutar muestra a dónde va y pide confirmación.

.PARAMETER Servidor
    Instancia de SQL Server. Ej.: localhost, localhost,1433 o tbsdatacenter.

.PARAMETER Base
    Base de datos donde vive el schema dbo_cotifly. Ej.: dbTarifario_Nexo_Pruebas.

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
    Se acepta por compatibilidad (docker-compose y scripts anteriores). El ejecutador ya decide solo si instala el
    esquema (paso 2) según dbo_cotifly tenga tablas o no.

.PARAMETER Si
    No pide confirmación.

.EXAMPLE
    # Instalación completa en un SQL Server local, creando la base:
    .\ejecutores\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -CrearBase

.EXAMPLE
    # Solo volver a correr los seeders, con usuario de SQL Server:
    .\ejecutores\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -Usuario sa -SoloSeeder
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

# Este archivo vive en db/ejecutores/: los .sql están en la carpeta padre (db/).
$raizDb = Split-Path -Parent $PSScriptRoot
$esquema = Join-Path $raizDb 'esquema\cotifly_esquema.sql'
$ajustes = Get-ChildItem -Path (Join-Path $raizDb 'scripts') -File -Filter '*.sql' | Sort-Object Name
$seeders = Get-ChildItem -Path (Join-Path $raizDb 'seeder') -File -Filter '*.sql' | Sort-Object Name
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
Write-Host 'Base de datos de Cotifly' -ForegroundColor Cyan
Write-Host "  Servidor : $Servidor"
Write-Host "  Base     : $Base$(if ($CrearBase) { '  (se crea si no existe)' })"
Write-Host "  Acceso   : $(if ($Usuario) { "usuario $Usuario" } else { 'autenticación de Windows' })"
Write-Host "  Pasos    : $(if ($SoloSeeder) { 'seeders' } elseif ($SoloEsquema) { 'esquema (si la base está vacía) + ajustes' } else { 'esquema (si la base está vacía) + ajustes + seeders' })"
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

    if (-not $SoloSeeder) {
        # Base nueva (dbo_cotifly sin tablas) -> esquema completo; base existente -> solo los ajustes.
        $consulta = "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly';"
        $tablas = (& sqlcmd @enBase -h -1 -W -Q $consulta | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $tablas -notmatch '^\d+$') { throw "No se pudo contar las tablas de dbo_cotifly en $Base ($tablas)." }
        if ([int]$tablas -eq 0) {
            Invoke-Sql 'Instalar el esquema dbo_cotifly (esquema\cotifly_esquema.sql)' ($enBase + @('-i', $esquema))
        }
        else {
            Write-Host "dbo_cotifly ya tiene $tablas tablas: no se instala el esquema; se corren los ajustes."
        }

        # Ajustes (02 en adelante): cada uno revisa si su cambio ya está, así que se pueden repetir.
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
               "UNION ALL SELECT 'Roles', COUNT(*) FROM dbo_cotifly.Roles " +
               "UNION ALL SELECT 'Tasas del seguro', COUNT(*) FROM dbo_cotifly.TarifasSeguro " +
               "UNION ALL SELECT 'Tramos de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroTramos " +
               "UNION ALL SELECT 'Medidas de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroMedidas " +
               "UNION ALL SELECT 'Reglas de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroTramoMedidas " +
               "UNION ALL SELECT 'Conexos de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroMedidaConexos;"
    & sqlcmd @enBase -Q $resumen
    Write-Host ''
    Write-Host 'Listo.' -ForegroundColor Green
}
finally {
    Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
}
