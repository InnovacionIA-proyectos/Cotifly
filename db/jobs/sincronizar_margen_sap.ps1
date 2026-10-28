<#
.SYNOPSIS
    HU-042.1 (#242). Paso de PowerShell del SQL Server Agent Job que sincroniza
    dbo_cotifly.SapMargenCliente desde SAP TM (RentabClienteSet).

.DESCRIPTION
    0. Revisa en SapMargenSyncLog cuándo fue la última corrida exitosa; si fue hace menos de
       SYNC_INTERVAL_MINUTOS, sale sin hacer nada. El job de SQL Agent se programa para intentar
       correr seguido (p. ej. cada hora); el intervalo REAL de sincronización lo decide esta
       variable de entorno, sin tocar ni recrear el horario del job (1440 min = 24h por defecto;
       cambiarlo a 60, por ejemplo, es solo cambiar la variable).
    1. Trae el set completo de RentabClienteSet de SAP TM (confirmado por curl: sin $filter
       trae todo, ~1500 registros, no hace falta paginar).
    2. Resuelve el NIT de cada BP contra el DW de Nexo (DimBPSolicitante) para que la fila
       ya quede cruzable directo con CotizacionesSolicitud, sin que el backend tenga que
       resolverlo aparte.
    3. Vuelca todo a una tabla temporal #SapMargenStage (crear + bulk insert) en la base destino — no es
       una tabla fija del esquema, se crea en esta conexión y desaparece sola al terminar.
    4. Llama a dbo_cotifly.sp_sincronizar_margen_sap, que hace el upsert real (alta, cambio
       de valor, soft delete de lo que ya no viene) y escribe el resultado en
       dbo_cotifly.SapMargenSyncLog, todo en una transacción.
    Si algo falla ANTES de llegar al punto 4 (SAP inalcanzable, DW inalcanzable), este script
    registra igual una fila en SapMargenSyncLog con status='error', para que no quede sin
    rastro (mismo criterio que fo-sync-worker de EmbarQA con 'worker_ciclo').

.NOTES
    DW: dbo.DimBPSolicitante vive en DWCubosOperacionesEBS (10.10.40.24,1433), usuario de solo
    lectura 'consultascubos' (confirmado 2026-10-28; es una base DISTINTA de DWCubosEBSSAP, la de
    ConnectionStrings:Regional que ya usa el backend para DimEjecutivo — no se puede reusar esa
    conexión). Columnas BP y NIT tomadas del encabezado real de SSMS.

    ⚠️ PENDIENTE DE CONFIRMAR ANTES DE USAR EN PRODUCCIÓN:
    - Que el servidor donde corre este job tenga ruta de red a sap-tm-prd.boterosoto.local:8000
      y a 10.10.40.24,1433.
    - Las variables de entorno de abajo deben existir en el contexto donde corre el SQL Agent
      Job (perfil del proxy/cuenta del paso); las claves nunca van hardcodeadas aquí (mismo
      criterio que etl-sap/EmbarQA: ver fo-sync-worker/Config.cs). El host, el puerto y el usuario
      ya están confirmados y se dejan de valor por defecto (no son secretos, igual que las demás
      IPs 10.10.40.x de este repo); solo la clave es obligatoria por variable de entorno.
#>

$ErrorActionPreference = 'Stop'

# Carga db/jobs/.env (mismo patrón que backend-dotnet/.env: se versiona .env.example con
# placeholders, .env con los valores reales queda ignorado por git, ver .gitignore de la raíz).
# Si no existe el archivo, no pasa nada: las variables pueden venir ya puestas de otra forma.
$archivoEnv = Join-Path $PSScriptRoot '.env'
if (Test-Path $archivoEnv) {
    Get-Content $archivoEnv | ForEach-Object {
        $linea = $_.Trim()
        if ($linea -and -not $linea.StartsWith('#') -and $linea.Contains('=')) {
            $partes = $linea.Split('=', 2)
            $nombre = $partes[0].Trim()
            $valor = $partes[1].Trim().Trim('"').Trim("'")
            [Environment]::SetEnvironmentVariable($nombre, $valor, 'Process')
        }
    }
}

function Variable-Requerida([string]$Nombre) {
    $valor = [Environment]::GetEnvironmentVariable($Nombre)
    if ([string]::IsNullOrWhiteSpace($valor)) {
        throw "Falta la variable de entorno $Nombre."
    }
    return $valor
}

function Variable-Opcional([string]$Nombre, [string]$PorDefecto) {
    $valor = [Environment]::GetEnvironmentVariable($Nombre)
    if ([string]::IsNullOrWhiteSpace($valor)) { return $PorDefecto }
    return $valor
}

# --- SAP TM OData ---
$sapUrl      = Variable-Requerida 'SAP_TM_URL'              # ej. http://sap-tm-prd.boterosoto.local:8000/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV/RentabClienteSet
$sapUsuario  = Variable-Requerida 'SAP_TM_USER'
$sapClave    = Variable-Requerida 'SAP_TM_PASSWORD'

# --- DW DWCubosOperacionesEBS (NIT por BP, tabla dbo.DimBPSolicitante). Host/puerto/usuario ya
#     confirmados (10.10.40.24, 1433, consultascubos) y quedan de valor por defecto; solo la clave
#     es obligatoria por variable de entorno (la coloca Datos, nunca en este archivo). ---
$dwServer    = Variable-Opcional  'NEXO_DW_SQL_SERVER' '10.10.40.24'
$dwPort      = Variable-Opcional  'NEXO_DW_SQL_PORT' '1433'
$dwDatabase  = Variable-Opcional  'NEXO_DW_SQL_DATABASE' 'DWCubosOperacionesEBS'
$dwUsuario   = Variable-Opcional  'NEXO_DW_SQL_USER' 'consultascubos'
$dwClave     = Variable-Requerida 'NEXO_DW_SQL_PASSWORD'

# --- Base destino (Cotifly). Login SQL (no autenticación de Windows), igual criterio que usa el
#     propio backend para ConnectionStrings:Cotifly (clsOpcionesConexiones.cs: "siempre SQL login,
#     nunca autenticación de Windows") — consistente con las otras dos conexiones de este script. ---
$destinoServer   = Variable-Opcional  'COTIFLY_SQL_SERVER' '.'
$destinoDatabase = Variable-Requerida 'COTIFLY_SQL_DATABASE'
$destinoUsuario  = Variable-Requerida 'COTIFLY_SQL_USER'
$destinoClave    = Variable-Requerida 'COTIFLY_SQL_PASSWORD'
$destinoCs = "Server=$destinoServer;Database=$destinoDatabase;User Id=$destinoUsuario;Password=$destinoClave;TrustServerCertificate=True;Connection Timeout=30"

# --- Cada cuánto sincroniza de verdad, sin tocar el horario del SQL Agent Job. El job de SQL
#     Agent se programa para INTENTAR correr seguido (cada hora); este script decide si ya le
#     tocaba o no, comparando contra la última corrida 'ok' del log. Cambiar el intervalo es
#     solo cambiar esta variable de entorno, sin tocar ni recrear el job de SQL Agent. ---
$intervaloMinutos = [double](Variable-Opcional 'SYNC_INTERVAL_MINUTOS' '1440')

function Registrar-FalloManual([string]$Mensaje) {
    try {
        $cs = New-Object System.Data.SqlClient.SqlConnection($destinoCs)
        $cs.Open()
        $cmd = $cs.CreateCommand()
        $cmd.CommandText = @"
INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError)
VALUES (SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), N'error', @mensaje);
"@
        $p = $cmd.Parameters.Add('@mensaje', [System.Data.SqlDbType]::NVarChar, 2000)
        $p.Value = $Mensaje.Substring(0, [Math]::Min(2000, $Mensaje.Length))
        $cmd.ExecuteNonQuery() | Out-Null
        $cs.Close()
    } catch {
        # Best-effort, igual que el worker_ciclo de EmbarQA: si ni esto funciona, no hay más que hacer
        # desde este script. Queda en el log de salida del paso del Agent Job.
        Write-Error "No se pudo registrar el fallo en SapMargenSyncLog: $_"
    }
}

function Obtener-UltimaCorridaOkAt() {
    $cs = New-Object System.Data.SqlClient.SqlConnection($destinoCs)
    $cs.Open()
    try {
        $cmd = $cs.CreateCommand()
        $cmd.CommandText = "SELECT TOP (1) finalizadoAt FROM dbo_cotifly.SapMargenSyncLog WHERE status = N'ok' ORDER BY iniciadoAt DESC;"
        $valor = $cmd.ExecuteScalar()
        if ($null -eq $valor -or $valor -is [DBNull]) { return $null }
        return [DateTimeOffset]$valor
    } finally {
        $cs.Close()
    }
}

# 0) ¿Ya le toca? Si la última corrida exitosa fue hace menos de $intervaloMinutos, no hace nada
#    (no cuenta como 'omitido' en el log: es la cadencia normal del job, no un conflicto de corridas).
$ultimaOkAt = Obtener-UltimaCorridaOkAt
if ($null -ne $ultimaOkAt -and ((Get-Date) - $ultimaOkAt.LocalDateTime).TotalMinutes -lt $intervaloMinutos) {
    Write-Output "Todavía no toca sincronizar (última corrida ok: $ultimaOkAt; intervalo configurado: $intervaloMinutos min). Saliendo sin hacer nada."
    exit 0
}

try {
    # 1) SAP TM: set completo, sin filtro.
    $headerAuth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${sapUsuario}:${sapClave}"))
    $respuestaSap = Invoke-RestMethod -Uri "$sapUrl`?`$format=json" -Headers @{ Authorization = "Basic $headerAuth" } -Method Get -TimeoutSec 120
    $filasSap = $respuestaSap.d.results
    if (-not $filasSap -or $filasSap.Count -eq 0) {
        throw "SAP TM devolvió 0 registros; no se sincroniza nada (posible respuesta inesperada, revisar antes de reintentar)."
    }

    # 2) DW DWCubosOperacionesEBS: NIT por BP, dbo.DimBPSolicitante (BP, NIT confirmados por pantallazo
    #    de SSMS 2026-10-28). En lotes de 500 y parametrizado (mismo límite y criterio que
    #    clsDirectorioEjecutivosDimEjecutivo.cs: SQL Server admite 2100 parámetros, se deja margen).
    $tamanoLote = 500
    $bps = ($filasSap.OrderPartyId | Sort-Object -Unique)
    $nitPorBp = @{}
    $dwCs = "Server=$dwServer,$dwPort;Database=$dwDatabase;User Id=$dwUsuario;Password=$dwClave;Encrypt=False;TrustServerCertificate=True;Connection Timeout=30"
    $cnDw = New-Object System.Data.SqlClient.SqlConnection($dwCs)
    $cnDw.Open()
    try {
        for ($i = 0; $i -lt $bps.Count; $i += $tamanoLote) {
            $lote = $bps[$i..([Math]::Min($i + $tamanoLote, $bps.Count) - 1)]
            $cmdDw = $cnDw.CreateCommand()
            $nombresParametro = for ($j = 0; $j -lt $lote.Count; $j++) {
                $nombre = "@bp$j"
                [void]$cmdDw.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter($nombre, [System.Data.SqlDbType]::NVarChar, 20)))
                $cmdDw.Parameters[$nombre].Value = $lote[$j]
                $nombre
            }
            $cmdDw.CommandText = "SELECT BP, NIT FROM dbo.DimBPSolicitante WHERE BP IN ($($nombresParametro -join ','))"
            $lector = $cmdDw.ExecuteReader()
            while ($lector.Read()) {
                $nitPorBp[$lector['BP'].ToString()] = $lector['NIT'].ToString()
            }
            $lector.Close()
        }
    } finally {
        $cnDw.Close()
    }

    # 3) Volcar a una tabla temporal #SapMargenStage en la base destino (crear + bulk insert; no es una
    #    tabla fija del esquema, ver detalle junto a su creación más abajo).
    $tabla = New-Object System.Data.DataTable
    [void]$tabla.Columns.Add('salesOrgId', [string])
    [void]$tabla.Columns.Add('trqType', [string])
    [void]$tabla.Columns.Add('bpSap', [string])
    [void]$tabla.Columns.Add('nit', [string])
    [void]$tabla.Columns.Add('margenRecomendado', [decimal])
    foreach ($fila in $filasSap) {
        $nit = $nitPorBp[$fila.OrderPartyId]
        $renglon = $tabla.NewRow()
        $renglon['salesOrgId'] = $fila.SalesOrgId
        $renglon['trqType'] = $fila.TrqType
        $renglon['bpSap'] = $fila.OrderPartyId
        $renglon['nit'] = if ($nit) { $nit } else { [DBNull]::Value }
        $renglon['margenRecomendado'] = [decimal]$fila.Value
        $tabla.Rows.Add($renglon)
    }

    $cnDestino = New-Object System.Data.SqlClient.SqlConnection($destinoCs)
    $cnDestino.Open()
    try {
        # #SapMargenStage es una tabla temporal LOCAL de esta conexión: no es un objeto fijo del esquema, se
        # crea en cada corrida y desaparece sola al cerrar $cnDestino. El SP de abajo la ve porque se ejecuta
        # en esta misma conexión/sesión (por eso bulk copy y EXEC tienen que compartir $cnDestino).
        $cmdCrearStage = $cnDestino.CreateCommand()
        # COLLATE DATABASE_DEFAULT en las columnas de texto: una tabla temporal (#...) nace con el collation
        # por defecto del SERVIDOR (tempdb), no el de esta base — si esta base tiene un collation distinto
        # (aquí, Modern_Spanish_CI_AS vs. el del servidor), el MERGE del SP falla con "collation conflict"
        # al comparar contra dbo_cotifly.SapMargenCliente. Con DATABASE_DEFAULT quedan iguales.
        $cmdCrearStage.CommandText = @"
CREATE TABLE #SapMargenStage
(
    salesOrgId        NVARCHAR(20)  COLLATE DATABASE_DEFAULT NOT NULL,
    trqType           NVARCHAR(8)   COLLATE DATABASE_DEFAULT NOT NULL,
    bpSap             NVARCHAR(20)  COLLATE DATABASE_DEFAULT NOT NULL,
    nit               NVARCHAR(20)  COLLATE DATABASE_DEFAULT NULL,
    margenRecomendado DECIMAL(9, 4) NOT NULL,
    CONSTRAINT pk_sapMargenStageTemp PRIMARY KEY (salesOrgId, trqType, bpSap)
);
"@
        $cmdCrearStage.ExecuteNonQuery() | Out-Null

        $bulk = New-Object System.Data.SqlClient.SqlBulkCopy($cnDestino)
        $bulk.DestinationTableName = '#SapMargenStage'
        $bulk.WriteToServer($tabla)

        # 4) El stored procedure hace el upsert real + log, en su propia transacción.
        $cmdSp = $cnDestino.CreateCommand()
        $cmdSp.CommandText = 'EXEC dbo_cotifly.sp_sincronizar_margen_sap;'
        $cmdSp.CommandTimeout = 120
        $cmdSp.ExecuteNonQuery() | Out-Null
    } finally {
        $cnDestino.Close()
    }

    Write-Output "Sincronización de margen SAP: $($tabla.Rows.Count) filas procesadas."
} catch {
    Registrar-FalloManual "$_"
    throw
}
