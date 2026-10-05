<#
.SYNOPSIS
    Cotifly: construir, probar y publicar la imagen del backend .NET en el registry (HU-006.4, #28).

.DESCRIPTION
    Equivalente de publicar-imagen.sh para PowerShell (Windows PowerShell 5.1 o PowerShell 7). Reemplaza a
    deploy/rebuild-on-worker.sh para el backend .NET: construye la imagen, hace la prueba de humo (Development sin base:
    HEALTHCHECK healthy, GET /health 200, GET /health/ready 503), la escanea con Trivy (HIGH/CRITICAL con parche fallan;
    COTIFLY_TRIVY_IMAGEN, por defecto aquasec/trivy:0.74.0 fijada por digest) y la sube a GHCR con tags inmutables. Nunca "latest".

    Tags (sha-<corto> y X.Y.Z son inmutables):
      sha-<commit corto>    siempre; si ya existe en el registry se conserva
      X.Y.Z                 si HEAD tiene un tag git vX.Y.Z (o con -Version X.Y.Z, que exige ese tag en HEAD); se crea
                            sobre el digest de sha-<corto> (imagetools create --prefer-index=false). Final desde
                            origin/main; X.Y.Z-rc.N puede salir de otra rama
      qa | main             con -Canal qa | -Canal main, desde esa rama y con HEAD subido a origin

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
# Trivy fijado por digest (misma imagen que el CI). COTIFLY_TRIVY_IMAGEN permite cambiarlo.
$TrivyImagen = [Environment]::GetEnvironmentVariable('COTIFLY_TRIVY_IMAGEN')
if ([string]::IsNullOrEmpty($TrivyImagen)) { $TrivyImagen = 'aquasec/trivy:0.74.0@sha256:62b1e65e8869bc4b4c6aa4fa2b21595256c7c2f6018a9d9ad61caf87187c1969' }
$Registry = $Imagen.Split('/')[0]
$Semver = '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$'

# Salida de un comando nativo recortada; cadena vacía si no devolvió nada (evita .Trim() sobre $null).
function Texto($Valor) {
    if ($null -eq $Valor) { return '' }
    return (@($Valor) -join "`n").Trim()
}
# git que debe terminar bien (fetch, rev-parse...).
function GitOk {
    $ErrorActionPreference = 'Continue'
    $salida = & git @args
    if ($LASTEXITCODE -ne 0) { Fallar "git $($args -join ' ') terminó con código $LASTEXITCODE" }
    return $salida
}

Push-Location $DirBackend
try {
    # ------------------------------------------------------------------------- commit y versión
    $Commit = Texto (GitOk rev-parse --short=7 HEAD)
    $Sucio = Texto (GitOk status --porcelain -- .)
    if ($Sucio) {
        if ($PermitirSucio -and $SinPush) {
            Write-Host "AVISO: hay cambios sin commit; la imagen no corresponde exactamente a $Commit."
            $Commit = "$Commit-sucio"
        } else {
            Fallar 'hay cambios sin commit. Una imagen publicada debe salir de un commit (usar -SinPush -PermitirSucio para probar).'
        }
    }

    $TagsHead = @(GitOk tag --points-at HEAD)
    if ($Version) {
        if ($Version -notmatch $Semver) { Fallar '-Version debe ser semver (X.Y.Z o X.Y.Z-rc.N)' }
        if ($TagsHead -notcontains "v$Version") { Fallar "HEAD no tiene el tag v$Version" }
    } else {
        $TagGit = $TagsHead | Where-Object { $_ -match '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$' } | Select-Object -First 1
        if ($TagGit) { $Version = $TagGit.Substring(1) }
    }

    # Trazabilidad antes de publicar (igual que publicar-imagen.sh):
    #   - hace falta -Canal o una versión, y HEAD debe estar en alguna rama de origin (un commit local no ocupa sha-<corto>);
    #   - -Canal desde su rama (QA o main) con HEAD en origin/<rama>;
    #   - la versión: el tag de origin apunta a HEAD; final (X.Y.Z) solo desde origin/main; X.Y.Z-rc.N desde cualquier rama.
    if (-not $SinPush) {
        if (-not $Canal -and -not $Version) { Fallar 'para publicar hace falta -Canal qa|main o una versión (tag vX.Y.Z en HEAD o -Version)' }
        GitOk fetch -q origin | Out-Null
        $Ramas = @(GitOk branch -r --contains HEAD) | Where-Object { $_ -notmatch '->' -and $_ -match '^\s*origin/' }
        if (-not $Ramas) { Fallar "HEAD ($Commit) no está en ninguna rama de origin: hacer push antes de publicar" }
        if ($Canal) {
            $RamaCanal = if ($Canal -eq 'main') { 'main' } else { 'QA' }
            $RamaActual = Texto (GitOk rev-parse --abbrev-ref HEAD)
            if ($RamaActual -cne $RamaCanal) { Fallar "-Canal $Canal se publica desde la rama $RamaCanal (rama actual: $RamaActual)" }
            & git merge-base --is-ancestor HEAD "origin/$RamaCanal"
            if ($LASTEXITCODE -ne 0) { Fallar "HEAD no está en origin/${RamaCanal}: hacer push antes de publicar" }
        }
        if ($Version) {
            # Tag anotado: "^{}" da el commit; tag liviano: la propia referencia.
            $Remoto = Texto (GitOk ls-remote origin "refs/tags/v$Version^{}")
            if (-not $Remoto) { $Remoto = Texto (GitOk ls-remote origin "refs/tags/v$Version") }
            if (-not $Remoto) { Fallar "el tag v$Version no está en origin (git push origin v$Version)" }
            $Remoto = ($Remoto -split '\s+')[0]
            $Head = Texto (GitOk rev-parse HEAD)
            if ($Remoto -ne $Head) { Fallar "el tag v$Version de origin apunta a $Remoto, no a HEAD" }
            if ($Version -notmatch '-') {
                & git merge-base --is-ancestor HEAD origin/main
                if ($LASTEXITCODE -ne 0) { Fallar "una versión final (v$Version) sale de un commit de origin/main; para probar fuera de main usar vX.Y.Z-rc.N" }
            }
        }
    }

    # sha-<corto> va primero: los demás tags se crean sobre su digest (mismos bytes).
    $Etiquetas = @("sha-$Commit")
    if ($Version) { $Etiquetas += $Version }
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
            $Puerto = (Texto ((& docker port $Contenedor 8080/tcp) | Select-Object -First 1)).Split(':')[-1]
            $Estado = ''
            for ($i = 0; $i -lt 45; $i++) {
                $Estado = Texto (& docker inspect --format '{{.State.Health.Status}}' $Contenedor)
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
            # Sin 'Stop' aquí: si el contenedor no existe, este error no debe tapar el original.
            $ErrorActionPreference = 'Continue'
            & docker rm -f $Contenedor 2>&1 | Out-Null
            $ErrorActionPreference = 'Stop'
        }
    }

    # ------------------------------------------------------------------------- escaneo de vulnerabilidades
    if (-not $SinEscaneo) {
        Paso "Escaneo de vulnerabilidades (${TrivyImagen}: HIGH/CRITICAL con parche disponible fallan)"
        # Excepciones temporales por CVE con vencimiento (las mismas del CI, #86): ver el encabezado de .trivyignore.yaml.
        # 'HIGH,CRITICAL' entre comillas: sin ellas PowerShell lo convierte en un arreglo de dos argumentos y Trivy falla con
        # "multiple targets cannot be specified" (#86).
        $IgnoreTrivy = Join-Path $DirBackend '.trivyignore.yaml'
        if (-not (Test-Path -LiteralPath $IgnoreTrivy)) { Fallar "no existe $IgnoreTrivy (lo usa también el CI)" }
        DockerOk run --rm -v //var/run/docker.sock:/var/run/docker.sock -v "${IgnoreTrivy}:/trivyignore.yaml:ro" $TrivyImagen `
            image --quiet --scanners vuln --severity 'HIGH,CRITICAL' --ignore-unfixed `
            --ignorefile /trivyignore.yaml --exit-code 1 $ImagenLocal
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
    # Tags inmutables: sha-<corto> se sube solo si no existe; X.Y.Z falla si ya existe y se crea sobre el digest de
    # sha-<corto> con --prefer-index=false (mismo manifiesto, mismo digest); qa/main se mueven a ese digest.
    # Existe: "not found"/MANIFEST_UNKNOWN = no existe; cualquier otro error (red, credenciales) detiene el script.
    function Existe([string]$Etiqueta) {
        $ErrorActionPreference = 'Continue'
        $Salida = Texto (& docker buildx imagetools inspect "${Imagen}:$Etiqueta" 2>&1)
        if ($LASTEXITCODE -eq 0) { return $true }
        if ($Salida -match '(?i)not found|manifest_unknown|manifest unknown') { return $false }
        Fallar "no se pudo consultar ${Imagen}:${Etiqueta}: $Salida"
    }
    foreach ($Etiqueta in $Etiquetas) {
        if ($Etiqueta -eq "sha-$Commit") {
            if (Existe $Etiqueta) {
                Write-Host "${Imagen}:$Etiqueta ya existe: se conserva (los demás tags apuntan a ese digest, no a este build)"
            } else {
                DockerOk tag $ImagenLocal "${Imagen}:$Etiqueta"
                DockerOk push "${Imagen}:$Etiqueta"
            }
            continue
        }
        if ($Etiqueta -ne 'qa' -and $Etiqueta -ne 'main' -and (Existe $Etiqueta)) {
            Fallar "${Imagen}:$Etiqueta ya existe; una versión publicada no se sobrescribe"
        }
        DockerOk buildx imagetools create --prefer-index=false -t "${Imagen}:$Etiqueta" "${Imagen}:sha-$Commit"
    }
    $Digest = ((& docker buildx imagetools inspect "${Imagen}:sha-$Commit") | Where-Object { $_ -match '^Digest:' } | Select-Object -First 1) -replace '^Digest:\s*', ''
    Write-Host ''
    Write-Host 'Imagen publicada.'
    foreach ($Etiqueta in $Etiquetas) { Write-Host "  ${Imagen}:$Etiqueta" }
    Write-Host "  Digest $Digest"
    $TagDespliegue = if ($Version) { $Version } else { "sha-$Commit" }
    Write-Host "Siguiente paso (DEPLOY.md §4): en Dokploy fijar COTIFLY_TAG=$TagDespliegue en el stack y desplegar."
} finally {
    Pop-Location
}
