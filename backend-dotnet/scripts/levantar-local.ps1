<#
.SYNOPSIS
    Cotifly: entorno local de desarrollo sin docker-compose (ADR-007, #14). SOLO DESARROLLO.

.DESCRIPTION
    Equivalente de levantar-local.sh para PowerShell (Windows PowerShell 5.1 o PowerShell 7). Levanta con
    "docker run" y nombres propios (prefijo cotifly-local-):
      1. Red          cotifly-local
      2. SQL Server   cotifly-local-sql (mcr.microsoft.com/mssql/server:2019-latest, volumen cotifly-local-sqldata,
                      puerto 14333 solo en 127.0.0.1 para SSMS o Azure Data Studio)
      3. Esquema      con sqlcmd 18 (/opt/mssql-tools18, incluido en la misma imagen de SQL Server 2019):
                        - bases dbTarifario_Nexo_Pruebas y dbsolucionesti            (scripts/sql/dev-01-bases.sql)
                        - dbo_cotifly al esquema vigente (0001 a 0010, y luego 02, 03, pendientes/04, 05, 07, 08 y 09): base vacía -> db/scripts/01_crear_esquema.sql;
                          esquema anterior a HU-021 (Cotizaciones) -> 0002 a 0010; ya migrada -> solo el 0010 (idempotente)
                          y después los ajustes db/scripts/02_*.sql en adelante más el pendientes/04, por nombre (se pueden repetir)
                        - seeders de db/seeder/ (idempotentes: líneas de negocio, mapeo SAP, roles)
                        - dbsolucionesti FALSA con usuarios y roles de prueba          (scripts/sql/dev-02-identidad.sql)
                        - IDROLES de desarrollo en Roles y login cotifly_app con permisos mínimos
                                                                                       (scripts/sql/dev-03-roles-y-login.sql)
                        - verificación de esos permisos conectado como cotifly_app     (scripts/sql/dev-04-verificar-permisos.sql)
      4. API          cotifly-local-api (imagen cotifly-api:dev, --env-file, volumen cotifly-local-pdfs, 127.0.0.1:8080)
                      y espera a que GET /health/ready responda 200.

    El archivo de variables por defecto es backend-dotnet/.env.local (no se versiona). Se genera la primera vez con
    las cadenas al SQL Server local y una clave JWT aleatoria; después no se sobrescribe (se le pueden agregar, por
    ejemplo, IA__Proveedor, IA__Modelo e IA__ApiKey). Para regenerarlo, borrarlo.
    Solo borra contenedores, red y volúmenes con prefijo cotifly-local-. Documentación: backend-dotnet/docs/06-ejecucion-local.md.

    Variables de entorno opcionales: COTIFLY_SQL_SA_PASSWORD, COTIFLY_SQL_APP_PASSWORD, COTIFLY_PUERTO_API (8080),
    COTIFLY_PUERTO_SQL (14333), COTIFLY_API_IMAGEN (cotifly-api:dev), COTIFLY_SQL_IMAGEN, COTIFLY_SQLCMD_IMAGEN y
    COTIFLY_SQLCMD_RUTA (por defecto la misma imagen de SQL Server con /opt/mssql-tools18/bin/sqlcmd).
    COTIFLY_SQL_APP_PASSWORD no admite comillas, punto y coma ni espacios (romperían el literal SQL del CREATE LOGIN
    o la cadena de conexión): el script lo valida antes de empezar. Los puertos se publican solo en 127.0.0.1.

.EXAMPLE
    .\backend-dotnet\scripts\levantar-local.ps1
.EXAMPLE
    .\backend-dotnet\scripts\levantar-local.ps1 -Construir
.EXAMPLE
    .\backend-dotnet\scripts\levantar-local.ps1 -Bajar -BorrarDatos
#>
[CmdletBinding()]
param(
    # Reconstruye la imagen cotifly-api:dev antes de levantar.
    [switch]$Construir,
    # Borra los contenedores y la red cotifly-local-* (conserva los volúmenes).
    [switch]$Bajar,
    # Con -Bajar, borra también los volúmenes cotifly-local-sqldata y cotifly-local-pdfs.
    [switch]$BorrarDatos,
    # Archivo de variables para la API; por defecto backend-dotnet/.env.local (se genera si no existe).
    [string]$EnvFile = ''
)

$ErrorActionPreference = 'Stop'

function Valor([string]$Nombre, [string]$PorDefecto) {
    $v = [Environment]::GetEnvironmentVariable($Nombre)
    if ([string]::IsNullOrEmpty($v)) { return $PorDefecto } else { return $v }
}

$DirScripts = $PSScriptRoot
$DirBackend = (Resolve-Path (Join-Path $DirScripts '..')).Path
$DirMigraciones = (Resolve-Path (Join-Path $DirBackend '..\db\migrations_mssql')).Path
$DirDb = (Resolve-Path (Join-Path $DirBackend '..\db')).Path

$Red = 'cotifly-local'
$SqlContenedor = 'cotifly-local-sql'
$ApiContenedor = 'cotifly-local-api'
$VolumenSql = 'cotifly-local-sqldata'
$VolumenPdf = 'cotifly-local-pdfs'
$ImagenSql = Valor 'COTIFLY_SQL_IMAGEN' 'mcr.microsoft.com/mssql/server:2019-latest'
# sqlcmd 18: mcr.microsoft.com/mssql-tools18 no existe en MCR y mcr.microsoft.com/mssql-tools (v13, 2017) está
# obsoleta; la imagen de SQL Server 2019 ya trae /opt/mssql-tools18, así no se descarga nada más.
$ImagenSqlcmd = Valor 'COTIFLY_SQLCMD_IMAGEN' $ImagenSql
$RutaSqlcmd = Valor 'COTIFLY_SQLCMD_RUTA' '/opt/mssql-tools18/bin/sqlcmd'
$ImagenApi = Valor 'COTIFLY_API_IMAGEN' 'cotifly-api:dev'
$ClaveSa = Valor 'COTIFLY_SQL_SA_PASSWORD' 'CotiflyLocal#2026'
$ClaveApp = Valor 'COTIFLY_SQL_APP_PASSWORD' 'CotiflyApp#2026'
$PuertoApi = Valor 'COTIFLY_PUERTO_API' '8080'
$PuertoSql = Valor 'COTIFLY_PUERTO_SQL' '14333'

function Paso([string]$Texto) { Write-Host ''; Write-Host "==> $Texto" }

# Ejecuta docker y falla si termina con código distinto de 0 (la salida se descarta salvo que se pida).
function InvocarDocker([string[]]$Argumentos, [switch]$ConSalida) {
    if ($ConSalida) {
        $salida = & docker @Argumentos
        if ($LASTEXITCODE -ne 0) { throw "docker $($Argumentos[0]) terminó con código $LASTEXITCODE" }
        return $salida
    }
    & docker @Argumentos | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker $($Argumentos[0]) terminó con código $LASTEXITCODE" }
}

# True si "docker <args>" termina con 0 (para inspect). Sin redirigir stderr del ejecutable nativo (PowerShell 5.1
# lo convertiría en error): los "No such object" se descartan cambiando temporalmente la preferencia de errores.
function Existe([string[]]$Argumentos) {
    $anterior = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try { & docker @Argumentos *> $null } catch { } finally { $ErrorActionPreference = $anterior }
    return ($LASTEXITCODE -eq 0)
}

# sqlcmd desde la imagen de herramientas, en la red local. Los scripts se montan de solo lectura (/sql y
# /migraciones). -b corta ante error, -r0 manda los errores a stderr (la salida normal se descarta), -I activa
# QUOTED_IDENTIFIER (índices filtrados), -C confía en el certificado autofirmado del SQL Server local (sqlcmd 18
# cifra por defecto). Las variables de sqlcmd llegan como variables de entorno (-e Nombre=valor). Por defecto se
# conecta como sa; la verificación de permisos pasa -Usuario cotifly_app.
function SqlcmdArchivo([string]$Base, [string]$RutaEnContenedor, [string[]]$Entorno = @(),
                       [string]$Usuario = 'sa', [string]$Clave = $ClaveSa) {
    $argumentos = @('run', '--rm', '--network', $Red,
        '-v', "$(Join-Path $DirScripts 'sql'):/sql:ro", '-v', "${DirMigraciones}:/migraciones:ro", '-v', "${DirDb}:/db:ro")
    foreach ($e in $Entorno) { $argumentos += @('-e', $e) }
    $argumentos += @('--entrypoint', $RutaSqlcmd, $ImagenSqlcmd, '-S', $SqlContenedor, '-U', $Usuario, '-P', $Clave, '-C',
        '-d', $Base, '-b', '-I', '-r0', '-i', $RutaEnContenedor)
    InvocarDocker $argumentos
}

function SqlcmdEscalar([string]$Base, [string]$Consulta) {
    $salida = InvocarDocker -ConSalida @('run', '--rm', '--network', $Red, '--entrypoint', $RutaSqlcmd, $ImagenSqlcmd,
        '-S', $SqlContenedor, '-U', 'sa', '-P', $ClaveSa, '-C', '-d', $Base, '-b', '-h', '-1', '-W', '-Q', "SET NOCOUNT ON; $Consulta")
    return (($salida | Out-String).Trim())
}

# La contraseña de cotifly_app va dentro de un literal N'...' (CREATE LOGIN) y de una cadena de conexión.
if ([string]::IsNullOrEmpty($ClaveApp) -or $ClaveApp -match "['"";\s]") {
    throw 'COTIFLY_SQL_APP_PASSWORD no puede estar vacía ni contener comillas, punto y coma o espacios.'
}

# ----------------------------------------------------------------------------- bajar
if ($Bajar) {
    Paso 'Bajando el entorno local (solo recursos cotifly-local-*)'
    foreach ($c in @($ApiContenedor, $SqlContenedor)) {
        if (Existe @('container', 'inspect', $c)) { InvocarDocker @('rm', '-f', $c); Write-Host "contenedor $c borrado" }
    }
    if (Existe @('network', 'inspect', $Red)) { InvocarDocker @('network', 'rm', $Red); Write-Host "red $Red borrada" }
    if ($BorrarDatos) {
        foreach ($v in @($VolumenSql, $VolumenPdf)) {
            if (Existe @('volume', 'inspect', $v)) { InvocarDocker @('volume', 'rm', $v); Write-Host "volumen $v borrado" }
        }
    }
    return
}

# ----------------------------------------------------------------------------- red y SQL Server
Paso "Red $Red"
if (-not (Existe @('network', 'inspect', $Red))) { InvocarDocker @('network', 'create', $Red) }
Write-Host 'lista'

Paso "SQL Server 2019 ($SqlContenedor, puerto del host $PuertoSql)"
if (Existe @('container', 'inspect', $SqlContenedor)) {
    InvocarDocker @('start', $SqlContenedor)
    Write-Host 'ya existía: iniciado'
} else {
    InvocarDocker @('run', '-d', '--name', $SqlContenedor, '--network', $Red,
        '-e', 'ACCEPT_EULA=Y', '-e', 'MSSQL_PID=Developer', '-e', "MSSQL_SA_PASSWORD=$ClaveSa",
        '-p', "127.0.0.1:${PuertoSql}:1433", '-v', "${VolumenSql}:/var/opt/mssql", $ImagenSql)
    Write-Host 'creado'
}

Write-Host -NoNewline 'esperando a SQL Server'
$intentos = 0
while ($true) {
    $listo = $false
    try { $listo = ((SqlcmdEscalar 'master' 'SELECT 1') -eq '1') } catch { $listo = $false }
    if ($listo) { break }
    $intentos++
    if ($intentos -ge 60) {
        Write-Host ''
        & docker logs --tail 30 $SqlContenedor
        throw 'SQL Server no respondió en 2 minutos.'
    }
    Write-Host -NoNewline '.'
    Start-Sleep -Seconds 2
}
Write-Host ' listo'

# ----------------------------------------------------------------------------- esquema y datos
Paso 'Bases, esquema dbo_cotifly y dbsolucionesti de desarrollo'
SqlcmdArchivo 'master' '/sql/dev-01-bases.sql'
# Esquema vigente (HU-021, ADR-019): 0001 a 0010. Se puede correr las veces que haga falta:
#   - dbo_cotifly sin tablas -> instalación desde cero (db/scripts/01_crear_esquema.sql, equivale a 0001-0010);
#   - con el esquema anterior (tabla Cotizaciones, p. ej. un volumen de antes de HU-021) -> migraciones 0002 a 0010
#     (el 0003 y el 0004 exigen Cotizaciones, por eso no se repiten sobre una base ya migrada);
#   - ya migrada (CotizacionesSolicitud) -> solo el 0010, idempotente.
# Después van los ajustes db/scripts/02_*.sql en adelante (hoy el 02 del cubicador, HU-023.3; el 03 del orden del camión y el id del
# catálogo del conexo y el 05 de la categoría del conexo, HU-014 #188: el backend mapea sus columnas, sin ellos /health/ready da 503
# "falta 03" o "falta 05"; el 07 de BUSQUEDA_AGOTADA, HU-023 #176; el 08 que retira las vistas del borrador de métricas, HU-014.3 #152;
# y el 09 de scorePrioridad, HU-018.1 #155: el backend mapea su columna, sin él /health/ready da 503 "falta 09"), en orden de nombre,
# igual que db/ejecutar-bd.*: cada uno revisa si su cambio ya está, así que se pueden repetir.
# De db/scripts/pendientes/ solo entra el 04_cubicacion_segundo_plano.sql (HU-023.5, el backend mapea sus columnas: sin él
# /health/ready da 503 "falta 04"), intercalado por nombre: queda 02, 03, pendientes/04, 05, 07, 08, 09. El resto de pendientes/ no se corre.
$tablas = SqlcmdEscalar 'dbTarifario_Nexo_Pruebas' "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly'"
$viejo = SqlcmdEscalar 'dbTarifario_Nexo_Pruebas' "SELECT CASE WHEN OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL THEN 0 ELSE 1 END"
if ($tablas -eq '0') {
    Write-Host 'aplicando db/scripts/01_crear_esquema.sql (instalación desde cero, 0001 a 0010)'
    SqlcmdArchivo 'dbTarifario_Nexo_Pruebas' '/db/scripts/01_crear_esquema.sql'
} elseif ($viejo -eq '1') {
    Write-Host 'dbo_cotifly tiene el esquema anterior a HU-021: se aplican 0002 a 0010'
    foreach ($script in @('0002_identidad_estado_agente.sql', '0003_metricas_reenviadas.sql', '0004_costeo_nexo.sql',
                          '0005_lineas_solicitud.sql', '0006_textos_y_concurrencia.sql', '0007_captura_nexo_campos.sql',
                          '0008_horas_por_linea.sql', '0009_reestructuracion_cotizaciones.sql', '0010_nombres_municipio.sql')) {
        Write-Host "aplicando $script"
        SqlcmdArchivo 'dbTarifario_Nexo_Pruebas' "/migraciones/$script"
    }
} else {
    Write-Host 'dbo_cotifly ya está migrado: se aplica solo el 0010 (idempotente)'
    SqlcmdArchivo 'dbTarifario_Nexo_Pruebas' '/migraciones/0010_nombres_municipio.sql'
}
$ajustes = @(Get-ChildItem -Path (Join-Path $DirDb 'scripts') -File -Filter '*.sql' |
    Where-Object { $_.Name -match '^\d{2}_' -and $_.Name -notmatch '^01_' }) +
    @(Get-Item -Path (Join-Path $DirDb 'scripts\pendientes\04_cubicacion_segundo_plano.sql')) | Sort-Object Name
foreach ($ajuste in $ajustes) {
    $relativo = $ajuste.FullName.Substring($DirDb.Length + 1).Replace('\', '/')
    Write-Host "aplicando db/$relativo (ajuste, se puede repetir)"
    SqlcmdArchivo 'dbTarifario_Nexo_Pruebas' "/db/$relativo"
}
foreach ($seeder in @('01_lineas_negocio.sql', '02_sap_mapeo_trqtype.sql', '03_roles.sql')) {
    Write-Host "sembrando $seeder"
    SqlcmdArchivo 'dbTarifario_Nexo_Pruebas' "/db/seeder/$seeder"
}
Write-Host 'aplicando dbsolucionesti de desarrollo'
SqlcmdArchivo 'dbsolucionesti' '/sql/dev-02-identidad.sql'
Write-Host 'aplicando IDROLES de desarrollo y login cotifly_app'
SqlcmdArchivo 'master' '/sql/dev-03-roles-y-login.sql' @("ClaveApp=$ClaveApp")
Write-Host 'verificando los permisos de cotifly_app (debe poder usar dbo_cotifly y nada más)'
SqlcmdArchivo 'dbTarifario_Nexo_Pruebas' '/sql/dev-04-verificar-permisos.sql' @() 'cotifly_app' $ClaveApp
Write-Host 'permisos verificados'

# ----------------------------------------------------------------------------- imagen y variables
Paso "Imagen $ImagenApi"
if ($Construir -or -not (Existe @('image', 'inspect', $ImagenApi))) {
    & docker build -t $ImagenApi -f (Join-Path $DirBackend 'Dockerfile') $DirBackend
    if ($LASTEXITCODE -ne 0) { throw "docker build terminó con código $LASTEXITCODE" }
} else {
    Write-Host 'se usa la existente (-Construir para reconstruirla)'
}

if ([string]::IsNullOrEmpty($EnvFile)) {
    $EnvFile = Join-Path $DirBackend '.env.local'
    if (-not (Test-Path $EnvFile)) {
        Paso "Generando $EnvFile"
        $bytes = New-Object byte[] 48
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $claveJwt = [Convert]::ToBase64String($bytes)
        $lineas = @(
            '# Generado por scripts/levantar-local.ps1 (SOLO DESARROLLO; no se versiona). Borrarlo para regenerarlo.',
            '# Se le pueden agregar variables de .env.example (por ejemplo IA__Proveedor, IA__Modelo, IA__ApiKey).',
            'ASPNETCORE_ENVIRONMENT=Development',
            'Zona__ZonaHoraria=America/Bogota',
            "ConnectionStrings__Cotifly=Server=$SqlContenedor,1433;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=$ClaveApp;Encrypt=True;TrustServerCertificate=True",
            "ConnectionStrings__Identidad=Server=$SqlContenedor,1433;Database=dbsolucionesti;User Id=cotifly_app;Password=$ClaveApp;Encrypt=True;TrustServerCertificate=True",
            "Jwt__SigningKey=$claveJwt",
            'Cors__OrigenesPermitidos__0=http://localhost:4200',
            "Pdf__BaseUrl=http://localhost:$PuertoApi",
            'Correo__Habilitado=false'
        )
        # UTF-8 sin BOM: docker --env-file no reconoce el BOM y tomaría la primera línea como variable.
        [System.IO.File]::WriteAllLines($EnvFile, $lineas, (New-Object System.Text.UTF8Encoding($false)))
    }
}
if (-not (Test-Path $EnvFile)) { throw "No existe el archivo de variables $EnvFile" }

# ----------------------------------------------------------------------------- API
Paso "API ($ApiContenedor, http://localhost:$PuertoApi)"
if (Existe @('container', 'inspect', $ApiContenedor)) { InvocarDocker @('rm', '-f', $ApiContenedor) }
InvocarDocker @('run', '-d', '--name', $ApiContenedor, '--network', $Red, '-p', "127.0.0.1:${PuertoApi}:8080",
    '--env-file', $EnvFile, '-v', "${VolumenPdf}:/data/pdfs", $ImagenApi)

Write-Host -NoNewline 'esperando a /health/ready'
$intentos = 0
while ($true) {
    $codigo = 0
    try {
        $codigo = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri "http://127.0.0.1:$PuertoApi/health/ready").StatusCode
    } catch { $codigo = 0 }
    if ($codigo -eq 200) { break }
    $intentos++
    if ($intentos -ge 45) {
        Write-Host ''
        & docker logs --tail 40 $ApiContenedor
        throw 'La API no quedó lista en 90 s.'
    }
    Write-Host -NoNewline '.'
    Start-Sleep -Seconds 2
}
Write-Host ' lista'

Write-Host @"

Entorno local listo.
  API          http://localhost:$PuertoApi   (GET /health, GET /health/ready)
  SQL Server   127.0.0.1,$PuertoSql   usuario sa (COTIFLY_SQL_SA_PASSWORD) o cotifly_app
  Variables    $EnvFile
  Logs         docker logs -f $ApiContenedor

Login de prueba (usuarios en scripts/sql/dev-02-identidad.sql):
  Invoke-RestMethod -Method Post -Uri http://localhost:$PuertoApi/api/auth/login-clientepro -ContentType 'application/json' -Body '{"idUsuario":"APEREZ"}'

Bajar:  .\backend-dotnet\scripts\levantar-local.ps1 -Bajar [-BorrarDatos]
"@
