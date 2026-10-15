using System.Collections.Concurrent;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// HU-023 (#176): ¿la base admite el motivo <c>BUSQUEDA_AGOTADA</c>? Lo autodetecta leyendo la definición de
/// <c>ck_cotizacionLineasSolicitud_motivoSinSeleccion</c> en <c>sys.check_constraints</c> (el script <c>db/scripts/07</c> la reemplaza
/// por una con los 5 motivos). La respuesta se guarda por cadena de conexión durante <see cref="Vigencia"/>: así un ambiente queda bien
/// en cuanto el DBA corre el 07, sin configurar nada ni reiniciar. Si la consulta falla, se asume que NO lo admite (lo seguro: el
/// respaldo guarda NULL y un aviso, nunca rompe el guardado). Singleton.
/// </summary>
public sealed class clsDetectorMotivosBd(IClsReloj reloj, ILogger<clsDetectorMotivosBd> logger)
{
    /// <summary>Tiempo que se reutiliza la detección de una base.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(5);

    /// <summary>Consulta de la detección: 1 si el CHECK del motivo admite <c>BUSQUEDA_AGOTADA</c>.</summary>
    public const string Consulta =
        "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion' "
        + "AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud') AND definition LIKE N'%BUSQUEDA[_]AGOTADA%') THEN 1 ELSE 0 END";

    private readonly ConcurrentDictionary<string, (bool Admite, DateTimeOffset VenceAt)> _detecciones = new(StringComparer.Ordinal);

    /// <summary>¿El CHECK de la base de <paramref name="cadena"/> admite <c>BUSQUEDA_AGOTADA</c>? (el script 07 está aplicado).</summary>
    public async Task<bool> AdmiteBusquedaAgotadaAsync(string cadena, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cadena);
        if (_detecciones.TryGetValue(cadena, out var guardada) && reloj.AhoraUtc < guardada.VenceAt)
        {
            return guardada.Admite;
        }

        bool admite;
        try
        {
            await using var conexion = new SqlConnection(cadena);
            await conexion.OpenAsync(cancellationToken);
            await using var comando = new SqlCommand(Consulta, conexion);
            admite = Convert.ToInt32(await comando.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or InvalidCastException)
        {
            logger.LogWarning("No se pudo leer el CHECK del motivo de 'sin selección' ({Error}): se asume sin el script 07.", ex.GetType().Name);
            return false;
        }

        if (!admite)
        {
            logger.LogWarning(
                "MOTIVOS_BD: la base no tiene db/scripts/07_motivo_busqueda_agotada.sql; BUSQUEDA_AGOTADA se guarda en NULL con un aviso en la ruta.");
        }

        _detecciones[cadena] = (admite, reloj.AhoraUtc + Vigencia);
        return admite;
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
