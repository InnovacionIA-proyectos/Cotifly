using System.Collections.Concurrent;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// HU-023 (#176): lo que se sabe del script <c>07</c> en una base, según <c>sys.check_constraints</c>.
/// </summary>
public enum EstadoScript07
{
    /// <summary>El CHECK del motivo no admite <c>BUSQUEDA_AGOTADA</c> (o no existe): falta el 07.</summary>
    NoAplicado = 0,

    /// <summary>El CHECK del motivo admite <c>BUSQUEDA_AGOTADA</c>.</summary>
    Aplicado = 1,

    /// <summary>
    /// No se puede verificar: el login no ve la definición del CHECK (<c>definition</c> llega NULL o no tiene <c>VIEW DEFINITION</c>
    /// sobre la tabla). Se trata como <see cref="NoAplicado"/> para guardar (lo seguro), pero la sonda lo dice distinto.
    /// </summary>
    NoVerificable = 2,
}

/// <summary>
/// HU-023 (#176): ¿la base admite el motivo <c>BUSQUEDA_AGOTADA</c>? Según <c>BaseDatos:MotivoBusquedaAgotada</c>
/// (<see cref="clsOpcionesBaseDatos"/>): <c>habilitado</c> o <c>deshabilitado</c> lo fijan sin consultar; <c>auto</c> (por defecto) lo
/// detecta leyendo la definición de <c>ck_cotizacionLineasSolicitud_motivoSinSeleccion</c> en <c>sys.check_constraints</c> (el script
/// <c>db/scripts/07</c> la reemplaza por una con los 5 motivos). Sin <c>VIEW DEFINITION</c> la definición llega NULL: eso es
/// <see cref="EstadoScript07.NoVerificable"/>, no "falta el 07" (auditoría A2). La detección se guarda por cadena de conexión durante
/// <see cref="Vigencia"/>: así un ambiente queda bien en cuanto el DBA corre el 07, sin reiniciar. Si la consulta falla o no se puede
/// verificar, se asume que NO lo admite (lo seguro: el respaldo guarda NULL y un aviso, nunca rompe el guardado). Singleton.
/// </summary>
public sealed class clsDetectorMotivosBd(IClsReloj reloj, IOptions<clsOpcionesBaseDatos> opciones, ILogger<clsDetectorMotivosBd> logger)
{
    /// <summary>Tiempo que se reutiliza la detección de una base.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(5);

    private const string FiltroCheck =
        "FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion' "
        + "AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud')";

    /// <summary>
    /// Consulta de la detección (<see cref="EstadoScript07"/> como entero): 1 si el CHECK admite <c>BUSQUEDA_AGOTADA</c>; 2 si existe pero
    /// su definición no es visible (NULL) o el login no tiene <c>VIEW DEFINITION</c> sobre la tabla; 0 en otro caso.
    /// </summary>
    public const string Consulta =
        "SELECT CASE WHEN EXISTS (SELECT 1 " + FiltroCheck + " AND definition LIKE N'%BUSQUEDA[_]AGOTADA%') THEN 1 "
        + "WHEN EXISTS (SELECT 1 " + FiltroCheck + " AND definition IS NULL) "
        + "OR ISNULL(HAS_PERMS_BY_NAME(N'dbo_cotifly.CotizacionLineasSolicitud', N'OBJECT', N'VIEW DEFINITION'), 0) = 0 THEN 2 "
        + "ELSE 0 END";

    private readonly ConcurrentDictionary<string, (EstadoScript07 Estado, DateTimeOffset VenceAt)> _detecciones = new(StringComparer.Ordinal);

    /// <summary>El valor de <see cref="Consulta"/> como <see cref="EstadoScript07"/> (un valor desconocido no se puede verificar).</summary>
    public static EstadoScript07 Interpretar(int valor) => valor switch
    {
        1 => EstadoScript07.Aplicado,
        0 => EstadoScript07.NoAplicado,
        _ => EstadoScript07.NoVerificable,
    };

    /// <summary>
    /// ¿Se guarda <c>BUSQUEDA_AGOTADA</c> tal cual en la base de <paramref name="cadena"/>? Con <c>habilitado</c> o <c>deshabilitado</c>
    /// no consulta la base; con <c>auto</c>, solo si <see cref="DetectarAsync"/> dice <see cref="EstadoScript07.Aplicado"/>.
    /// </summary>
    public async Task<bool> AdmiteBusquedaAgotadaAsync(string cadena, CancellationToken cancellationToken = default)
    {
        switch (opciones.Value.MotivoBusquedaAgotadaNormalizado)
        {
            case clsOpcionesBaseDatos.ModoHabilitado:
                return true;
            case clsOpcionesBaseDatos.ModoDeshabilitado:
                return false;
            default:
                return await DetectarAsync(cadena, cancellationToken) == EstadoScript07.Aplicado;
        }
    }

    /// <summary>Estado del script 07 en la base de <paramref name="cadena"/> (con caché de <see cref="Vigencia"/>).</summary>
    public async Task<EstadoScript07> DetectarAsync(string cadena, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cadena);
        if (_detecciones.TryGetValue(cadena, out var guardada) && reloj.AhoraUtc < guardada.VenceAt)
        {
            return guardada.Estado;
        }

        EstadoScript07 estado;
        try
        {
            await using var conexion = new SqlConnection(cadena);
            await conexion.OpenAsync(cancellationToken);
            await using var comando = new SqlCommand(Consulta, conexion);
            estado = Interpretar(Convert.ToInt32(await comando.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or InvalidCastException)
        {
            logger.LogWarning("No se pudo leer el CHECK del motivo de 'sin selección' ({Error}): se asume sin el script 07.", ex.GetType().Name);
            return EstadoScript07.NoAplicado;
        }

        if (estado == EstadoScript07.NoAplicado)
        {
            logger.LogWarning(
                "MOTIVOS_BD: la base no tiene db/scripts/07_motivo_busqueda_agotada.sql; BUSQUEDA_AGOTADA se guarda en NULL con un aviso en la ruta.");
        }
        else if (estado == EstadoScript07.NoVerificable)
        {
            logger.LogWarning(
                "MOTIVOS_BD: no se puede verificar el script 07 (el login no tiene VIEW DEFINITION sobre dbo_cotifly.CotizacionLineasSolicitud); "
                + "BUSQUEDA_AGOTADA se guarda en NULL con un aviso en la ruta. Otorgue VIEW DEFINITION o configure BaseDatos__MotivoBusquedaAgotada=habilitado.");
        }

        _detecciones[cadena] = (estado, reloj.AhoraUtc + Vigencia);
        return estado;
    }

    /// <summary>Olvida las detecciones (pruebas, o tras aplicar el 07 sin esperar la vigencia).</summary>
    public void Olvidar() => _detecciones.Clear();
}

/// <summary>
/// HU-023 (#176), opción A con autodetección: antes de guardar, una ruta cuyo <c>motivoSinSeleccion</c> no está en el <c>CHECK</c> del 02
/// (hoy solo <c>BUSQUEDA_AGOTADA</c>) se guarda tal cual si la base tiene el script 07 (<see cref="clsDetectorMotivosBd"/>); si no, el
/// motivo va en <c>NULL</c> y el valor real queda como advertencia visible de la ruta (<see cref="clsLineaSolicitud.DegradarMotivoSinSeleccion"/>).
/// Solo consulta la base cuando hay un motivo nuevo que guardar.
/// </summary>
public sealed class clsMotivoSinSeleccionInterceptor(clsDetectorMotivosBd detector) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        AplicarAsync(eventData.Context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await AplicarAsync(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private async ValueTask AplicarAsync(DbContext? contexto, CancellationToken cancellationToken)
    {
        if (contexto is null)
        {
            return;
        }

        var nuevas = contexto.ChangeTracker.Entries<clsLineaSolicitud>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified
                        && e.Entity.MotivoSinSeleccion is { } motivo && !clsMotivosSinSeleccion.DelScript02.Contains(motivo))
            .Select(e => e.Entity)
            .ToList();
        if (nuevas.Count == 0 || !contexto.Database.IsSqlServer() || contexto.Database.GetConnectionString() is not { Length: > 0 } cadena)
        {
            return;
        }

        if (await detector.AdmiteBusquedaAgotadaAsync(cadena, cancellationToken))
        {
            return;
        }

        foreach (var ruta in nuevas)
        {
            ruta.DegradarMotivoSinSeleccion(clsMotivosSinSeleccion.Mensaje(ruta.MotivoSinSeleccion));
        }
    }
}
