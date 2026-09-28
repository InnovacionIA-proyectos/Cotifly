<#
.SYNOPSIS
    Cotifly: construir, probar y publicar la imagen del backend .NET en el registry (HU-006.4, #28).

.DESCRIPTION
    Equivalente de publicar-imagen.sh para PowerShell (Windows PowerShell 5.1 o PowerShell 7). Reemplaza a
    deploy/rebuild-on-worker.sh para el backend .NET: construye la imagen, hace la prueba de humo (Development sin base:
    HEALTHCHECK healthy, GET /health 200, GET /health/ready 503), la escanea con Trivy (HIGH/CRITICAL con parche fallan;
    COTIFLY_TRIVY_VERSION, por defecto 0.74.0) y la sube a GHCR con tags inmutables. Nunca "latest".

    Tags:
      sha-<commit corto>    siempre
      X.Y.Z                 si HEAD tiene un tag git vX.Y.Z (o con -Version X.Y.Z, que exige ese tag en HEAD)
      qa | main             con -Canal qa | -Canal main

    Variables de entorno: COTIFLY_IMAGEN (por defecto ghcr.io/innovacionia-proyectos/cotifly-backend); GHCR_USUARIO y
    GHCR_TOKEN (PAT clásico con write:packages) para "docker login"; si faltan se usa la sesión existente.
    Documentación: DEPLOY.md §4.

.EXAMPLE
    .\backend-dotnet\scripts\publicar-imagen.ps1 -Canal qa
.EXAMPLE
    .\backend-dotnet\scripts\publicar-imagen.ps1 -SinPush
#>
[CmdletBinding()]
param(
    # Agrega el tag del canal (qa o main).
    [ValidateSet('', 'qa', 'main')]
    [string]$Canal = '',
    # Versión semver explícita; HEAD debe tener el tag vX.Y.Z.
    [string]$Version = '',
    # Solo build y prueba de humo.
    [switch]$SinPush,
    # Omite la prueba de humo (no recomendado).
    [switch]$SinPrueba,
    # Omite el escaneo de vulnerabilidades con Trivy (no recomendado).
    [switch]$SinEscaneo,
    # Permite cambios sin commit (solo con -SinPush).
    [switch]$PermitirSucio
)

$ErrorActionPreference = 'Stop'

function Paso([string]$Texto) { Write-Host ''; Write-Host "==> $Texto" }
function Fallar([string]$Texto) { throw "ERROR: $Texto" }
function DockerOk {
    # En Windows PowerShell 5.1 con la salida redirigida, cada línea de stderr de docker (el progreso de build) sería un
    # error terminante con 'Stop': dentro de esta función solo cuenta el código de salida.
    $ErrorActionPreference = 'Continue'
    & docker @args
    if ($LASTEXITCODE -ne 0) { Fallar "docker $($args -join ' ') terminó con código $LASTEXITCODE" }
}

$DirBackend = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Imagen = [Environment]::GetEnvironmentVariable('COTIFLY_IMAGEN')
if ([string]::IsNullOrEmpty($Imagen)) { $Imagen = 'ghcr.io/innovacionia-proyectos/cotifly-backend' }
$ImagenLocal = 'cotifly-api:publicar'
$Contenedor = "cotifly-publicar-$PID"
$Trivy = [Environment]::GetEnvironmentVariable('COTIFLY_TRIVY_VERSION')
if ([string]::IsNullOrEmpty($Trivy)) { $Trivy = '0.74.0' }
$Registry = $Imagen.Split('/')[0]
$Semver = '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'

Push-Location $DirBackend
try {
    # ------------------------------------------------------------------------- commit y versión
    $Commit = (& git rev-parse --short=7 HEAD).Trim()
    $Sucio = (& git status --porcelain -- .)
    if ($Sucio) {
        if ($PermitirSucio -and $SinPush) {
            Write-Host "AVISO: hay cambios sin commit; la imagen no corresponde exactamente a $Commit."
            $Commit = "$Commit-sucio"
        } else {
            Fallar 'hay cambios sin commit. Una imagen publicada debe salir de un commit (usar -SinPush -PermitirSucio para probar).'
        }
    }

    $TagsHead = @(& git tag --points-at HEAD)
    if ($Version) {
        if ($Version -notmatch $Semver) { Fallar '-Version debe ser semver (X.Y.Z o X.Y.Z-rc.N)' }
        if ($TagsHead -notcontains "v$Version") { Fallar "HEAD no tiene el tag v$Version" }
    } else {
        $TagGit = $TagsHead | Where-Object { $_ -match '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' } | Select-Object -First 1
        if ($TagGit) { $Version = $TagGit.Substring(1) }
    }

    $Etiquetas = @()
    if ($Version) { $Etiquetas += $Version }
    $Etiquetas += "sha-$Commit"
    if ($Canal) { $Etiquetas += $Canal }
    if ($Version) { $VersionBuild = $Version } elseif ($Canal) { $VersionBuild = "$Canal-$Commit" } else { $VersionBuild = "manual-$Commit" }

    Write-Host "Imagen:     $Imagen"
    Write-Host "Versión:    $VersionBuild"
    Write-Host "Etiquetas:  $($Etiquetas -join ' ')"

    # ------------------------------------------------------------------------- build
    Paso "docker build ($ImagenLocal)"
    $Fecha = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    DockerOk build --build-arg "VERSION=$VersionBuild" --build-arg "VCS_REF=$Commit" --build-arg "BUILD_DATE=$Fecha" `
        -t $ImagenLocal -f (Join-Path $DirBackend 'Dockerfile') $DirBackend

    # ------------------------------------------------------------------------- prueba de humo
    if (-not $SinPrueba) {
        Paso 'Prueba de humo (Development, sin base: /health 200, /health/ready 503, HEALTHCHECK healthy)'
        try {
            DockerOk run -d --name $Contenedor -p '127.0.0.1::8080' -e ASPNETCORE_ENVIRONMENT=Development $ImagenLocal | Out-Null
            $Puerto = ((& docker port $Contenedor 8080/tcp) | Select-Object -First 1).Split(':')[-1].Trim()
            $Estado = ''
            for ($i = 0; $i -lt 45; $i++) {
                $Estado = (& docker inspect --format '{{.State.Health.Status}}' $Contenedor).Trim()
                if ($Estado -eq 'healthy' -or $Estado -eq 'unhealthy') { break }
                Start-Sleep -Seconds 2
            }
            if ($Estado -ne 'healthy') {
                & docker logs --tail 40 $Contenedor
                Fallar "HEALTHCHECK en estado '$Estado'"
            }
            $Salud = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Puerto/health"
            Write-Host "GET /health -> $($Salud.StatusCode) $($Salud.Content)"
            try {
                $Listo = (Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Puerto/health/ready").StatusCode
            } catch [System.Net.WebException] {
                $Listo = [int]$_.Exception.Response.StatusCode
            } catch {
                # PowerShell 7 (HttpResponseException)
                $Listo = [int]$_.Exception.Response.StatusCode
            }
            if ($Listo -ne 503) { Fallar "GET /health/ready respondió $Listo (se esperaba 503 sin base)" }
            Write-Host 'GET /health/ready -> 503 (esperado sin base)'
        } finally {
            & docker rm -f $Contenedor 2>$null | Out-Null
        }
    }

    # ------------------------------------------------------------------------- escaneo de vulnerabilidades
    if (-not $SinEscaneo) {
        Paso "Escaneo de vulnerabilidades (Trivy ${Trivy}: HIGH/CRITICAL con parche disponible fallan)"
        & docker run --rm -v //var/run/docker.sock:/var/run/docker.sock "aquasec/trivy:$Trivy" `
            image --quiet --scanners vuln --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1 $ImagenLocal
        if ($LASTEXITCODE -ne 0) { Fallar 'la imagen tiene vulnerabilidades HIGH/CRITICAL con parche (actualizar ASPNET_VERSION/SDK_VERSION o el paquete)' }
    }

    # ------------------------------------------------------------------------- push
    if ($SinPush) {
        Paso "Sin push (-SinPush). Imagen local: $ImagenLocal"
        return
    }

    Paso "Login en $Registry"
    $Usuario = [Environment]::GetEnvironmentVariable('GHCR_USUARIO')
    $Token = [Environment]::GetEnvironmentVariable('GHCR_TOKEN')
    if ($Usuario -and $Token) {
        $Token | & docker login $Registry -u $Usuario --password-stdin
        if ($LASTEXITCODE -ne 0) { Fallar 'docker login falló' }
    } else {
        Write-Host 'Sin GHCR_USUARIO/GHCR_TOKEN: se usa la sesión existente de docker login.'
    }

    Paso 'Etiquetar y subir'
    foreach ($Etiqueta in $Etiquetas) {
        DockerOk tag $ImagenLocal "${Imagen}:$Etiqueta"
        DockerOk push "${Imagen}:$Etiqueta"
    }
    $Digest = (& docker inspect --format '{{index .RepoDigests 0}}' "${Imagen}:sha-$Commit")
    Write-Host ''
    Write-Host 'Imagen publicada.'
    foreach ($Etiqueta in $Etiquetas) { Write-Host "  ${Imagen}:$Etiqueta" }
    Write-Host "  Digest $Digest"
    $TagDespliegue = if ($Version) { $Version } else { "sha-$Commit" }
    Write-Host "Siguiente paso (DEPLOY.md §4): en Dokploy fijar COTIFLY_TAG=$TagDespliegue en el stack y desplegar."
} finally {
    Pop-Location
}
